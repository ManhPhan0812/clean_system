using System;
using System.IO;
using CleanSystem;

internal static class RecycleBinTests
{
    public static void RunAll(string parent, Action<string, Action> run)
    {
        string logs = Path.Combine(parent, "recycle-fixtures");
        run("recycle service includes D items when C is empty using the all-drive shell query", () => {
            string scope = "not-called";
            var service = new RecycleBinService(root => { scope = root; return root == null ? new RecycleBinInfo(1234, 2) : new RecycleBinInfo(0, 0, root); }, root => { throw new Exception("Must not empty during query."); });
            var info = service.QueryAsync().GetAwaiter().GetResult();
            Assert(scope == null && info.Root == null && info.Bytes == 1234 && info.Count == 2, "All-drive query missed items from D.");
        });
        run("recycle query rejects single-drive metadata instead of displaying incomplete totals", () => {
            var service = new RecycleBinService(root => new RecycleBinInfo(1234, 2, @"D:\"), root => { throw new Exception("Must not empty during query."); });
            bool rejected = false;
            try { service.Query(); } catch (IOException) { rejected = true; }
            Assert(rejected, "Single-drive metadata was displayed as all-drive totals.");
        });
        run("recycle cleanup records its audit before the mocked shell call", () => {
            var current = new RecycleBinInfo(2048, 2); bool called = false;
            var service = new RecycleBinService(root => current, root => {
                Assert(root == null, "Cleanup did not request all drives.");
                string log = Directory.GetFiles(logs, "*.log")[0];
                using (var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream)) Assert(reader.ReadToEnd().Contains("BEGIN scope=all-drives;"), "No all-drive audit before empty.");
                called = true; current = new RecycleBinInfo(0, 0); return 0;
            });
            var result = service.EmptyAsync(current, logs).GetAwaiter().GetResult();
            Assert(called && result.Completed && result.Attempted && result.After.Count == 0 && File.ReadAllText(result.LogPath).Contains("completed=True"), "Recycle completion/audit wrong: " + result.Error);
        });
        run("changed recycle totals require a fresh confirmation", () => {
            int calls = 0;
            var service = new RecycleBinService(root => new RecycleBinInfo(4096, 3), root => { calls++; return 0; });
            var result = service.EmptyApproved(new RecycleBinInfo(1024, 1), logs);
            Assert(calls == 0 && !result.Attempted && result.Error != null, "Changed bin was emptied after stale approval.");
        });
        run("recycle audit failure prevents the mocked destructive call", () => {
            int calls = 0; var info = new RecycleBinInfo(1024, 1);
            string blocked = Path.Combine(parent, "recycle-log-is-file"); File.WriteAllText(blocked, "blocked");
            var service = new RecycleBinService(root => info, root => { calls++; return 0; });
            var result = service.EmptyApproved(info, blocked);
            Assert(calls == 0 && !result.Attempted && result.Error != null, "Recycle cleanup ran without an audit.");
        });
        run("recycle shell failures report remaining items instead of success", () => {
            var info = new RecycleBinInfo(4096, 4);
            var service = new RecycleBinService(root => info, root => { info = new RecycleBinInfo(1024, 1); return unchecked((int)0x80070005); });
            var result = service.EmptyApproved(info, logs);
            Assert(result.Attempted && !result.Completed && result.Error != null && result.After.Count == 1, "Partial shell failure was misreported.");
        });
        run("unreadable post-cleanup recycle totals do not claim an empty bin", () => {
            bool emptied = false; var info = new RecycleBinInfo(1024, 1);
            var service = new RecycleBinService(root => { if (emptied) throw new IOException("query failure"); return info; }, root => { emptied = true; return 0; });
            var result = service.EmptyApproved(info, logs);
            Assert(result.Attempted && !result.Completed && result.After == null && result.Error != null, "Failed verification claimed success.");
        });
        run("already-empty recycle bin does not call the destructive API", () => {
            int calls = 0; var info = new RecycleBinInfo(0, 0);
            var service = new RecycleBinService(root => info, root => { calls++; return 0; });
            var result = service.EmptyApproved(info, logs);
            Assert(calls == 0 && !result.Attempted && result.Completed, "Empty bin caused a needless destructive API call.");
        });
        run("recycle cleanup rejects missing or single-drive approval", () => {
            int calls = 0;
            var service = new RecycleBinService(root => new RecycleBinInfo(1024, 1), root => { calls++; return 0; });
            var missing = service.EmptyApproved(null, logs);
            var onlyC = service.EmptyApproved(new RecycleBinInfo(1024, 1, @"C:\"), logs);
            var onlyD = service.EmptyApproved(new RecycleBinInfo(1024, 1, @"D:\"), logs);
            Assert(calls == 0 && missing.Error != null && onlyC.Error != null && onlyD.Error != null, "Missing/single-drive approval accepted for all-drive cleanup.");
        });
        run("native recycle query works read-only with the current Windows ABI", () => {
            var info = new RecycleBinService().QueryAsync().GetAwaiter().GetResult();
            Assert(info.Root == null && info.Bytes >= 0 && info.Count >= 0, "Native all-drive recycle metadata was invalid.");
            Console.WriteLine("INFO: native query only; bin not emptied. Scope=all-drives; Items=" + info.Count + "; bytes=" + info.Bytes);
        });
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
}
