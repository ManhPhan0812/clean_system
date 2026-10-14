using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CleanSystem;

internal static class CommandTests
{
    private static string sandbox;
    public static void RunAll(string parent, Action<string, Action> run)
    {
        sandbox = Path.Combine(parent, "command-fixtures"); Directory.CreateDirectory(sandbox);
        run("command reset removes only its registered root", TestRemoveFolder);
        run("command clear preserves root and removes hidden content", TestClearContents);
        run("command roots with apostrophes/spaces are literal", TestLiteralPath);
        run("command rejects changed targets and unknown ids", TestRejectScope);
        run("recursive command refuses junctions before deleting anything", TestRejectJunction);
        run("command audit creation failure prevents execution", TestAuditFailure);
        run("external CLI receives pinned cache path; exit code and stderr are logged", TestExternalFailure);
        run("canceling command stops a child process before returning", TestCancellation);
        run("FVM selection rejects traversal and all-version flags", TestFvmSelection);
        run("active Java and Codex block respective reset commands", TestActiveOwners);
        run("missing CLI is reported before execution", TestMissingTool);
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static string Folder(string name) { string path = Path.Combine(sandbox, name); Directory.CreateDirectory(path); return path; }
    private static CleanupGroup Group(string id, string root) { return new CleanupGroup(id, id, "", root, true, CleanupRule.InspectOnly); }
    private static CommandRunner Runner(CleanupGroup group, string tool = null, bool java = false, bool codex = false)
    {
        return new CommandRunner(new[] { group }, false, name => tool, () => java, () => codex);
    }
    private static CommandResult Run(CommandRunner runner, string id, string logName)
    {
        return runner.Run(runner.Prepare(id), Path.Combine(sandbox, logName), CancellationToken.None, null);
    }
    private static void TestRemoveFolder()
    {
        string root = Folder("remove-root"); File.WriteAllText(Path.Combine(root, "file.txt"), "fixture");
        string sibling = Folder("remove-root-sibling"); string keep = Path.Combine(sibling, "keep.txt"); File.WriteAllText(keep, "keep");
        var result = Run(Runner(Group("gitnexus-data", root)), "gitnexus-data", "logs-remove");
        Assert(result.Error == null && result.ExitCode == 0 && !Directory.Exists(root) && File.Exists(keep), "Reset command exceeded scope or failed: " + result.Error + " " + result.Output);
        Assert(File.ReadAllText(result.LogPath).Contains("START") && File.ReadAllText(result.LogPath).Contains("END exit=0"), "Reset audit missing execution result.");
    }
    private static void TestClearContents()
    {
        string root = Folder("clear-contents"); string hidden = Path.Combine(root, "hidden.txt"); File.WriteAllText(hidden, "hidden"); File.SetAttributes(hidden, FileAttributes.Hidden);
        string nested = Path.Combine(root, "nested"); Directory.CreateDirectory(nested); File.WriteAllText(Path.Combine(nested, "data.txt"), "data");
        var result = Run(Runner(Group("maven-repository", root)), "maven-repository", "logs-clear");
        Assert(result.Error == null && Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any(), "Clear did not preserve empty root or left hidden data: " + result.Error);
    }
    private static void TestLiteralPath()
    {
        string root = Folder("space and O'Brien"); File.WriteAllText(Path.Combine(root, "file.txt"), "literal");
        var result = Run(Runner(Group("generic-cache", root)), "generic-cache", "logs-literal");
        Assert(result.Error == null && Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any(), "PowerShell literal quoting failed: " + result.Error);
    }
    private static void TestRejectScope()
    {
        string root = Folder("scope"); string outside = Folder("scope-outside"); string keep = Path.Combine(outside, "keep.txt"); File.WriteAllText(keep, "keep");
        var runner = Runner(Group("gitnexus-data", root)); var plan = runner.Prepare("gitnexus-data"); plan.Target = outside;
        var result = runner.Run(plan, Path.Combine(sandbox, "logs-invalid"), CancellationToken.None, null);
        Assert(result.Error != null && File.Exists(keep) && result.LogPath == null, "Changed target was executed.");
        bool rejected = false; try { runner.Prepare("unknown"); } catch (ArgumentException) { rejected = true; }
        Assert(rejected, "Unknown command accepted.");
    }
    private static void TestRejectJunction()
    {
        string root = Folder("junction-root"), outside = Folder("junction-outside");
        string keep = Path.Combine(root, "keep.txt"); File.WriteAllText(keep, "keep");
        string important = Path.Combine(outside, "important.txt"); File.WriteAllText(important, "important");
        string script = "New-Item -ItemType Junction -Path " + CommandRunner.Quote(Path.Combine(root, "linked")) + " -Target " + CommandRunner.Quote(outside) + " -ErrorAction Stop | Out-Null";
        using (var process = Process.Start(new ProcessStartInfo(Powershell(), "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script))) { UseShellExecute = false, CreateNoWindow = true }))
        {
            Assert(process.WaitForExit(10000) && process.ExitCode == 0, "Could not create junction fixture.");
        }
        var result = Run(Runner(Group("gitnexus-data", root)), "gitnexus-data", "logs-linked");
        Assert(result.Error != null && File.Exists(keep) && File.Exists(important), "Recursive reset followed a junction or partially deleted before refusing it.");
    }
    private static void TestAuditFailure()
    {
        string root = Folder("audit-target"); string keep = Path.Combine(root, "keep.txt"); File.WriteAllText(keep, "keep");
        string badLog = Path.Combine(sandbox, "log-is-file"); File.WriteAllText(badLog, "block");
        var runner = Runner(Group("gitnexus-data", root)); var result = runner.Run(runner.Prepare("gitnexus-data"), badLog, CancellationToken.None, null);
        Assert(result.Error != null && File.Exists(keep), "Reset happened without an audit log.");
    }
    private static string Tool(string name, string content)
    {
        string path = Path.Combine(Folder("fake-tools"), name + ".cmd"); File.WriteAllText(path, "@echo off\r\n" + content, Encoding.ASCII); return path;
    }
    private static string Powershell() { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"); }
    private static void TestExternalFailure()
    {
        string root = Folder("go-fake-cache");
        string fake = Tool("go-fail", "echo cache=\"%GOMODCACHE%\"\r\necho fake-cli-error 1>&2\r\nexit /b 7\r\n");
        var result = Run(Runner(Group("go-modules", root), fake), "go-modules", "logs-cli-failure");
        Assert(result.ExitCode == 7 && result.Error != null && result.Output.Contains(root) && result.Output.Contains("fake-cli-error"), "CLI exit code, environment or stderr was lost: " + result.Error + " " + result.Output);
        Assert(File.ReadAllText(result.LogPath).Contains("fake-cli-error"), "Stderr missing from audit.");
    }
    private static void TestCancellation()
    {
        string root = Folder("cancel-go"); string pidFile = Path.Combine(sandbox, "worker.pid");
        string fake = Tool("go-worker", "\"" + typeof(CommandTests).Assembly.Location + "\" --command-worker \"" + pidFile + "\"\r\n");
        var runner = Runner(Group("go-modules", root), fake); var plan = runner.Prepare("go-modules");
        using (var cancel = new CancellationTokenSource())
        {
            Task<CommandResult> task = Task.Run(() => runner.Run(plan, Path.Combine(sandbox, "logs-cancel"), cancel.Token, null));
            var timer = Stopwatch.StartNew();
            while (!File.Exists(pidFile) && !task.IsCompleted && timer.ElapsedMilliseconds < 30000) Thread.Sleep(50);
            bool started = File.Exists(pidFile); cancel.Cancel();
            Assert(task.Wait(10000), "Cancellation did not return promptly.");
            Assert(started && task.Result.Canceled, "Worker did not start or cancellation was not reported: started=" + started + "; canceled=" + task.Result.Canceled + "; error=" + task.Result.Error + "; output=" + task.Result.Output);
            int pid = Int32.Parse(File.ReadAllText(pidFile)); bool alive = false;
            try { using (var process = Process.GetProcessById(pid)) alive = !process.HasExited; } catch (ArgumentException) { }
            Assert(!alive, "Child process survived cancellation.");
        }
    }
    public static int Worker(string pidFile)
    {
        File.WriteAllText(pidFile, Process.GetCurrentProcess().Id.ToString());
        Console.WriteLine("child-ready"); Console.Out.Flush();
        while (true) Thread.Sleep(1000);
    }
    private static void TestFvmSelection()
    {
        string root = Folder("fvm"); string sdk = Path.Combine(root, "versions", "3.24.0"); Directory.CreateDirectory(sdk);
        string fake = Tool("fvm", "echo fake-fvm\r\nexit /b 0\r\n"); var runner = Runner(Group("fvm-sdks", root), fake);
        var plan = runner.Prepare("fvm-sdks", "3.24.0"); Assert(plan.Target == sdk && plan.DisplayCommand.Contains("FVM_CACHE_PATH") && plan.Arguments == "remove 3.24.0", "FVM selected wrong SDK or lacked cache pinning.");
        foreach (string version in new[] { "--all", "..\\outside", "3.24.0; whoami", "", "missing" })
        {
            bool rejected = false; try { runner.Prepare("fvm-sdks", version); } catch (Exception e) { rejected = e is ArgumentException || e is IOException; }
            Assert(rejected, "FVM accepted an invalid or destructive version selector.");
        }
    }
    private static void TestActiveOwners()
    {
        foreach (string id in new[] { "gradle-caches", "codex-data" })
        {
            string root = Folder("busy-" + id); string keep = Path.Combine(root, "keep.txt"); File.WriteAllText(keep, "keep");
            var runner = Runner(Group(id, root), null, true, true);
            var result = Run(runner, id, "logs-busy-" + id);
            Assert(result.Error != null && File.Exists(keep) && result.LogPath == null, "Active owner did not block reset.");
        }
    }
    private static void TestMissingTool()
    {
        var runner = Runner(Group("go-modules", Folder("missing-tool")));
        bool failed = false; try { runner.Prepare("go-modules"); } catch (IOException) { failed = true; }
        Assert(failed, "Missing CLI was accepted.");
    }
}
