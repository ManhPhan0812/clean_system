using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using CleanSystem;

internal static class CleanerTests
{
    private static int passed;
    private static string sandbox;
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string link, string existing, IntPtr security);

    public static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--command-worker") return CommandTests.Worker(args[1]);
        try
        {
            if (args.Length != 1) throw new ArgumentException("Supply the project test-output directory.");
            sandbox = Path.Combine(Path.GetFullPath(args[0]), "fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sandbox);
            Run("scan is read-only; age and creation gates; nested files", TestScan);
            Run("delete unchanged old file and write audit trail", TestDelete);
            Run("changed and replaced files survive stale snapshots", TestChanged);
            Run("locked file survives; cleanup continues", TestLocked);
            Run("out-of-root and sibling-prefix paths are rejected", TestScope);
            Run("read-only files and hard links are preserved", TestProtected);
            Run("junctions are never traversed or deleted through", TestJunction);
            Run("canceled operations do not delete files", TestCancellation);
            Run("audit failure prevents deletion", TestLogFailure);
            Run("production roots are narrowly scoped to C", TestProductionRoots);
            Run("developer inventory counts new and read-only files without selecting them", TestDeveloperInventory);
            Run("inventory-only policy blocks forged cleanup requests", TestInspectOnly);
            Run("Gradle rules preserve dependencies, locks, registry and settings", TestGradleRules);
            Run("temporary and developer scans remain separate", TestSeparateModes);
            Run("Gradle cleanup deletes only eligible generated cache and daemon logs", TestGradleCleanup);
            Run("Java activity blocks both Gradle scan eligibility and stale cleanup", TestJavaGuard);
            Run("single-folder scan touches only the requested Other group", TestSingleFolderScan);
            Run("unknown or wrong-mode folder ids never trigger a full scan", TestInvalidFolderScan);
            Run("successive folder scans preserve other results and replace only the target", TestMergeFolderScans);
            Run("canceling a folder rescan clears only that folder's cleanup candidates", TestCanceledFolderRescan);
            CommandTests.RunAll(sandbox, Run);
            RecycleBinTests.RunAll(sandbox, Run);
            Console.WriteLine("PASS: " + passed + " tests. Fixtures retained at " + sandbox);
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine("FAIL: " + e); return 1; }
    }
    private static void Run(string name, Action action) { action(); passed++; Console.WriteLine("PASS: " + name); }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static string Folder(string name) { string path = Path.Combine(sandbox, name); Directory.CreateDirectory(path); return path; }
    private static string OldFile(string root, string name)
    {
        string path = Path.Combine(root, name); Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, "fixture content");
        File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-30));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));
        return path;
    }
    private static Cleaner Engine(string root) { return new Cleaner(new[] { new CleanupGroup("fixture", "Test fixture", "", root) }); }
    private static ScanResult Scan(Cleaner engine) { return engine.Scan(7, CancellationToken.None, null); }
    private static CleanResult Clean(Cleaner engine, ScanResult scan, string logName)
    {
        return engine.Clean(scan.Groups.SelectMany(g => g.Files).ToList(), scan.CutoffUtc, Path.Combine(sandbox, logName), CancellationToken.None, null);
    }
    private static void TestScan()
    {
        string root = Folder("scan"); string old = OldFile(root, @"nested\old.tmp");
        string recent = Path.Combine(root, "recent.tmp"); File.WriteAllText(recent, "new");
        string backdated = Path.Combine(root, "new-with-old-write.tmp"); File.WriteAllText(backdated, "new"); File.SetLastWriteTimeUtc(backdated, DateTime.UtcNow.AddDays(-30));
        var scan = Scan(Engine(root));
        Assert(scan.Groups[0].Count == 1 && scan.Groups[0].Files[0].Path == old, "Scan included new files or missed old file.");
        Assert(File.Exists(old) && File.Exists(recent) && File.Exists(backdated), "Scan deleted a file.");
    }
    private static void TestDelete()
    {
        string root = Folder("delete"); string file = OldFile(root, "old.tmp");
        var engine = Engine(root); var scan = Scan(engine); long size = scan.Groups[0].Bytes;
        var result = Clean(engine, scan, "logs-delete");
        Assert(result.Error == null && result.Deleted == 1 && result.Bytes == size && !File.Exists(file), "Old file was not deleted correctly: " + result.Error);
        string log = File.ReadAllText(result.LogPath);
        Assert(log.Contains("\tATTEMPT\t") && log.Contains("\tDELETED\t") && log.Contains("\tEND\t"), "Incomplete audit log.");
    }
    private static void TestChanged()
    {
        string root = Folder("changed"); string file = OldFile(root, "old.tmp");
        string replacement = OldFile(root, "replacement.tmp"); var engine = Engine(root); var scan = Scan(engine);
        File.AppendAllText(file, "changed");
        // Keep original allocated so the filesystem cannot reuse its identity for the replacement.
        File.Move(replacement, Path.Combine(root, "original-moved.tmp")); OldFile(root, "replacement.tmp");
        var result = Clean(engine, scan, "logs-changed");
        Assert(result.Deleted == 0 && result.Skipped == 2 && File.Exists(file) && File.Exists(replacement), "Stale scan deleted changed or replaced data.");
    }
    private static void TestLocked()
    {
        string root = Folder("locked"); string file = OldFile(root, "locked.tmp"); string unlocked = OldFile(root, "free.tmp");
        var engine = Engine(root); var scan = Scan(engine);
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            var result = Clean(engine, scan, "logs-locked");
            Assert(result.Deleted == 1 && result.Skipped == 1 && File.Exists(file) && !File.Exists(unlocked), "Locked file deleted or free file not cleaned.");
        }
    }
    private static void TestScope()
    {
        string root = Folder("scope"); string outside = OldFile(Folder("scope-sibling"), "outside.tmp");
        Assert(!PathPolicy.IsWithin(outside, root), "Sibling prefix accepted.");
        Assert(!PathPolicy.IsWithin(root, root), "Root itself accepted as a file.");
        Assert(!PathPolicy.IsWithin(Path.Combine(root, @"..\scope-sibling\outside.tmp"), root), "Traversal accepted.");
        var candidate = NativeFiles.Snapshot(outside, Path.GetDirectoryName(outside), "outside", false);
        var result = Engine(root).Clean(new List<FileCandidate> { candidate }, DateTime.UtcNow.AddDays(-7), Path.Combine(sandbox, "logs-scope"), CancellationToken.None, null);
        Assert(result.Deleted == 0 && result.Skipped == 1 && File.Exists(outside), "Non-allowlisted root accepted.");
        candidate.Root = root;
        result = Engine(root).Clean(new List<FileCandidate> { candidate }, DateTime.UtcNow.AddDays(-7), Path.Combine(sandbox, "logs-scope-forged"), CancellationToken.None, null);
        Assert(result.Deleted == 0 && result.Skipped == 1 && File.Exists(outside), "Forged root accepted.");
    }
    private static void TestProtected()
    {
        string root = Folder("protected"); string readOnly = OldFile(root, "readonly.tmp"); File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        string hard = OldFile(root, "hard.tmp"); string link = Path.Combine(root, "hard-link.tmp");
        Assert(CreateHardLink(link, hard, IntPtr.Zero), "Could not create test hard link: " + Marshal.GetLastWin32Error());
        var scan = Scan(Engine(root));
        Assert(scan.Groups[0].Count == 0 && File.Exists(hard) && File.Exists(link) && File.Exists(readOnly), "Protected files were included.");
        string afterScan = OldFile(root, "readonly-after-scan.tmp"); var engine = Engine(root); scan = Scan(engine);
        File.SetAttributes(afterScan, FileAttributes.ReadOnly);
        var result = Clean(engine, scan, "logs-protected"); Assert(result.Deleted == 0 && File.Exists(afterScan), "Read-only change ignored.");
    }
    private static void Junction(string path, string target)
    {
        string script = "$ErrorActionPreference = 'Stop'; New-Item -ItemType Junction -Path '" + path.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' | Out-Null";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        using (var process = Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " + encoded) { UseShellExecute = false, CreateNoWindow = true }))
        {
            if (!process.WaitForExit(15000)) { process.Kill(); throw new Exception("Junction fixture timed out."); }
            Assert(process.ExitCode == 0, "Could not create junction fixture.");
        }
    }
    private static void TestJunction()
    {
        string root = Folder("junction"); string outside = OldFile(Folder("junction-outside"), "important.txt");
        Junction(Path.Combine(root, "link"), Path.GetDirectoryName(outside));
        var scan = Scan(Engine(root)); Assert(scan.Groups[0].Count == 0 && File.Exists(outside), "Scan traversed junction.");
        var linkedRoot = Engine(Path.Combine(root, "link")); scan = Scan(linkedRoot);
        Assert(scan.Groups[0].Count == 0, "Root junction was accepted.");
        string nested = Path.Combine(root, "nested"); Directory.CreateDirectory(nested); OldFile(nested, "important.txt");
        var engine = Engine(root); scan = Scan(engine);
        Directory.Move(nested, Path.Combine(root, "nested-original")); Junction(nested, Path.GetDirectoryName(outside));
        var result = Clean(engine, scan, "logs-junction"); Assert(result.Deleted == 0 && result.Skipped == 1 && File.Exists(outside), "Changed ancestor junction was traversed.");
    }
    private static void TestCancellation()
    {
        string root = Folder("cancel"); string file = OldFile(root, "old.tmp"); var engine = Engine(root); var scan = Scan(engine);
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); var canceledScan = engine.Scan(7, cancel.Token, null); Assert(canceledScan.Canceled, "Canceled scan not marked.");
            var result = engine.Clean(scan.Groups[0].Files, scan.CutoffUtc, Path.Combine(sandbox, "logs-canceled"), cancel.Token, null);
            Assert(result.Canceled && result.Deleted == 0 && File.Exists(file), "Canceled cleanup deleted file.");
        }
    }
    private static void TestLogFailure()
    {
        string root = Folder("log-failure"); string file = OldFile(root, "old.tmp"); var engine = Engine(root); var scan = Scan(engine);
        string blockedLog = Path.Combine(sandbox, "not-a-directory"); File.WriteAllText(blockedLog, "block");
        var result = engine.Clean(scan.Groups[0].Files, scan.CutoffUtc, blockedLog, CancellationToken.None, null);
        Assert(result.Error != null && result.Deleted == 0 && File.Exists(file), "Deletion occurred without log.");
    }
    private static void TestProductionRoots()
    {
        foreach (var group in new Cleaner().NewGroups())
        {
            Assert(PathPolicy.IsOnC(group.Root), "Production root outside C.");
            if (!group.IsDeveloper)
                Assert(group.Root.EndsWith("\\Temp", StringComparison.OrdinalIgnoreCase) || group.Root.EndsWith("\\WER\\ReportArchive", StringComparison.OrdinalIgnoreCase) || group.Root.EndsWith("\\WER\\ReportQueue", StringComparison.OrdinalIgnoreCase), "Broad production root.");
            else
            {
                Assert(!group.Selected, "Developer data selected by default.");
                Assert(!group.CanClean || group.Rule == CleanupRule.GradleDerived || group.Rule == CleanupRule.GradleLogs, "Unreviewed developer data may be deleted.");
            }
        }
    }
    private static void TestDeveloperInventory()
    {
        string root = Folder("developer-inventory"); string old = OldFile(root, "old.tmp");
        File.SetAttributes(old, FileAttributes.ReadOnly);
        string recent = Path.Combine(root, "recent.tmp"); File.WriteAllText(recent, "new developer data");
        var engine = new Cleaner(new[] { new CleanupGroup("dev-inspect", "Test", "", root, true, CleanupRule.InspectOnly) });
        var scan = engine.Scan(7, CancellationToken.None, null, true); var group = scan.Groups.Single();
        Assert(group.TotalBytes == new FileInfo(old).Length + new FileInfo(recent).Length && group.TotalCount == 2, "Inventory ignored read-only/new data.");
        Assert(!group.Selected && !group.CanClean && group.Count == 0 && !group.Partial, "Inventory became cleanable or incorrect partial status.");
        Assert(File.Exists(old) && File.Exists(recent), "Inventory mutated developer data.");
    }
    private static void TestInspectOnly()
    {
        string root = Folder("developer-protected"); string file = OldFile(root, "important.txt");
        var engine = new Cleaner(new[] { new CleanupGroup("protected", "Test", "", root, true, CleanupRule.InspectOnly) });
        var fileInfo = NativeFiles.Snapshot(file, root, "Test", false);
        var result = engine.Clean(new[] { fileInfo }, DateTime.UtcNow.AddDays(-7), Path.Combine(sandbox, "logs-inspect"), CancellationToken.None, null);
        Assert(result.Deleted == 0 && result.Skipped == 1 && File.Exists(file), "Inspection-only root could be deleted through the backend.");
    }
    private static void TestGradleRules()
    {
        string root = Folder("gradle-rules");
        var caches = new CleanupGroup("caches", "Test", "", root, true, CleanupRule.GradleDerived);
        foreach (string relative in new[] { @"build-cache-1\ab\artifact", @"jars-9\hash\library.jar", @"transforms-3\hash\output.bin" })
            Assert(Cleaner.MatchesRule(caches, Path.Combine(root, relative)), "Generated Gradle cache not recognized: " + relative);
        foreach (string relative in new[] { @"modules-2\files-2.1\library.jar", @"build-cache-1\cache.lock", @"build-cache-1\CACHEDIR.TAG", "gradle.properties", @"jars-custom\file", @"..\outside\file" })
            Assert(!Cleaner.MatchesRule(caches, Path.Combine(root, relative)), "Protected Gradle data eligible: " + relative);
        var logs = new CleanupGroup("logs", "Test", "", root, true, CleanupRule.GradleLogs);
        Assert(Cleaner.MatchesRule(logs, Path.Combine(root, @"9.0\daemon-1234.out.log")), "Daemon log not recognized.");
        foreach (string file in new[] { "registry.bin", "registry.bin.lock", "gradle.properties", "other.out.log" })
            Assert(!Cleaner.MatchesRule(logs, Path.Combine(root, file)), "Non-log daemon data eligible.");
        string dependency = OldFile(root, @"modules-2\files-2.1\library.jar");
        var engine = new Cleaner(new[] { caches });
        var result = engine.Clean(new[] { NativeFiles.Snapshot(dependency, root, "Test", false) }, DateTime.UtcNow.AddDays(-7), Path.Combine(sandbox, "logs-gradle-protected"), CancellationToken.None, null);
        Assert(result.Deleted == 0 && result.Skipped == 1 && File.Exists(dependency), "Gradle dependency protection not enforced at deletion.");
    }
    private static void TestSeparateModes()
    {
        string temp = Folder("mode-temp"), dev = Folder("mode-dev"); OldFile(temp, "temp.tmp"); OldFile(dev, "dev.tmp");
        var engine = new Cleaner(new[] { new CleanupGroup("fixture", "Temp", "", temp), new CleanupGroup("inspect", "Dev", "", dev, true, CleanupRule.InspectOnly) });
        var normal = engine.Scan(7, CancellationToken.None, null); var developer = engine.Scan(7, CancellationToken.None, null, true);
        Assert(normal.Groups.Count == 1 && !normal.Groups[0].IsDeveloper && normal.Groups[0].Count == 1, "Temp scan includes developer roots.");
        Assert(developer.Groups.Count == 1 && developer.Groups[0].IsDeveloper && developer.Groups[0].TotalCount == 1 && developer.Groups[0].Count == 0, "Developer scan includes temporary roots.");
    }
    private static void TestGradleCleanup()
    {
        string cache = Folder("gradle-clean-cache"), daemon = Folder("gradle-clean-daemon");
        string derived = OldFile(cache, @"build-cache-1\hash\artifact"), transformed = OldFile(cache, @"transforms-3\hash\output");
        string dependency = OldFile(cache, @"modules-2\files-2.1\private.jar"), cacheLock = OldFile(cache, @"build-cache-1\cache.lock");
        string registry = OldFile(daemon, @"9.0\registry.bin"), log = OldFile(daemon, @"9.0\daemon-42.out.log");
        var groups = new[] { new CleanupGroup("cache", "Cache", "", cache, true, CleanupRule.GradleDerived), new CleanupGroup("daemon", "Logs", "", daemon, true, CleanupRule.GradleLogs) };
        var engine = new Cleaner(groups, () => false); var scan = engine.Scan(7, CancellationToken.None, null, true);
        Assert(scan.Groups.Sum(g => g.Count) == 3 && scan.Groups.Sum(g => g.TotalCount) == 6, "Wrong Gradle eligibility/inventory.");
        var result = Clean(engine, scan, "logs-gradle-clean");
        Assert(result.Error == null && result.Deleted == 3 && !File.Exists(derived) && !File.Exists(transformed) && !File.Exists(log), "Eligible Gradle data was not cleaned.");
        Assert(File.Exists(dependency) && File.Exists(cacheLock) && File.Exists(registry), "Dependency, lock or daemon registry was deleted.");
    }
    private static void TestJavaGuard()
    {
        string root = Folder("java-guard"); string file = OldFile(root, @"build-cache-1\artifact");
        var groups = new[] { new CleanupGroup("cache", "Cache", "", root, true, CleanupRule.GradleDerived) };
        var idleEngine = new Cleaner(groups, () => false); var scan = idleEngine.Scan(7, CancellationToken.None, null, true);
        var busyEngine = new Cleaner(groups, () => true); var activeScan = busyEngine.Scan(7, CancellationToken.None, null, true);
        Assert(activeScan.Groups[0].Count == 0 && activeScan.Groups[0].TotalCount == 1 && activeScan.Groups[0].State.Contains("đang chạy"), "Active Java did not block scan eligibility.");
        var result = Clean(busyEngine, scan, "logs-java-guard");
        Assert(result.Deleted == 0 && result.Skipped == 1 && File.Exists(file), "Java started after scan did not prevent deletion.");
    }
    private static void TestSingleFolderScan()
    {
        string first = Folder("single-first"), second = Folder("single-second");
        string file = OldFile(first, "first.txt"); OldFile(second, "second.txt"); OldFile(second, "third.txt");
        var engine = new Cleaner(new[] {
            new CleanupGroup("first", "First", "", first, true, CleanupRule.InspectOnly),
            new CleanupGroup("second", "Second", "", second, true, CleanupRule.InspectOnly)
        });
        var result = engine.Scan(7, CancellationToken.None, null, true, "first");
        Assert(result.Groups.Count == 1 && result.Groups[0].Id == "first" && result.Inspected == 1, "Single scan traversed a different group.");
        Assert(result.Groups[0].TotalBytes == new FileInfo(file).Length && File.Exists(file), "Single scan changed a file or measured the wrong scope.");
    }
    private static void TestInvalidFolderScan()
    {
        string temp = Folder("invalid-id-temp"), dev = Folder("invalid-id-dev");
        var engine = new Cleaner(new[] { new CleanupGroup("fixture", "Temp", "", temp), new CleanupGroup("dev", "Dev", "", dev, true, CleanupRule.InspectOnly) });
        foreach (string id in new[] { "unknown", "fixture", "..\\outside", "" })
        {
            bool rejected = false;
            try { engine.Scan(7, CancellationToken.None, null, true, id); }
            catch (ArgumentException) { rejected = true; }
            Assert(rejected, "Invalid folder id fell back to a whole scan: " + id);
        }
    }
    private static Cleaner FolderScanEngine(string name)
    {
        string first = Folder(name + "-first"), second = Folder(name + "-second");
        OldFile(first, @"build-cache-1\first.bin"); OldFile(second, @"9.0\daemon-123.out.log");
        return new Cleaner(new[] {
            new CleanupGroup("first", "First", "", first, true, CleanupRule.GradleDerived),
            new CleanupGroup("second", "Second", "", second, true, CleanupRule.GradleLogs)
        }, () => false);
    }
    private static void TestMergeFolderScans()
    {
        var engine = FolderScanEngine("merge");
        var firstUpdate = engine.Scan(7, CancellationToken.None, null, true, "first");
        var combined = ScanResult.MergeFolderScan(engine.NewGroups(), firstUpdate, "first");
        Assert(combined.Groups.Count == 2 && !combined.Groups[1].Measured, "Unscanned rows were removed or marked as scanned.");
        combined.Groups[0].Selected = true;
        var original = combined.Groups[0]; var oldCandidate = original.Files[0];
        var secondUpdate = engine.Scan(7, CancellationToken.None, null, true, "second");
        combined = ScanResult.MergeFolderScan(combined.Groups, secondUpdate, "second");
        Assert(Object.ReferenceEquals(original, combined.Groups[0]) && combined.Groups[0].Selected && combined.Groups.Sum(g => g.Count) == 2, "A second scan lost previous results/selections.");
        Assert(oldCandidate.ScanCutoffUtc == firstUpdate.CutoffUtc && combined.Groups[1].Files[0].ScanCutoffUtc == secondUpdate.CutoffUtc, "Candidate age thresholds were lost during merge.");
        string extra = OldFile(combined.Groups[1].Root, @"9.0\daemon-456.out.log");
        var rescan = engine.Scan(7, CancellationToken.None, null, true, "second");
        combined = ScanResult.MergeFolderScan(combined.Groups, rescan, "second");
        Assert(combined.Groups[1].Count == 2 && combined.Groups[0].Count == 1, "Folder rescan appended duplicate candidates.");
        var cleaned = Clean(engine, combined, "logs-merged-folders");
        Assert(cleaned.Error == null && cleaned.Deleted == 3 && !File.Exists(extra), "Merged folder results could not be cleaned correctly.");
    }
    private static void TestCanceledFolderRescan()
    {
        var engine = FolderScanEngine("cancel-folder");
        var original = engine.Scan(7, CancellationToken.None, null, true);
        string secondFile = original.Groups[1].Files[0].Path;
        // Model an operation canceled after discovering a candidate, not just before enumeration.
        var partial = engine.Scan(7, CancellationToken.None, null, true, "second"); partial.Canceled = true;
        var combined = ScanResult.MergeFolderScan(original.Groups, partial, "second");
        Assert(!combined.Canceled && combined.Groups[0].Count == 1 && combined.Groups[1].Count == 0, "Canceled target revived candidates or invalidated a completed sibling.");
        var cleaned = Clean(engine, combined, "logs-canceled-folder");
        Assert(cleaned.Deleted == 1 && File.Exists(secondFile), "Canceled folder's candidate was deleted.");
    }
}
