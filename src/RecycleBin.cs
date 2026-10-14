using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
        private readonly Func<string, int> empty;
        public RecycleBinService() : this(NativeRecycleBin.Query, NativeRecycleBin.Empty) { }
        internal RecycleBinService(Func<string, RecycleBinInfo> reader, Func<string, int> cleaner) { query = reader; empty = cleaner; }
        public RecycleBinInfo Query()
        {
            var info = query(AllDrivesRoot);
            if (info == null || info.Root != AllDrivesRoot) throw new IOException("Thông tin Thùng rác chưa bao gồm tất cả ổ đĩa.");
            return info;
        }
        public Task<RecycleBinInfo> QueryAsync() { return OnSta(Query); }
        public Task<RecycleBinResult> EmptyAsync(RecycleBinInfo approved, string logDirectory) { return OnSta(() => EmptyApproved(approved, logDirectory)); }
        internal RecycleBinResult EmptyApproved(RecycleBinInfo approved, string logDirectory)
        {
            var result = new RecycleBinResult();
            try
            {
                if (approved == null || approved.Root != AllDrivesRoot) throw new ArgumentException("Cần xác nhận Thùng rác trên tất cả ổ đĩa trước khi dọn.");
                result.Before = Query();
                if (result.Before.Count == 0) { result.After = result.Before; result.Completed = true; return result; }
                if (result.Before.Count != approved.Count || result.Before.Bytes != approved.Bytes) throw new IOException("Thùng rác đã thay đổi sau khi xác nhận. Làm mới và xác nhận lại trước khi dọn.");
                Directory.CreateDirectory(logDirectory);
                result.LogPath = Path.Combine(logDirectory, "recycle-bin-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".log");
                using (var stream = new FileStream(result.LogPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(true)))
                {
                    writer.AutoFlush = true;
                    writer.WriteLine(DateTime.UtcNow.ToString("o") + " BEGIN scope=all-drives; items=" + result.Before.Count + "; bytes=" + result.Before.Bytes);
                    stream.Flush(true);
                    result.Attempted = true;
                    int hr = empty(AllDrivesRoot);
                    // A failed shell operation can still have deleted some items. Always refresh and report remaining data.
                    try { result.After = Query(); }
                    catch (Exception e) { result.Error = "Windows đã trả kết quả nhưng chưa đọc lại được Thùng rác: " + e.Message; }
                    if (hr < 0) result.Error = "Windows không hoàn tất việc dọn Thùng rác (HRESULT 0x" + hr.ToString("X8") + "). Có thể một phần dữ liệu đã được xóa.";
                    else if (result.After != null && result.After.Count > 0) result.Error = "Thùng rác vẫn còn " + result.After.Count.ToString("N0") + " mục. Có thể dữ liệu mới được thêm hoặc không xóa được.";
                    result.Completed = hr >= 0 && result.After != null && result.After.Count == 0;
                    writer.WriteLine(DateTime.UtcNow.ToString("o") + " END hresult=0x" + hr.ToString("X8") + "; remaining_items=" + (result.After == null ? "unknown" : result.After.Count.ToString()) + "; remaining_bytes=" + (result.After == null ? "unknown" : result.After.Bytes.ToString()) + "; completed=" + result.Completed);
                    if (result.Error != null) writer.WriteLine("ERROR " + result.Error);
                    stream.Flush(true);
                }
            }
            catch (Exception e) { result.Error = e.Message; result.Completed = false; }
            return result;
        }
        private static Task<T> OnSta<T>(Func<T> action)
        {
            var completion = new TaskCompletionSource<T>();
            var thread = new Thread(() => { try { completion.SetResult(action()); } catch (Exception e) { completion.SetException(e); } });
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
        [DllImport("shell32.dll", EntryPoint = "SHEmptyRecycleBinW", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int EmptyBin(IntPtr owner, string root, uint flags);
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
        public static int Empty(string root)
        {
            ValidateRoot(root); int initialized = CoInitializeEx(IntPtr.Zero, 2); Marshal.ThrowExceptionForHR(initialized);
            try { return EmptyBin(IntPtr.Zero, root, 7); } // Confirmation is supplied by the application; no shell progress/sound.
            finally { CoUninitialize(); }
        }
    }
}
