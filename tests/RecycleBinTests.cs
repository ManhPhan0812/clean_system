using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CleanSystem;

internal static class RecycleBinTests
{
    private sealed class Backend : IRecycleBinBackend
    {
        public RecycleBinInfo Current = new RecycleBinInfo(2048, 2);
        public Func<string, RecycleBinInfo> Reader;
        public Func<IList<RecycleBinItem>> Enumerator;
        public Func<RecycleBinItem, bool, IntPtr, RecycleDeleteOutcome> Cleaner;
        public int Calls;
        public RecycleBinInfo Query(string root) { Assert(root == null, "Query did not include all drives."); return Reader == null ? Current : Reader(root); }
        public IList<RecycleBinItem> Snapshot() { return Enumerator == null ? Items("a", "b") : Enumerator(); }
        public RecycleDeleteOutcome Delete(RecycleBinItem item, bool elevation, IntPtr owner) { Calls++; return Cleaner(item, elevation, owner); }
    }
    private static IList<RecycleBinItem> Items(params string[] names) { return names.Select(n => new RecycleBinItem(n)).ToList(); }
    private static RecycleDeleteOutcome Deleted() { return new RecycleDeleteOutcome(true, false, "deleted"); }
    private static RecycleDeleteOutcome Permission() { return new RecycleDeleteOutcome(false, true, "permission"); }
    public static void RunAll(string parent, Action<string, Action> run)
    {
        string logs = Path.Combine(parent, "recycle-fixtures");
        run("recycle query includes all drives and rejects single-drive metadata", () => {
            var backend = new Backend { Reader = root => new RecycleBinInfo(1234, 2) };
            var service = new RecycleBinService(backend); var info = service.QueryAsync().GetAwaiter().GetResult();
            Assert(info.Root == null && info.Bytes == 1234 && backend.Calls == 0, "All-drive totals incorrect.");
            backend.Reader = root => new RecycleBinInfo(1234, 2, @"D:\");
            bool rejected = false; try { service.Query(); } catch (IOException) { rejected = true; }
            Assert(rejected, "Single-drive metadata accepted.");
        });
        run("recycle records durable audit before each deletion and reports completion", () => {
            var backend = new Backend();
            backend.Cleaner = (item, elevate, owner) => {
                string log = Directory.GetFiles(logs, "*.log")[0];
                using (var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream)) {
                    string content = reader.ReadToEnd();
                    Assert(content.Contains("BEGIN scope=all-drives;") && content.Contains("ATTEMPT " + item.Name), "No durable item audit.");
                }
                backend.Current = new RecycleBinInfo(0, 0); return Deleted();
            };
            var result = new RecycleBinService(backend).EmptyAsync(backend.Current, logs).GetAwaiter().GetResult();
            Assert(result.Completed && result.Finished && result.Deleted == 2 && result.Skipped == 0 && File.ReadAllText(result.LogPath).Contains("completed=True"), "Completion/audit wrong.");
        });
        run("No skips protected item once and continues later recycle items without waiting for Stop", () => {
            var backend = new Backend { Current = new RecycleBinInfo(3072, 3), Enumerator = () => Items("first", "protected", "last") };
            var attempts = new List<string>(); int prompts = 0; RecycleBinProgress last = null;
            backend.Cleaner = (item, elevate, owner) => {
                Assert(!elevate, "No allowed elevated retry."); attempts.Add(item.Name);
                if (item.Name == "protected") return Permission();
                backend.Current = new RecycleBinInfo(1024, 1); return Deleted();
            };
            var task = new RecycleBinService(backend).EmptyAsync(backend.Current, logs, item => { prompts++; return false; }, update => last = update);
            Assert(task.Wait(5000), "No hung instead of proceeding."); var result = task.Result;
            Assert(attempts.SequenceEqual(new[] { "first", "protected", "last" }) && prompts == 1 && result.Finished && !result.Completed && result.Error == null && result.Deleted == 2 && result.Skipped == 1 && result.After.Count == 1, "No canceled the batch or misreported results.");
            Assert(last.Deleted == 2 && last.Skipped == 1 && last.Current == null && File.ReadAllText(result.LogPath).Contains("PERMISSION allowed=False"), "Final progress or skip audit missing.");
        });
        run("Yes retries only the protected recycle item then continues the batch", () => {
            var backend = new Backend(); var attempts = new List<string>(); int prompts = 0;
            backend.Cleaner = (item, elevated, owner) => {
                Assert(owner == new IntPtr(123), "Dialog owner was lost."); attempts.Add(item.Name + ":" + elevated);
                if (item.Name == "a" && !elevated) return Permission();
                backend.Current = new RecycleBinInfo(0, 0); return Deleted();
            };
            var result = new RecycleBinService(backend).EmptyAsync(backend.Current, logs, item => { prompts++; return true; }, null, new IntPtr(123)).GetAwaiter().GetResult();
            Assert(result.Completed && result.Deleted == 2 && prompts == 1 && attempts.SequenceEqual(new[] { "a:False", "a:True", "b:False" }), "Yes retried the whole bin or stopped after the first item.");
        });
        run("refusing Windows elevation after Yes skips only that item without repeated prompts", () => {
            var backend = new Backend(); int prompts = 0;
            backend.Cleaner = (item, elevated, owner) => item.Name == "a" ? Permission() : Deleted();
            var task = new RecycleBinService(backend).EmptyAsync(backend.Current, logs, item => { prompts++; return true; });
            Assert(task.Wait(5000) && prompts == 1 && backend.Calls == 3 && task.Result.Finished && task.Result.Deleted == 1 && task.Result.Skipped == 1 && task.Result.Error == null, "Declined elevation retried indefinitely or canceled the batch.");
        });
        run("recycle errors skip the affected item and remaining data is displayed", () => {
            var backend = new Backend();
            backend.Cleaner = (item, elevated, owner) => item.Name == "a" ? new RecycleDeleteOutcome(false, false, "locked") : Deleted();
            var result = new RecycleBinService(backend).EmptyApproved(backend.Current, logs, item => { throw new Exception("Locked file must not ask for elevation."); });
            Assert(result.Finished && result.Deleted == 1 && result.Skipped == 1 && result.After != null && result.Error == null, "Partial cleanup was treated as hanging/fatal.");
        });
        run("changed recycle totals require fresh confirmation before any snapshot/deletion", () => {
            var backend = new Backend { Enumerator = () => { throw new Exception("Stale approval reached enumeration."); } };
            var result = new RecycleBinService(backend).EmptyApproved(new RecycleBinInfo(1024, 1), logs);
            Assert(backend.Calls == 0 && !result.Attempted && result.Error != null, "Stale approval deleted data.");
        });
        run("recycle audit failure prevents enumeration and deletion", () => {
            string blocked = Path.Combine(parent, "recycle-log-is-file"); File.WriteAllText(blocked, "blocked");
            var backend = new Backend { Enumerator = () => { throw new Exception("No audit reached enumeration."); } };
            var result = new RecycleBinService(backend).EmptyApproved(backend.Current, blocked);
            Assert(backend.Calls == 0 && !result.Attempted && result.Error != null, "Cleanup ran without audit.");
        });
        run("unreadable post-cleanup totals do not claim an empty bin", () => {
            var backend = new Backend(); bool emptied = false;
            backend.Reader = root => { if (emptied) throw new IOException("query failure"); return backend.Current; };
            backend.Cleaner = (item, elevated, owner) => { emptied = true; return Deleted(); };
            var result = new RecycleBinService(backend).EmptyApproved(backend.Current, logs);
            Assert(result.Attempted && result.Finished && !result.Completed && result.After == null && result.Error != null, "Failed verification claimed success.");
        });
        run("already-empty bin and invalid approvals never enumerate/delete", () => {
            var backend = new Backend { Current = new RecycleBinInfo(0, 0), Enumerator = () => { throw new Exception("Must not enumerate."); } };
            var service = new RecycleBinService(backend);
            Assert(service.EmptyApproved(backend.Current, logs).Completed, "Empty bin was not completed.");
            Assert(service.EmptyApproved(null, logs).Error != null && service.EmptyApproved(new RecycleBinInfo(1024, 1, @"D:\"), logs).Error != null && backend.Calls == 0, "Invalid scope approved.");
        });
        run("positive Shell skip HRESULTs and aborted operations are never counted as deleted", () => {
            var sink = new ShellDeleteSink(); sink.PostDeleteItem(0, null, 0x00270005, null);
            Assert(sink.UserSkipped && !NativeShellDelete.Classify(0, true, true, sink.UserSkipped, false, sink.LastProblem).Deleted, "Shell skip counted as deletion.");
            Assert(!NativeShellDelete.Classify(0, true, true, false, false, 0).Deleted && !NativeShellDelete.Classify(0, false, false, false, false, 0).Deleted, "Aborted/unverified deletion counted as success.");
            sink.PostDeleteItem(0, null, unchecked((int)0x80270002), null);
            Assert(sink.NeedsPermission, "Elevation requirement lost.");
            var leaf = new ShellDeleteSink(); leaf.PostDeleteItem(0, null, 0x00270008, null);
            Assert(NativeShellDelete.Classify(0, false, leaf.SawDeletion, leaf.UserSkipped, false, 0).Deleted, "Completed leaf treated as a skip.");
            var folder = new ShellDeleteSink(); folder.PostDeleteItem(0, null, 0x00270003, null); folder.PostDeleteItem(0, null, 0, null);
            Assert(NativeShellDelete.Classify(0, false, folder.SawDeletion, folder.UserSkipped, false, 0).Deleted, "Intermediate NOT_HANDLED callback blocked folder completion.");
            Assert(NativeShellDelete.RequiresPermission(unchecked((int)0x80270021)) && !NativeShellDelete.RequiresPermission(unchecked((int)0x80270027)), "Shell access-denied and locked outcomes conflated.");
        });
        run("STA recycle worker pumps messages and completes asynchronously", () => {
            var backend = new Backend();
            backend.Reader = root => {
                Assert(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA, "Worker is not STA.");
                bool delivered = false; var frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => { delivered = true; frame.Continue = false; }));
                Dispatcher.PushFrame(frame); Assert(delivered, "Worker did not dispatch messages."); return backend.Current;
            };
            Assert(new RecycleBinService(backend).QueryAsync().Wait(5000), "Worker did not complete.");
        });
        run("native Shell deletion skips a locked fixture then deletes a free fixture with working COM callbacks", () => {
            string directory = Path.Combine(parent, "native-shell-delete-fixtures"); Directory.CreateDirectory(directory);
            string locked = Path.Combine(directory, "locked.txt"), free = Path.Combine(directory, "free.txt"), folder = Path.Combine(directory, "nested-folder");
            File.WriteAllText(locked, "locked"); File.WriteAllText(free, "free");
            Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "child.txt"), "child");
            var backend = new Backend();
            backend.Reader = root => new RecycleBinInfo(6, Directory.GetFileSystemEntries(directory).Length);
            backend.Enumerator = () => new List<RecycleBinItem> {
                new RecycleBinItem("locked fixture", NativeShellDelete.OpenItem(locked)),
                new RecycleBinItem("free fixture", NativeShellDelete.OpenItem(free)),
                new RecycleBinItem("folder fixture", NativeShellDelete.OpenItem(folder))
            };
            backend.Cleaner = (item, elevation, owner) => NativeShellDelete.DeleteOne(item.ShellItem, false, IntPtr.Zero);
            using (var handle = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                var task = new RecycleBinService(backend).EmptyAsync(backend.Query(null), logs);
                Assert(task.Wait(15000), "Native Shell operation hung on locked fixture.");
                var result = task.Result;
                Assert(result.Finished && result.Deleted == 2 && result.Skipped == 1 && File.Exists(locked) && !File.Exists(free) && !Directory.Exists(folder), "Native skip/delete callback incorrect: " + result.Error);
            }
        });
        run("native all-drive recycle query and Shell snapshot work read-only", () => {
            var completion = new TaskCompletionSource<bool>();
            var thread = new Thread(() => {
                try {
                    var backend = new ShellRecycleBinBackend(); var info = backend.Query(null); var items = backend.Snapshot();
                    try { Assert(info.Root == null && info.Bytes >= 0 && info.Count >= 0 && items.All(i => !String.IsNullOrEmpty(i.Name) && i.ShellItem != null), "Native snapshot ABI incorrect."); }
                    finally { foreach (var item in items) item.Dispose(); }
                    Console.WriteLine("INFO: real bin read only; snapshot items=" + items.Count + "; aggregate items=" + info.Count); completion.SetResult(true);
                } catch (Exception e) { completion.SetException(e); }
            }); thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start();
            Assert(completion.Task.Wait(15000) && completion.Task.Result, "Native read timed out.");
        });
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
