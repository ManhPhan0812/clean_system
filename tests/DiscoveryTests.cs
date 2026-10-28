using System;
using System.IO;
using System.Linq;
using System.Threading;
using CleanSystem;

internal static class DiscoveryTests
{
    private static string sandbox;

    public static void RunAll(string parent, Action<string, Action> run)
    {
        sandbox = Path.Combine(parent, "discovery-fixtures");
        Directory.CreateDirectory(sandbox);
        run("tool query resolves a Go cache, query failure falls back to the variable", TestToolCacheResolution);
        run("discovered rows are InspectOnly, deduplicated and never runnable", TestDiscoveredRows);
        run("discovered rows outside C are blocked, never measured or deleted", TestDiscoveredOffDriveBlocked);
        run("a group with an explicit policy override runs its own command, not the Id mapping", TestPolicyOverride);
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }

    private static string Folder(string name)
    {
        string path = Path.Combine(sandbox, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TestToolCacheResolution()
    {
        string queried = Folder("tool-go-modcache");
        string fallback = Folder("env-go-modcache");
        var saved = SystemDiscovery.QueryToolOutput;
        try
        {
            SystemDiscovery.QueryToolOutput = (tool, args) => tool == "go" && args == "env GOMODCACHE" ? queried : null;
            Assert(SystemDiscovery.ResolveToolCache("go", "env GOMODCACHE", fallback, "GOMODCACHE") == PathPolicy.Normalize(queried),
                "Tool answer did not win over the fallback path.");
            SystemDiscovery.QueryToolOutput = (tool, args) => "not-a-path";
            string previous = Environment.GetEnvironmentVariable("GOMODCACHE");
            try
            {
                Environment.SetEnvironmentVariable("GOMODCACHE", fallback);
                Assert(SystemDiscovery.ResolveToolCache("go", "env GOMODCACHE", Path.Combine(sandbox, "unused-default"), "GOMODCACHE") == PathPolicy.Normalize(fallback),
                    "Unusable tool answer did not fall back to the environment variable.");
            }
            finally { Environment.SetEnvironmentVariable("GOMODCACHE", previous); }
            SystemDiscovery.QueryToolOutput = (tool, args) => { throw new InvalidOperationException("query failed"); };
            Assert(SystemDiscovery.ResolveToolCache("go", "env GOMODCACHE", fallback, "GOMODCACHE") == PathPolicy.Normalize(fallback),
                "Throwing tool query did not fall back to the default path.");
        }
        finally { SystemDiscovery.QueryToolOutput = saved; }
    }

    private static void TestDiscoveredRows()
    {
        string home = Folder("fake-home");
        string cacheLike = Path.Combine(home, ".my-cache");
        Directory.CreateDirectory(cacheLike);
        File.WriteAllText(Path.Combine(cacheLike, "data.bin"), "cached");
        string sensitive = Path.Combine(home, ".ssh");
        Directory.CreateDirectory(sensitive);
        string known = Folder("known-cache");
        string npmCache = Folder("npm-cache");
        var saved = SystemDiscovery.QueryToolOutput;
        try
        {
            SystemDiscovery.QueryToolOutput = (tool, args) =>
            {
                if (tool == "npm") return npmCache;
                if (tool == "pip") return known;
                return null;
            };
            var groups = SystemDiscovery.DiscoverExtraGroups(home, new[] { known });
            Assert(groups.Any(g => g.Root == PathPolicy.Normalize(npmCache) && g.Id == "discovered-npm-cache"), "npm tool cache was not discovered.");
            Assert(groups.Any(g => g.Root == PathPolicy.Normalize(cacheLike)), "Cache-like home folder was not discovered.");
            Assert(!groups.Any(g => g.Root == PathPolicy.Normalize(known)), "Known root was discovered a second time.");
            Assert(!groups.Any(g => g.Root == PathPolicy.Normalize(sensitive)), "Non-cache home folder was surfaced as a cache.");
            Assert(groups.All(g => g.IsDeveloper && g.Rule == CleanupRule.InspectOnly && !g.CanClean && g.EffectiveAction == CommandAction.None && !g.CanRunCommand),
                "A discovered row was cleanable or runnable.");
            bool rejected = false;
            try { new CommandRunner(groups, false, name => null, () => false, () => false).Prepare(groups[0].Id); }
            catch (ArgumentException) { rejected = true; }
            Assert(rejected, "CommandRunner accepted a discovered row.");
            var engine = new Cleaner(groups);
            var scanned = engine.Scan(0, CancellationToken.None, null, true).Groups.Single(g => g.Root == PathPolicy.Normalize(cacheLike));
            Assert(scanned.Measured && scanned.TotalCount == 1 && scanned.Files.Count == 0, "Discovered row was not measured, or it produced deletion candidates.");
        }
        finally { SystemDiscovery.QueryToolOutput = saved; }
    }

    private static void TestDiscoveredOffDriveBlocked()
    {
        var groups = new[]
        {
            new CleanupGroup("discovered-off-drive", "Off-drive cache", "", @"D:\clean-system-discovered", true, CleanupRule.InspectOnly)
        };
        var engine = new Cleaner(groups, null, null, true);
        var blocked = engine.NewGroups().Single();
        Assert(blocked.BlockedReason != null && !blocked.CanClean && !blocked.CanRunCommand && !blocked.Selected, "Off-drive discovered row was not blocked.");
        var scan = engine.Scan(0, CancellationToken.None, null, true);
        Assert(scan.Groups.Count == 1 && !scan.Groups[0].Measured, "Scan measured an off-drive discovered row.");
    }

    private static void TestPolicyOverride()
    {
        string root = Folder("override-root");
        File.WriteAllText(Path.Combine(root, "file.txt"), "data");
        var group = new CleanupGroup("custom-id", "Custom", "", root, true, CleanupRule.InspectOnly, "", "", "", CommandAction.ClearContents, null, "test-override");
        group = new Cleaner(new[] { group }).NewGroups().Single();
        Assert(group.EffectiveAction == CommandAction.ClearContents && group.CanRunCommand, "Policy override did not take effect.");
        var runner = new CommandRunner(new[] { group }, false, name => null, () => false, () => false);
        var plan = runner.Prepare("custom-id");
        Assert(plan.Action == CommandAction.ClearContents, "Runner ignored the group's policy override.");
        var result = runner.Run(plan, Path.Combine(sandbox, "logs-override"), CancellationToken.None, null);
        Assert(result.Error == null && Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any(), "Override clear command did not run: " + result.Error);
    }
}
