using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CleanSystem
{
    internal sealed class RecycleBinItem : IDisposable
    {
        public string Name { get; private set; }
        internal IShellItem ShellItem { get; private set; }
        internal RecycleBinItem(string name, IShellItem item = null) { Name = name; ShellItem = item; }
        public void Dispose() { if (ShellItem != null) { Marshal.ReleaseComObject(ShellItem); ShellItem = null; } }
    }
    internal sealed class RecycleBinProgress
    {
        public int Deleted, Skipped, Total;
        public string Current;
        public RecycleBinProgress(int deleted, int skipped, int total, string current) { Deleted = deleted; Skipped = skipped; Total = total; Current = current; }
    }
    internal sealed class RecycleDeleteOutcome
    {
        public bool Deleted, NeedsPermission;
        public string Detail;
        public RecycleDeleteOutcome(bool deleted, bool permission, string detail) { Deleted = deleted; NeedsPermission = permission; Detail = detail; }
    }
    internal interface IRecycleBinBackend
    {
        RecycleBinInfo Query(string root);
        IList<RecycleBinItem> Snapshot();
        RecycleDeleteOutcome Delete(RecycleBinItem item, bool allowElevation, IntPtr owner);
    }
    internal sealed class ShellRecycleBinBackend : IRecycleBinBackend
    {
        internal const string RecycleNamespace = "shell:::{645FF040-5081-101B-9F08-00AA002F954E}";
        public RecycleBinInfo Query(string root) { return NativeRecycleBin.Query(root); }
        public IList<RecycleBinItem> Snapshot()
        {
            var items = new List<RecycleBinItem>(); IShellItem folder = null; object enumeration = null;
            try
            {
                folder = NativeShellDelete.OpenItem(RecycleNamespace);
                var handler = new Guid("94f60519-2850-4924-aa5a-d15e84868039");
                var iid = typeof(IEnumShellItems).GUID;
                Marshal.ThrowExceptionForHR(folder.BindToHandler(IntPtr.Zero, ref handler, ref iid, out enumeration));
                var iterator = (IEnumShellItems)enumeration;
                for (;;)
                {
                    IShellItem item; uint fetched;
                    int hr = iterator.Next(1, out item, out fetched);
                    Marshal.ThrowExceptionForHR(hr);
                    if (fetched == 0) break;
                    try { items.Add(new RecycleBinItem(NativeShellDelete.DisplayName(item), item)); }
                    catch { Marshal.ReleaseComObject(item); throw; }
                }
                return items;
            }
            catch { foreach (var item in items) item.Dispose(); throw; }
            finally {
                if (enumeration != null) Marshal.ReleaseComObject(enumeration);
                if (folder != null) Marshal.ReleaseComObject(folder);
            }
        }
        public RecycleDeleteOutcome Delete(RecycleBinItem item, bool allowElevation, IntPtr owner)
        { return NativeShellDelete.DeleteOne(item.ShellItem, allowElevation, owner); }
    }
    internal static class NativeShellDelete
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(string name, IntPtr context, ref Guid iid, out IShellItem item);
        internal static IShellItem OpenItem(string name)
        {
            IShellItem item; var iid = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(name, IntPtr.Zero, ref iid, out item)); return item;
        }
        internal static string DisplayName(IShellItem item)
        {
            IntPtr name; Marshal.ThrowExceptionForHR(item.GetDisplayName(0, out name));
            try { return Marshal.PtrToStringUni(name); } finally { Marshal.FreeCoTaskMem(name); }
        }
        internal static RecycleDeleteOutcome DeleteOne(IShellItem item, bool allowElevation, IntPtr owner)
        {
            if (item == null) throw new ArgumentNullException("item");
            IFileOperation operation = null;
            try
            {
                operation = (IFileOperation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("3ad05575-8857-4850-9277-11b85bdb8e09")));
                // Application confirmation covers permanent deletion. Error UI is suppressed so
                // access failures return to our Yes/No dialog instead of blocking the whole bin.
                // Elevation is permitted only after the user explicitly selects Yes for this item.
                uint flags = 0x0004 | 0x0010 | 0x0400; // SILENT, NOCONFIRMATION, NOERRORUI
                if (allowElevation) flags |= 0x00040000; // FOFX_SHOWELEVATIONPROMPT
                Marshal.ThrowExceptionForHR(operation.SetOperationFlags(flags));
                Marshal.ThrowExceptionForHR(operation.SetOwnerWindow(owner));
                var sink = new ShellDeleteSink();
                Marshal.ThrowExceptionForHR(operation.DeleteItem(item, sink));
                int hr = operation.PerformOperations();
                bool aborted;
                Marshal.ThrowExceptionForHR(operation.GetAnyOperationsAborted(out aborted));
                return Classify(hr, aborted, sink.SawDeletion, sink.UserSkipped, sink.NeedsPermission, sink.LastProblem);
            }
            catch (COMException e) { return Classify(e.ErrorCode, true, false, true, RequiresPermission(e.ErrorCode), e.ErrorCode); }
            finally { if (operation != null) Marshal.ReleaseComObject(operation); }
        }
        internal static RecycleDeleteOutcome Classify(int hr, bool aborted, bool sawDeletion, bool problem, bool permission, int detail)
        {
            bool deleted = hr >= 0 && !aborted && sawDeletion && !problem;
            return new RecycleDeleteOutcome(deleted, !deleted && (permission || RequiresPermission(hr)),
                deleted ? "Đã xóa." : "Windows giữ lại mục này (HRESULT 0x" + (detail != 0 ? detail : hr).ToString("X8") + ").");
        }
        internal static bool RequiresPermission(int hr)
        {
            return hr == unchecked((int)0x80270002) || hr == unchecked((int)0x80070005) ||
                hr == unchecked((int)0x80070522) || hr == unchecked((int)0x800702E4) ||
                hr == unchecked((int)0x80270021) || hr == unchecked((int)0x80270022) || hr == unchecked((int)0x8027003F);
        }
    }

    // Signatures and vtable order follow the Windows SDK ShObjIdl_core.h.
    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr context, ref Guid handler, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object result);
        [PreserveSig] int GetParent(out IShellItem parent);
        [PreserveSig] int GetDisplayName(uint type, out IntPtr name);
        [PreserveSig] int GetAttributes(uint mask, out uint attributes);
        [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
    }
    [ComImport, Guid("70629033-e363-4a28-a567-0db78006e6d7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IEnumShellItems
    {
        [PreserveSig] int Next(uint count, out IShellItem item, out uint fetched);
        [PreserveSig] int Skip(uint count);
        [PreserveSig] int Reset();
        [PreserveSig] int Clone(out IEnumShellItems iterator);
    }
    [ComImport, Guid("947aab5f-0a5c-4c13-b4d6-4bf7836fc9f8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFileOperation
    {
        [PreserveSig] int Advise(IFileOperationProgressSink sink, out uint cookie);
        [PreserveSig] int Unadvise(uint cookie);
        [PreserveSig] int SetOperationFlags(uint flags);
        [PreserveSig] int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);
        [PreserveSig] int SetProgressDialog(IntPtr dialog);
        [PreserveSig] int SetProperties(IntPtr properties);
        [PreserveSig] int SetOwnerWindow(IntPtr owner);
        [PreserveSig] int ApplyPropertiesToItem(IShellItem item);
        [PreserveSig] int ApplyPropertiesToItems(IntPtr items);
        [PreserveSig] int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
        [PreserveSig] int RenameItems(IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int MoveItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
        [PreserveSig] int MoveItems(IntPtr items, IShellItem destination);
        [PreserveSig] int CopyItem(IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, IFileOperationProgressSink sink);
        [PreserveSig] int CopyItems(IntPtr items, IShellItem destination);
        [PreserveSig] int DeleteItem(IShellItem item, IFileOperationProgressSink sink);
        [PreserveSig] int DeleteItems(IntPtr items);
        [PreserveSig] int NewItem(IShellItem destination, uint attributes, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, IFileOperationProgressSink sink);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
    }
    [ComVisible(true), Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int hr);
        [PreserveSig] int PreRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostRenameItem(uint flags, IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string name, int hr, IShellItem created);
        [PreserveSig] int PreMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostMoveItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int hr, IShellItem created);
        [PreserveSig] int PreCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostCopyItem(uint flags, IShellItem item, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, int hr, IShellItem created);
        [PreserveSig] int PreDeleteItem(uint flags, IShellItem item);
        [PreserveSig] int PostDeleteItem(uint flags, IShellItem item, int hr, IShellItem created);
        [PreserveSig] int PreNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int PostNewItem(uint flags, IShellItem destination, [MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.LPWStr)] string template, uint attributes, int hr, IShellItem created);
        [PreserveSig] int UpdateProgress(uint total, uint done);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class ShellDeleteSink : IFileOperationProgressSink
    {
        internal bool SawDeletion, UserSkipped, NeedsPermission;
        internal int LastProblem;
        public int StartOperations() { return 0; }
        public int FinishOperations(int hr) { return 0; }
        public int PreRenameItem(uint f, IShellItem i, string n) { return 0; }
        public int PostRenameItem(uint f, IShellItem i, string n, int hr, IShellItem c) { return 0; }
        public int PreMoveItem(uint f, IShellItem i, IShellItem d, string n) { return 0; }
        public int PostMoveItem(uint f, IShellItem i, IShellItem d, string n, int hr, IShellItem c) { return 0; }
        public int PreCopyItem(uint f, IShellItem i, IShellItem d, string n) { return 0; }
        public int PostCopyItem(uint f, IShellItem i, IShellItem d, string n, int hr, IShellItem c) { return 0; }
        public int PreDeleteItem(uint f, IShellItem i) { return 0; }
        public int PostDeleteItem(uint f, IShellItem i, int hr, IShellItem c)
        {
            // COPYENGINE_S_DONT_PROCESS_CHILDREN is a completed leaf deletion, not a skip.
            if (hr == 0 || hr == 0x00270008) SawDeletion = true;
            else if (hr < 0 || hr == 0x00270005) {
                LastProblem = hr; NeedsPermission |= NativeShellDelete.RequiresPermission(hr);
                UserSkipped |= hr == 0x00270005;
            }
            // NOT_HANDLED/PENDING are intermediate callbacks. The operation may still
            // succeed later; GetAnyOperationsAborted and terminal callbacks decide completion.
            return 0;
        }
        public int PreNewItem(uint f, IShellItem d, string n) { return 0; }
        public int PostNewItem(uint f, IShellItem d, string n, string t, uint a, int hr, IShellItem c) { return 0; }
        public int UpdateProgress(uint t, uint d) { return 0; }
        public int ResetTimer() { return 0; }
        public int PauseTimer() { return 0; }
        public int ResumeTimer() { return 0; }
    }
}
