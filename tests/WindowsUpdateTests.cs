using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CleanSystem;

internal static class WindowsUpdateTests
{
    private sealed class Outcome { public int ExitCode; public string Output; }
    private static string sandbox;
    public static void RunAll(string parent, Action<string, Action> run)
    {
        sandbox = Path.Combine(parent, "windows-update-fixtures"); Directory.CreateDirectory(sandbox);
        run("Windows Update catalog targets Download only and always includes the requested warning", () => {
            var group = new Cleaner().NewGroups().Single(g => g.Id == WindowsUpdateCache.GroupId);
            var plan = new CommandRunner().Prepare(group.Id);
            Assert(group.Root == WindowsUpdateCache.Root && group.Root.EndsWith(@"\SoftwareDistribution\Download") && group.IsDeveloper && group.CanRunCommand && !group.CanClean && !group.Selected, "Update cache scope or policy is wrong.");
            Assert(plan.Description.Contains(WindowsUpdateCache.Warning) && plan.DisplayCommand.Contains("finally") && !plan.DisplayCommand.Contains("DataStore"), "Warning or service recovery is missing.");
        });
        run("Windows Update command rejects missing administrator rights before execution", () => {
            string root = Fixture("no-admin"); string keep = Path.Combine(root, "keep.txt"); File.WriteAllText(keep, "keep");
            var runner = Runner(root, false);
            var result = runner.Run(runner.Prepare(WindowsUpdateCache.GroupId), Path.Combine(sandbox, "admin-logs"), CancellationToken.None, null);
            Assert(result.Error != null && result.Error.Contains("Run as administrator") && result.LogPath == null && File.Exists(keep), "Non-admin update cleanup started.");
        });
        run("non-admin Other scan skips Windows Update while other groups remain accessible", () => {
            string updateRoot = Fixture("scan-update"); File.WriteAllText(Path.Combine(updateRoot, "update.cab"), "update");
            string otherRoot = Fixture("scan-other"); File.WriteAllText(Path.Combine(otherRoot, "cache.txt"), "cache");
            var groups = new[] {
                new CleanupGroup(WindowsUpdateCache.GroupId, "Update fixture", "", updateRoot, true, CleanupRule.InspectOnly),
                new CleanupGroup("generic-cache", "Other fixture", "", otherRoot, true, CleanupRule.InspectOnly)
            };
            var denied = new Cleaner(groups, null, () => false);
            var result = denied.Scan(7, CancellationToken.None, null, true);
            Assert(result.Inspected == 1 && !result.Groups[0].Measured && result.Groups[0].TotalCount == 0 && result.Groups[0].State.Contains("không được truy cập") && result.Groups[1].Measured, "Non-admin scan accessed Windows Update or blocked Other.");
            bool rejected = false;
            try { denied.Scan(7, CancellationToken.None, null, true, WindowsUpdateCache.GroupId); } catch (UnauthorizedAccessException e) { rejected = e.Message == WindowsUpdateCache.AccessWarning; }
            Assert(rejected, "Non-admin direct Windows Update scan was allowed.");
            var allowed = new Cleaner(groups, null, () => true).Scan(7, CancellationToken.None, null, true, WindowsUpdateCache.GroupId);
            Assert(allowed.Inspected == 1 && allowed.Groups[0].Measured && allowed.Groups[0].TotalCount == 1, "Administrator scan was blocked.");
        });
        run("Windows Update script clears only Download and restarts both mocked services", () => {
            string root = Fixture("O'Brien update downloads"); string nested = Path.Combine(root, "nested"); Directory.CreateDirectory(nested);
            string hidden = Path.Combine(root, "hidden.txt"); File.WriteAllText(hidden, "hidden"); File.SetAttributes(hidden, FileAttributes.Hidden | FileAttributes.ReadOnly);
            File.WriteAllText(Path.Combine(nested, "update.cab"), "fixture");
            string sibling = Path.Combine(Path.GetDirectoryName(root), "DataStore"); Directory.CreateDirectory(sibling); string keep = Path.Combine(sibling, "keep.edb"); File.WriteAllText(keep, "keep");
            string trace = Trace("success"); var result = Execute(root, trace, "");
            Assert(result.ExitCode == 0 && Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any() && File.Exists(keep), "Update deletion exceeded scope or failed: " + result.Output);
            Assert(File.ReadAllLines(trace).SequenceEqual(new[] { "STOP wuauserv", "STOP bits", "START wuauserv", "START bits" }), "Service sequence is wrong.");
        });
        run("Windows Update stop failure preserves files and attempts both service restarts", () => {
            foreach (string failing in new[] { "wuauserv", "bits" }) {
                string root = Fixture("stop-failure-" + failing); string keep = Path.Combine(root, "keep.txt"); File.WriteAllText(keep, "keep");
                string trace = Trace("stop-" + failing); var result = Execute(root, trace, "$script:fakeStopFailure = " + CommandRunner.Quote(failing) + "\n");
                Assert(result.ExitCode != 0 && File.Exists(keep), "Deletion continued after service stop failure.");
                Assert(File.ReadAllLines(trace).Contains("START wuauserv") && File.ReadAllLines(trace).Contains("START bits"), "Stop failure left a service without a restart attempt.");
            }
        });
        run("Windows Update locked file stops deletion without retries and still restarts services", () => {
            string root = Fixture("locked"); string locked = Path.Combine(root, "a-locked.cab"); string later = Path.Combine(root, "z-later.cab");
            File.WriteAllText(locked, "locked"); File.WriteAllText(later, "later"); string trace = Trace("locked");
            using (var handle = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                var result = Execute(root, trace, "");
                Assert(result.ExitCode != 0 && File.Exists(locked) && File.Exists(later), "Locked file was forced or cleanup continued after its error.");
            }
            Assert(File.ReadAllLines(trace).Contains("START wuauserv") && File.ReadAllLines(trace).Contains("START bits"), "Locked file prevented service recovery.");
        });
        run("Windows Update restart failure still attempts the other service and reports failure", () => {
            string root = Fixture("restart-failure"); File.WriteAllText(Path.Combine(root, "update.cab"), "fixture"); string trace = Trace("restart-failure");
            var result = Execute(root, trace, "$script:fakeStartFailure = 'wuauserv'\n");
            Assert(result.ExitCode != 0 && result.Output.Contains("wuauserv") && File.ReadAllLines(trace).Contains("START bits"), "Restart failure was hidden or prevented the second restart.");
        });
        run("Windows Update reboot, busy installer, disabled service and unreadable status block all mutations", () => {
            foreach (string state in new[] { "Reboot", "Busy", "Disabled", "ProbeFailure" }) {
                string root = Fixture("blocked-" + state); string keep = Path.Combine(root, "keep.cab"); File.WriteAllText(keep, "keep"); string trace = Trace("blocked-" + state);
                var result = Execute(root, trace, "$script:fake" + state + " = $true\n");
                Assert(result.ExitCode != 0 && File.Exists(keep) && !File.Exists(trace), "Blocked update state changed services/files: " + state);
            }
        });
        run("Windows Update revalidates links after stopping services before deleting any file", () => {
            string root = Fixture("late-link"); string keep = Path.Combine(root, "keep.cab"); File.WriteAllText(keep, "keep");
            string outside = Fixture("late-link-outside"); string important = Path.Combine(outside, "important.txt"); File.WriteAllText(important, "important");
            string trace = Trace("late-link");
            string action = @"$script:fakeAfterStop = { Microsoft.PowerShell.Management\New-Item -ItemType Junction -Path " + CommandRunner.Quote(Path.Combine(root, "linked")) + " -Target " + CommandRunner.Quote(outside) + " -ErrorAction Stop | Out-Null }\n";
            var result = Execute(root, trace, action);
            Assert((File.GetAttributes(Path.Combine(root, "linked")) & FileAttributes.ReparsePoint) != 0, "Late junction fixture was not created.");
            Assert(result.ExitCode != 0 && result.Output.Contains("Linked descendant") && File.Exists(keep) && File.Exists(important) && File.ReadAllLines(trace).Contains("START bits"), "Late junction was traversed or recovery skipped.");
        });
        run("Windows Update disappearing target reports failure and still restores services", () => {
            string root = Fixture("disappearing-target"); File.WriteAllText(Path.Combine(root, "keep.cab"), "keep"); string moved = root + "-moved"; string trace = Trace("disappearing-target");
            var result = Execute(root, trace, "$script:fakeAfterStop = { [IO.Directory]::Move(" + CommandRunner.Quote(root) + ", " + CommandRunner.Quote(moved) + ") }\n");
            Assert(result.ExitCode != 0 && File.Exists(Path.Combine(moved, "keep.cab")) && File.ReadAllLines(trace).Contains("START wuauserv") && File.ReadAllLines(trace).Contains("START bits"), "Missing target bypassed recovery or was misreported as success.");
        });
        run("Windows Update audit failure prevents service or file changes", () => {
            string root = Fixture("audit-blocked"); string keep = Path.Combine(root, "keep.cab"); File.WriteAllText(keep, "keep");
            string logFile = Path.Combine(sandbox, "log-is-file"); File.WriteAllText(logFile, "block");
            var runner = Runner(root, true); var result = runner.Run(runner.Prepare(WindowsUpdateCache.GroupId), logFile, CancellationToken.None, null);
            Assert(result.Error != null && result.LogPath == null && File.Exists(keep), "Update operation ran without its audit log.");
        });
        run("Windows Update worker survives owner job disposal to finish service recovery", () => {
            string root = Fixture("owner-close"); File.WriteAllText(Path.Combine(root, "update.cab"), "fixture"); string trace = Trace("owner-close");
            var result = Execute(root, trace, "$script:fakeAfterStop = { Start-Sleep -Milliseconds 300 }\n", true);
            Assert(result.ExitCode == 0 && File.ReadAllLines(trace).Contains("START wuauserv") && File.ReadAllLines(trace).Contains("START bits"), "Owner job disposal killed service recovery.");
        });
        run("Windows Update supports paths longer than MAX_PATH during validation and cleanup", () => {
            string root = Fixture("long-path"); string deep = root;
            while (deep.Length < 330) deep = Path.Combine(deep, new string('d', 45));
            Directory.CreateDirectory(PathPolicy.IOPath(deep)); string file = Path.Combine(deep, "update.cab"); File.WriteAllText(PathPolicy.IOPath(file), "fixture");
            int checkedCount = -1;
            CommandRunner.ValidateTree(root, CancellationToken.None, (count, path) => checkedCount = count);
            Assert(checkedCount > 0, "Long-path validation did not report completion.");
            string trace = Trace("long-path"); var result = Execute(root, trace, "");
            Assert(result.ExitCode == 0 && Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any() && File.ReadAllLines(trace).Contains("START bits"), "Long-path cleanup failed: " + result.Output);
        });
        run("canceling Windows Update validation never launches a worker or changes services", () => {
            string root = Fixture("cancel-preflight"); string keep = Path.Combine(root, "keep.cab"); File.WriteAllText(keep, "keep");
            var runner = Runner(root, true); bool boundary = false;
            using (var canceled = new CancellationTokenSource()) {
                var result = runner.Run(runner.Prepare(WindowsUpdateCache.GroupId), Path.Combine(sandbox, "cancel-logs"), canceled.Token,
                    line => { if (line.StartsWith("Kiểm tra:")) canceled.Cancel(); }, () => boundary = true);
                Assert(result.Canceled && !result.Started && !boundary && File.Exists(keep) && File.ReadAllText(result.LogPath).Contains("END precheck=canceled"), "Preflight cancellation crossed the service boundary or was not logged.");
            }
        });
        run("validation deadlines stop read-only checks before service or file changes", () => {
            string root = Fixture("validation-timeout"); string keep = Path.Combine(root, "keep.cab"); File.WriteAllText(keep, "keep"); bool timedOut = false;
            try { CommandRunner.ValidateTree(root, CancellationToken.None, null, TimeSpan.Zero); } catch (TimeoutException) { timedOut = true; }
            Assert(timedOut && File.Exists(keep), "C# validation deadline did not stop the check.");
            string trace = Trace("validation-timeout"); var result = Execute(root, trace, "", false, 0);
            Assert(result.ExitCode != 0 && !File.Exists(trace) && File.Exists(keep), "Child validation deadline changed services/files.");
        });
        run("missing drive cannot loop forever while searching for a validation ancestor", () => {
            var occupied = DriveInfo.GetDrives().Select(d => Char.ToUpperInvariant(d.Name[0])).ToArray();
            char unused = Enumerable.Range('A', 26).Select(n => (char)n).First(letter => !occupied.Contains(letter));
            bool rejected = false;
            try { CommandRunner.ValidateTree(unused + @":\missing\cache", CancellationToken.None, null, TimeSpan.FromSeconds(2)); }
            catch (IOException) { rejected = true; }
            Assert(rejected, "Missing drive validation did not terminate with an error.");
        });
        run("service stop timeout does not start deleting and still attempts both restarts", () => {
            string root = Fixture("stop-timeout"); string keep = Path.Combine(root, "keep.cab"); File.WriteAllText(keep, "keep"); string trace = Trace("stop-timeout");
            var result = Execute(root, trace, "$script:fakeWaitTimeout = $true\n");
            Assert(result.ExitCode != 0 && File.Exists(keep) && File.ReadAllLines(trace).Contains("START wuauserv") && File.ReadAllLines(trace).Contains("START bits"), "Service wait timeout skipped recovery or started deletion.");
        });
    }
    private static CommandRunner Runner(string root, bool admin)
    {
        return new CommandRunner(new[] { new CleanupGroup(WindowsUpdateCache.GroupId, "Update fixture", "", root, true, CleanupRule.InspectOnly) }, false, name => null, () => false, () => false, () => admin);
    }
    private static string Fixture(string name) { string root = Path.Combine(sandbox, name); Directory.CreateDirectory(root); return root; }
    private static string Trace(string name) { return Path.Combine(sandbox, name + ".events"); }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static Outcome Execute(string root, string trace, string options, bool closeJob = false, int validationTimeoutSeconds = 60)
    {
        // All service and Windows Update COM commands are shadowed by fixture-only mocks in a clean PowerShell process.
        // Production is never invoked through CommandRunner.Run here, so no real service can be stopped.
        string script = "Add-Type -AssemblyName System.ServiceProcess\n$script:fakeTrace = " + CommandRunner.Quote(trace) + "\n" + options + @"
$script:fakeStates = @{ wuauserv = 'Running'; bits = 'Running' }
function New-Object {
    [CmdletBinding(DefaultParameterSetName='Type')]
    param([Parameter(Position=0,ParameterSetName='Type')][string]$TypeName, [Parameter(Position=1,ParameterSetName='Type')][object[]]$ArgumentList, [Parameter(ParameterSetName='Com')][string]$ComObject)
    if ($ComObject) {
        if ($script:fakeProbeFailure) { throw 'Mock update status unavailable.' }
        if ($ComObject -eq 'Microsoft.Update.SystemInfo') { return [pscustomobject]@{ RebootRequired = [bool]$script:fakeReboot } }
        if ($ComObject -eq 'Microsoft.Update.Session') {
            $session = [pscustomobject]@{}
            $session | Add-Member ScriptMethod CreateUpdateInstaller { return [pscustomobject]@{ IsBusy = [bool]$script:fakeBusy } }
            return $session
        }
        throw 'Unexpected COM request; never invoke real WUA.'
    }
    Microsoft.PowerShell.Utility\New-Object @PSBoundParameters
}
function Get-Service {
    [CmdletBinding()] param([string]$Name)
    if ($Name -notin @('wuauserv','bits')) { throw 'Unexpected service name.' }
    $service = [pscustomobject]@{ Name=$Name; Status=$script:fakeStates[$Name]; StartType=$(if ($script:fakeDisabled) { 'Disabled' } else { 'Manual' }) }
    $service | Add-Member ScriptMethod WaitForStatus { param($expected,$timeout) if ($script:fakeWaitTimeout -and $expected.ToString() -eq 'Stopped') { throw 'Mock service wait timeout.' }; if ($script:fakeStates[$this.Name] -ne $expected.ToString()) { throw 'Mock service did not reach expected state.' } }
    return $service
}
function Stop-Service {
    [CmdletBinding()] param([string]$Name,[switch]$Force,[switch]$NoWait)
    if ($Name -notin @('wuauserv','bits')) { throw 'Unexpected service name.' }
    [IO.File]::AppendAllText($script:fakeTrace, ('STOP ' + $Name + [Environment]::NewLine))
    if ($script:fakeStopFailure -eq $Name) { throw ('Mock stop failed: ' + $Name) }
    $script:fakeStates[$Name] = 'Stopped'
    if ($Name -eq 'bits' -and $script:fakeAfterStop) { & $script:fakeAfterStop }
}
function Start-Service {
    [CmdletBinding()] param([string]$Name)
    if ($Name -notin @('wuauserv','bits')) { throw 'Unexpected service name.' }
    [IO.File]::AppendAllText($script:fakeTrace, ('START ' + $Name + [Environment]::NewLine))
    if ($script:fakeStartFailure -eq $Name) { throw ('Mock restart failed: ' + $Name) }
    $script:fakeStates[$Name] = 'Running'
}
" + CommandRunner.BuildScript(Runner(root, true).Prepare(WindowsUpdateCache.GroupId), validationTimeoutSeconds);
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        var info = new ProcessStartInfo(powershell, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script))) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        using (var process = Process.Start(info)) {
            ProcessJob job = null;
            if (closeJob) { job = new ProcessJob(false); job.Attach(process); }
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            process.StandardInput.WriteLine("RUN"); process.StandardInput.Close();
            if (job != null) job.Dispose();
            if (!process.WaitForExit(20000)) { process.Kill(); throw new Exception("Mock update script timed out."); }
            Task.WaitAll(stdout, stderr);
            return new Outcome { ExitCode = process.ExitCode, Output = stdout.Result + stderr.Result };
        }
    }
}
