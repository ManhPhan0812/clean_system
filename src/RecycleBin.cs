using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace CleanSystem
{
    internal sealed class RecycleBinInfo
    {
        public string Root { get; private set; }
        public long Bytes { get; private set; }
        public long Count { get; private set; }
        public RecycleBinInfo(long bytes, long count, string root = RecycleBinService.AllDrivesRoot)
        {
            if (bytes < 0 || count < 0) throw new ArgumentOutOfRangeException("bytes", "Thông tin Thùng rác không hợp lệ.");
            Bytes = bytes; Count = count; Root = root;
        }
    }
    internal sealed class RecycleBinResult
    {
        public bool Attempted;
        public bool Completed;
        public bool Finished;
        public int Deleted;
        public int Skipped;
        public RecycleBinInfo Before;
        public RecycleBinInfo After;
        public string LogPath;
        public string Error;
    }
    internal sealed class RecycleBinService
    {
        // A null root tells the Windows Shell to query/empty Recycle Bins on all drives.
        public const string AllDrivesRoot = null;
        private readonly Func<string, RecycleBinInfo> query;
        private readonly IRecycleBinBackend backend;
        public RecycleBinService() : this(new ShellRecycleBinBackend()) { }
        internal RecycleBinService(IRecycleBinBackend operations) { backend = operations; query = backend.Query; }
        public RecycleBinInfo Query()
        {
            var info = query(AllDrivesRoot);
            if (info == null || info.Root != AllDrivesRoot) throw new IOException("Thông tin Thùng rác chưa bao gồm tất cả ổ đĩa.");
            return info;
        }
        public Task<RecycleBinInfo> QueryAsync() { return OnSta(Query); }
        public Task<RecycleBinResult> EmptyAsync(RecycleBinInfo approved, string logDirectory, Func<RecycleBinItem, bool> confirmPermission = null, Action<RecycleBinProgress> progress = null, IntPtr owner = default(IntPtr))
        { return OnSta(() => EmptyApproved(approved, logDirectory, confirmPermission, progress, owner)); }
        internal RecycleBinResult EmptyApproved(RecycleBinInfo approved, string logDirectory, Func<RecycleBinItem, bool> confirmPermission = null, Action<RecycleBinProgress> progress = null, IntPtr owner = default(IntPtr))
        {
            var result = new RecycleBinResult();
            IList<RecycleBinItem> items = null;
            try
            {
                if (approved == null || approved.Root != AllDrivesRoot) throw new ArgumentException("Cần xác nhận Thùng rác trên tất cả ổ đĩa trước khi dọn.");
                result.Before = Query();
                if (result.Before.Count == 0) { result.After = result.Before; result.Completed = result.Finished = true; return result; }
                if (result.Before.Count != approved.Count || result.Before.Bytes != approved.Bytes) throw new IOException("Thùng rác đã thay đổi sau khi xác nhận. Làm mới và xác nhận lại trước khi dọn.");
                Directory.CreateDirectory(logDirectory);
                result.LogPath = Path.Combine(logDirectory, "recycle-bin-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".log");
                using (var stream = new FileStream(result.LogPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(true)))
                {
                    writer.AutoFlush = true;
                    writer.WriteLine(DateTime.UtcNow.ToString("o") + " BEGIN scope=all-drives; items=" + result.Before.Count + "; bytes=" + result.Before.Bytes);
                    stream.Flush(true);
                    // Snapshot Shell identities before deleting: new items are never swept into this run.
                    items = backend.Snapshot();
                    result.Attempted = true;
                    foreach (var item in items)
                    {
                        writer.WriteLine(DateTime.UtcNow.ToString("o") + " ATTEMPT " + item.Name.Replace('\r', ' ').Replace('\n', ' '));
                        stream.Flush(true);
                        if (progress != null) progress(new RecycleBinProgress(result.Deleted, result.Skipped, items.Count, item.Name));
                        var outcome = backend.Delete(item, false, owner);
                        if (outcome.NeedsPermission && confirmPermission != null)
                        {
                            bool allowed = confirmPermission(item);
                            writer.WriteLine(DateTime.UtcNow.ToString("o") + " PERMISSION allowed=" + allowed);
                            stream.Flush(true);
                            if (allowed) outcome = backend.Delete(item, true, owner);
                            else outcome = new RecycleDeleteOutcome(false, false, "Người dùng chọn Không; giữ mục này.");
                        }
                        if (outcome.Deleted) result.Deleted++; else result.Skipped++;
                        writer.WriteLine(DateTime.UtcNow.ToString("o") + (outcome.Deleted ? " DELETED " : " SKIPPED ") + item.Name.Replace('\r', ' ').Replace('\n', ' ') + "; " + outcome.Detail);
                        stream.Flush(true);
                        if (progress != null) progress(new RecycleBinProgress(result.Deleted, result.Skipped, items.Count, null));
                    }
                    result.Finished = true;
                    try { result.After = Query(); }
                    catch (Exception e) { result.Error = "Windows đã trả kết quả nhưng chưa đọc lại được Thùng rác: " + e.Message; }
                    result.Completed = result.Error == null && result.After != null && result.After.Count == 0;
                    writer.WriteLine(DateTime.UtcNow.ToString("o") + " END deleted=" + result.Deleted + "; skipped=" + result.Skipped + "; remaining_items=" + (result.After == null ? "unknown" : result.After.Count.ToString()) + "; remaining_bytes=" + (result.After == null ? "unknown" : result.After.Bytes.ToString()) + "; completed=" + result.Completed);
                    if (result.Error != null) writer.WriteLine("ERROR " + result.Error);
                    stream.Flush(true);
                }
            }
            catch (Exception e) {
                result.Error = e.Message; result.Completed = false;
                if (result.Attempted && result.After == null) try { result.After = Query(); } catch { }
            }
            finally { if (items != null) foreach (var item in items) item.Dispose(); }
            return result;
        }
        private static Task<T> OnSta<T>(Func<T> action)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            // Shell dialogs and COM callbacks need an STA with a live message loop.
            var thread = new Thread(() => {
                try {
                    var dispatcher = Dispatcher.CurrentDispatcher;
                    dispatcher.BeginInvoke(new Action(() => {
                        try { completion.TrySetResult(action()); } catch (Exception e) { completion.TrySetException(e); }
                        finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
                    }));
                    Dispatcher.Run();
                } catch (Exception e) { completion.TrySetException(e); }
            });
            thread.IsBackground = true; thread.SetApartmentState(ApartmentState.STA); thread.Start(); return completion.Task;
        }
    }
    internal static class NativeRecycleBin
    {
        // shellapi.h uses pack(1) on Win32 and normal alignment on Win64.
        [StructLayout(LayoutKind.Sequential)] private struct Info64 { public uint Size; public long Bytes, Count; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct Info32 { public uint Size; public long Bytes, Count; }
        [DllImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int Query64(string root, ref Info64 info);
        [DllImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int Query32(string root, ref Info32 info);
        [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint mode);
        [DllImport("ole32.dll")] private static extern void CoUninitialize();
        private static void ValidateRoot(string root) { if (root != RecycleBinService.AllDrivesRoot) throw new ArgumentException("Thao tác Thùng rác phải áp dụng cho tất cả ổ đĩa."); }
        public static RecycleBinInfo Query(string root)
        {
            ValidateRoot(root); int initialized = CoInitializeEx(IntPtr.Zero, 2); Marshal.ThrowExceptionForHR(initialized);
            try
            {
                if (IntPtr.Size == 8)
                {
                    var info = new Info64 { Size = (uint)Marshal.SizeOf(typeof(Info64)) }; Marshal.ThrowExceptionForHR(Query64(root, ref info));
                    return new RecycleBinInfo(info.Bytes, info.Count, root);
                }
                else
                {
                    var info = new Info32 { Size = (uint)Marshal.SizeOf(typeof(Info32)) }; Marshal.ThrowExceptionForHR(Query32(root, ref info));
                    return new RecycleBinInfo(info.Bytes, info.Count, root);
                }
            }
            finally { CoUninitialize(); }
        }
    }
}
