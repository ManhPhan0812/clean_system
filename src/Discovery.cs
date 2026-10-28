using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace CleanSystem
{
    // Hybrid discovery for the Other / developer list.
    //
    // The list is no longer only the hardcoded paths in Cleaner.DeveloperGroups:
    // roots are resolved, in order, from the tool itself, then from the tool's
    // documented environment variable, then from the default location. Extra
    // rows found on this machine are added as InspectOnly rows only.
    //
    // Safety invariant, unchanged from before: discovery can add a row to be
    // *measured*, but it can never promote a row to deletable or runnable.
    // Only the reviewed policies in Cleaner/CommandRunner do that, and a
    // discovered row has EffectiveAction == None, so neither the Clean button
    // nor RunCommand will touch it. Cleaner still blocks roots outside C:.
    internal static class SystemDiscovery
    {
        // Query a tool and return its first non-empty output line, or null.
        // Replaceable in tests so discovery never depends on the real machine.
        internal static Func<string, string, string> QueryToolOutput = DefaultQueryToolOutput;

        private const int QueryTimeoutMilliseconds = 3000;

        // Tool-reported cache location wins; otherwise fall back to the
        // documented environment variable and then to the default path.
        // Go is the main case: `go env` itself honours GOMODCACHE/GOCACHE,
        // so a tool answer never overrides an explicit variable for Go.
        internal static string ResolveToolCache(string tool, string arguments, string fallback, string environmentVariable)
        {
            string queried = null;
            try
            {
                string output = QueryToolOutput == null ? null : QueryToolOutput(tool, arguments);
                if (!String.IsNullOrWhiteSpace(output))
                {
                    string candidate = output.Trim().Trim('"');
                    if (Path.IsPathRooted(candidate)) queried = PathPolicy.Normalize(candidate);
                }
            }
            catch (Exception) { }
            if (queried != null) return queried;
            return RootResolution.Resolve(fallback, environmentVariable);
        }

        internal static string ResolveToolCache(string tool, string arguments, string fallback, params string[] environmentVariables)
        {
            string queried = null;
            try
            {
                string output = QueryToolOutput == null ? null : QueryToolOutput(tool, arguments);
                if (!String.IsNullOrWhiteSpace(output))
                {
                    string candidate = output.Trim().Trim('"');
                    if (Path.IsPathRooted(candidate)) queried = PathPolicy.Normalize(candidate);
                }
            }
            catch (Exception) { }
            if (queried != null) return queried;
            return RootResolution.Resolve(fallback, environmentVariables);
        }

        // Extra rows found on this machine. knownRoots are the roots already
        // in the list (temp, WER and the reviewed developer groups); anything
        // colliding with them, the profile root itself, or an unusable path
        // is skipped instead of shown twice.
        internal static List<CleanupGroup> DiscoverExtraGroups(string userProfile, IEnumerable<string> knownRoots)
        {
            var discovered = new List<CleanupGroup>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (knownRoots != null)
            {
                foreach (string known in knownRoots)
                {
                    try { seen.Add(PathPolicy.Normalize(known)); } catch (Exception) { }
                }
            }
            Action<CleanupGroup> add = delegate(CleanupGroup group)
            {
                if (group == null) return;
                try
                {
                    string root = PathPolicy.Normalize(group.Root);
                    if (!Path.IsPathRooted(root)) return;
                    if (!String.IsNullOrEmpty(userProfile) && String.Equals(root, PathPolicy.Normalize(userProfile), StringComparison.OrdinalIgnoreCase)) return;
                    if (seen.Contains(root)) return;
                    seen.Add(root);
                    discovered.Add(group);
                }
                catch (Exception) { }
            };

            // Caches reported by tools actually installed on this machine.
            // These are measurement-only: the app does not run their clean
            // commands, it only shows where the tool keeps its cache.
            string npmCache = QueryCachePath("npm", "config get cache");
            if (npmCache != null) add(new CleanupGroup("discovered-npm-cache", "npm · cache", "Phát hiện từ npm trên máy này · chỉ thống kê, chưa có lệnh dọn được review", npmCache, true, CleanupRule.InspectOnly,
                "Mục phát hiện từ hệ thống, chỉ đo dung lượng. Ứng dụng không tự dọn mục này; dùng lệnh của chính công cụ nếu cần, sau khi kiểm tra dữ liệu cần giữ.", "", "https://docs.npmjs.com/cli/v11/commands/npm-cache", null, null, "tool:npm config get cache"));
            string pipCache = QueryCachePath("pip", "cache dir");
            if (pipCache != null) add(new CleanupGroup("discovered-pip-cache", "pip · cache", "Phát hiện từ pip trên máy này · chỉ thống kê, chưa có lệnh dọn được review", pipCache, true, CleanupRule.InspectOnly,
                "Mục phát hiện từ hệ thống, chỉ đo dung lượng. Ứng dụng không tự dọn mục này; dùng lệnh của chính công cụ nếu cần, sau khi kiểm tra dữ liệu cần giữ.", "", "https://pip.pypa.io/en/stable/cli/pip_cache/", null, null, "tool:pip cache dir"));
            string nugetCache = QueryDotnetGlobalPackages();
            if (nugetCache != null) add(new CleanupGroup("discovered-nuget-packages", "NuGet · global packages", "Phát hiện từ dotnet trên máy này · chỉ thống kê, chưa có lệnh dọn được review", nugetCache, true, CleanupRule.InspectOnly,
                "Mục phát hiện từ hệ thống, chỉ đo dung lượng. Ứng dụng không tự dọn mục này; dùng lệnh của chính công cụ nếu cần, sau khi kiểm tra dữ liệu cần giữ.", "", "https://learn.microsoft.com/en-us/nuget/consume-packages/managing-the-global-packages-and-cache-folders", null, null, "tool:dotnet nuget locals"));

            // Dot-directories in the user profile that look like tool caches.
            // The name rule is deliberately narrow, and it never matches a
            // container folder (for example .nuget, .cargo or .gradle, whose
            // cache is a subfolder already covered by a known row): anything
            // else (for example .ssh or .gnupg) is never surfaced either.
            if (!String.IsNullOrWhiteSpace(userProfile))
            {
                string[] directories = null;
                try { directories = Directory.GetDirectories(userProfile); } catch (Exception) { directories = null; }
                if (directories != null)
                {
                    foreach (string directory in directories)
                    {
                        string name = Path.GetFileName(directory);
                        if (String.IsNullOrEmpty(name) || !name.StartsWith(".", StringComparison.Ordinal)) continue;
                        if (!IsCacheLikeDirectory(name)) continue;
                        string id = "discovered-" + name.TrimStart('.').ToLowerInvariant().Replace(".", "-").Replace("_", "-");
                        add(new CleanupGroup(id, name + " · phát hiện trên máy", "Phát hiện từ thư mục home trên máy này · chỉ thống kê, chưa có lệnh dọn được review", directory, true, CleanupRule.InspectOnly,
                            "Mục phát hiện từ hệ thống, chỉ đo dung lượng. Ứng dụng không tự dọn mục này vì chưa có chính sách dọn được review cho nó; kiểm tra bằng chính công cụ tạo ra thư mục trước khi xóa thủ công.", "", "", null, null, "home-scan"));
                    }
                }
            }
            return discovered;
        }

        private static bool IsCacheLikeDirectory(string name)
        {
            string lower = name.ToLowerInvariant();
            if (lower.Contains("cache")) return true;
            // npm's own cache directory and pnpm's store are the only
            // non-"cache" names admitted; both are data-only folders.
            return lower == ".npm" || lower == ".pnpm-store";
        }

        private static string QueryCachePath(string tool, string arguments)
        {
            try
            {
                string output = QueryToolOutput == null ? null : QueryToolOutput(tool, arguments);
                if (String.IsNullOrWhiteSpace(output)) return null;
                foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string candidate = line.Trim().Trim('"');
                    if (Path.IsPathRooted(candidate)) return PathPolicy.Normalize(candidate);
                }
            }
            catch (Exception) { }
            return null;
        }

        private static string QueryDotnetGlobalPackages()
        {
            try
            {
                string output = QueryToolOutput == null ? null : QueryToolOutput("dotnet", "nuget locals global-packages --list");
                if (String.IsNullOrWhiteSpace(output)) return null;
                foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string trimmed = line.Trim();
                    const string prefix = "global-packages:";
                    if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        string candidate = trimmed.Substring(prefix.Length).Trim().Trim('"');
                        if (Path.IsPathRooted(candidate)) return PathPolicy.Normalize(candidate);
                        continue;
                    }
                    if (Path.IsPathRooted(trimmed)) return PathPolicy.Normalize(trimmed);
                }
            }
            catch (Exception) { }
            return null;
        }

        private static string DefaultQueryToolOutput(string tool, string arguments)
        {
            string executable = CommandRunner.ResolveTool(tool);
            if (String.IsNullOrEmpty(executable)) return null;
            bool script = executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || executable.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
            var info = new ProcessStartInfo
            {
                FileName = script ? "cmd.exe" : executable,
                Arguments = script ? "/c \"" + tool + "\" " + arguments : arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = new UTF8Encoding(false)
            };
            using (var process = new Process { StartInfo = info })
            {
                try { process.Start(); }
                catch (Exception) { return null; }
                bool exited = process.WaitForExit(QueryTimeoutMilliseconds);
                if (!exited)
                {
                    try { process.Kill(); } catch (Exception) { }
                    try { process.WaitForExit(); } catch (Exception) { }
                    return null;
                }
                string output;
                try { output = process.StandardOutput.ReadToEnd(); } catch (Exception) { return null; }
                if (process.ExitCode != 0 || String.IsNullOrWhiteSpace(output)) return null;
                foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string candidate = line.Trim();
                    if (!String.IsNullOrEmpty(candidate)) return candidate;
                }
                return null;
            }
        }
    }
}
