using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace CleanSystem
{
    internal enum CommandAction { None, RemoveFolder, ClearContents, GoModules, GoBuild, PubCache, FvmVersion, OpenEditor, WindowsUpdateDownloads }

    internal sealed class CommandPlan
    {
        public string GroupId { get; internal set; }
        public string Root { get; internal set; }
        public string Target { get; internal set; }
        public string Version { get; internal set; }
        public string Executable { get; internal set; }
        public string Arguments { get; internal set; }
        public string Description { get; internal set; }
        public CommandAction Action { get; internal set; }
        public string DisplayCommand { get; internal set; }
    }

    internal sealed class CommandResult
    {
        public int ExitCode = -1;
        public bool Canceled;
        public bool Started;
        public string Error;
        public string LogPath;
        public string Output;
        public string Summary;
    }

    internal static class ToolPresence
    {
        private static readonly object gate = new object();
        private static readonly Dictionary<string, bool> cache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        // Cached per process: the row template reads CanRunCommand on every layout pass and each PATH probe is a syscall.
        public static bool Has(string name)
        {
            if (String.IsNullOrEmpty(name)) return true;
            lock (gate) { bool known; if (cache.TryGetValue(name, out known)) return known; }
            bool found;
            try { found = !String.IsNullOrEmpty(CommandRunner.ResolveTool(name)); }
            catch (Exception) { found = false; }
            lock (gate) { cache[name] = found; }
            return found;
        }
        public static void Reset()
        {
            lock (gate) cache.Clear();
        }
    }

    internal sealed class CommandRunner
    {
        private readonly List<CleanupGroup> definitions;
        private readonly bool requireCDrive;
        private readonly Func<string, string> resolveTool;
        private readonly Func<bool> javaActive;
        private readonly Func<bool> codexActive;
        private readonly Func<bool> administrator;

        public CommandRunner() : this(new Cleaner().NewGroups(), true, ResolveTool, Cleaner.JavaMayBeActive, CodexMayBeActive) { }
        internal CommandRunner(IEnumerable<CleanupGroup> groups, bool requireC, Func<string, string> resolver, Func<bool> javaProbe, Func<bool> codexProbe, Func<bool> adminProbe = null)
        {
            definitions = groups.ToList(); requireCDrive = requireC;
            resolveTool = resolver; javaActive = javaProbe; codexActive = codexProbe;
            administrator = adminProbe ?? WindowsUpdateCache.IsAdministrator;
        }
        public static CommandAction ActionFor(string id)
        {
            switch (id)
            {
                case WindowsUpdateCache.GroupId: return CommandAction.WindowsUpdateDownloads;
                case "gradle-caches": case "gradle-daemon": case "gitnexus-data": return CommandAction.RemoveFolder;
                case "maven-repository": case "generic-cache": case "codex-data": case "gemini-data": return CommandAction.ClearContents;
                case "go-modules": return CommandAction.GoModules;
                case "go-build": return CommandAction.GoBuild;
                case "pub-cache": return CommandAction.PubCache;
                case "fvm-sdks": return CommandAction.FvmVersion;
                case "vscode-extensions": case "cursor-extensions": return CommandAction.OpenEditor;
                default: return CommandAction.None;
            }
        }
        // A command that needs a CLI is only offered when that CLI is on PATH; the mapping is fixed per id.
        public static string ToolFor(string id)
        {
            switch (id)
            {
                case "go-modules": case "go-build": return "go";
                case "pub-cache": return "flutter";
                case "fvm-sdks": return "fvm";
                case "vscode-extensions": return "code";
                case "cursor-extensions": return "cursor";
                default: return null;
            }
        }
        public static string ButtonText(string id)
        {
            return ButtonTextForAction(ActionFor(id), id);
        }
        public static string ButtonTextForAction(CommandAction action, string id)
        {
            switch (action)
            {
                case CommandAction.WindowsUpdateDownloads: return "Dọn cache cập nhật";
                case CommandAction.FvmVersion: return "Chọn SDK / chạy";
                case CommandAction.OpenEditor: return id == "cursor-extensions" ? "Mở Cursor" : "Mở VS Code";
                default: return "Chạy lệnh";
            }
        }
        private CleanupGroup Definition(string id)
        {
            var group = definitions.SingleOrDefault(g => g.Id == id && g.IsDeveloper);
            if (group == null || group.EffectiveAction == CommandAction.None) throw new ArgumentException("Mục này không có lệnh được hỗ trợ.");
            if (requireCDrive && !PathPolicy.IsOnC(group.Root)) throw new IOException("Lệnh chỉ được chạy trong các thư mục cho phép trên ổ C.");
            return group;
        }
        public CommandPlan Prepare(string id, string version = null)
        {
            var group = Definition(id);
            var plan = new CommandPlan { GroupId = id, Root = group.Root, Target = group.Root, Action = group.EffectiveAction, Version = version };
            switch (plan.Action)
            {
                case CommandAction.WindowsUpdateDownloads:
                    plan.DisplayCommand = WindowsUpdateCache.DisplayCommand(group.Root);
                    plan.Description = WindowsUpdateCache.Guidance;
                    break;
                case CommandAction.RemoveFolder:
                    plan.DisplayCommand = "Remove-Item -LiteralPath " + Quote(group.Root) + " -Recurse -Force";
                    plan.Description = id == "gitnexus-data" ? "Xóa toàn bộ .gitnexus của tài khoản, gồm indexes và registry.json. Dừng GitNexus server/MCP trước khi chạy." : "Xóa toàn bộ thư mục, gồm dependency/cache hoặc dữ liệu daemon. Đóng IDE, dừng build và các Gradle daemon trước khi chạy.";
                    break;
                case CommandAction.ClearContents:
                    plan.DisplayCommand = "Get-ChildItem -LiteralPath " + Quote(group.Root) + " -Force | Remove-Item -Recurse -Force";
                    plan.Description = id == "maven-repository" ? "Xóa mọi artifact trong repository, gồm artifact nội bộ chưa publish. Giữ thư mục repository và settings.xml ở bên ngoài. Có thể không khôi phục được artifact nội bộ." :
                        id == "codex-data" ? "Xóa toàn bộ nội dung .codex: cấu hình, đăng nhập, lịch sử, skills, plugins và worktrees có thể mất vĩnh viễn. Đóng Codex trước khi chạy. Giữ thư mục gốc." :
                        id == "gemini-data" ? "Xóa toàn bộ nội dung .gemini, gồm cấu hình, xác thực và phiên làm việc nếu có. Đóng Gemini trước khi chạy. Giữ thư mục gốc." :
                        "Xóa mọi nội dung trong .cache của các công cụ, không chỉ file cũ. Kiểm tra và sao lưu dữ liệu cần giữ; đóng các công cụ đang dùng cache. Giữ thư mục gốc.";
                    break;
                case CommandAction.GoModules: case CommandAction.GoBuild:
                    plan.Executable = RequireTool("go"); plan.Arguments = plan.Action == CommandAction.GoModules ? "clean -modcache" : "clean -cache";
                    plan.DisplayCommand = (plan.Action == CommandAction.GoModules ? "$env:GOMODCACHE = " : "$env:GOCACHE = ") + Quote(group.Root) + "\n& " + Quote(plan.Executable) + " " + plan.Arguments;
                    plan.Description = "Đóng build Go trước khi chạy. Go dọn toàn bộ cache này, không áp dụng ngưỡng tuổi file. Dependency/cache cần tải hoặc tạo lại. Không xóa go/src hoặc go/bin.";
                    break;
                case CommandAction.PubCache:
                    plan.Executable = RequireTool("flutter"); plan.Arguments = "pub cache clean --force";
                    plan.DisplayCommand = "$env:PUB_CACHE = " + Quote(group.Root) + "\n& " + Quote(plan.Executable) + " " + plan.Arguments;
                    plan.Description = "Xóa toàn bộ Pub cache sau khi xác nhận tại đây. Đóng Flutter/Dart và IDE. Cần pub get và có thể kích hoạt lại công cụ global như FVM; các thay đổi trong package cache sẽ mất.";
                    break;
                case CommandAction.FvmVersion:
                    if (String.IsNullOrEmpty(version) || !Regex.IsMatch(version, @"^(?:\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?|stable|beta|dev|master|main)$")) throw new ArgumentException("Chọn một phiên bản SDK hợp lệ, không dùng --all hoặc đường dẫn.");
                    plan.Target = PathPolicy.Normalize(Path.Combine(group.Root, "versions", version));
                    if (!PathPolicy.IsWithin(plan.Target, group.Root) || !Directory.Exists(plan.Target)) throw new IOException("SDK đã chọn không có trong thư mục versions này.");
                    plan.Executable = RequireTool("fvm"); plan.Arguments = "remove " + version;
                    plan.DisplayCommand = "$env:FVM_CACHE_PATH = " + Quote(group.Root) + "\n& " + Quote(plan.Executable) + " list\n& " + Quote(plan.Executable) + " remove " + Quote(version);
                    plan.Description = "Chỉ gỡ SDK " + version + ". Kiểm tra .fvmrc của các dự án và bản global trước khi gỡ. Phiên bản đã cũ không có nghĩa là không còn dùng. Cache Git và các SDK khác được giữ.";
                    break;
                case CommandAction.OpenEditor:
                    plan.Executable = RequireTool(id == "vscode-extensions" ? "code" : "cursor");
                    plan.DisplayCommand = id == "vscode-extensions" ? "code" : "cursor";
                    plan.Description = "Mở trình soạn thảo. Nhấn Ctrl+Shift+X, chọn tiện ích không dùng và Uninstall. Không xóa hàng loạt extensions.";
                    break;
            }
            return plan;
        }
        private string RequireTool(string name)
        {
            string executable = resolveTool(name);
            if (String.IsNullOrEmpty(executable) || !File.Exists(executable)) throw new IOException("Không tìm thấy " + name + " trong PATH. Cài công cụ hoặc mở lại ứng dụng sau khi cập nhật PATH.");
            return Path.GetFullPath(executable);
        }
        public static string ResolveTool(string name)
        {
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                if (String.IsNullOrWhiteSpace(directory)) continue;
                foreach (string extension in new[] { ".exe", ".cmd", ".bat" })
                {
                    try { string path = Path.Combine(directory.Trim().Trim('"'), name + extension); if (File.Exists(path)) return Path.GetFullPath(path); }
                    catch (ArgumentException) { }
                }
            }
            return null;
        }
        private static bool CodexMayBeActive()
        {
            foreach (string name in new[] { "Codex", "OpenAI.Codex", "ChatGPT" })
            {
                var processes = Process.GetProcessesByName(name); bool active = processes.Length > 0;
                foreach (var process in processes) process.Dispose();
                if (active) return true;
            }
            return false;
        }
        public static string Quote(string text) { return "'" + text.Replace("'", "''") + "'"; }

        internal static void ValidateTree(string target, CancellationToken token, Action<int, string> progress = null, TimeSpan? timeout = null)
        {
            var timer = Stopwatch.StartNew();
            TimeSpan limit = timeout ?? TimeSpan.FromSeconds(60);
            int checkedEntries = 0; long lastReport = 0;
            Action check = () => {
                token.ThrowIfCancellationRequested();
                if (timer.Elapsed >= limit) throw new TimeoutException("Kiểm tra thư mục vượt quá " + limit.TotalSeconds.ToString("0") + " giây. Đã dừng trước khi chạy lệnh; chưa dừng dịch vụ hoặc xóa dữ liệu.");
            };
            if (progress != null) progress(0, target);
            check();
            if (!Directory.Exists(target))
            {
                if (File.Exists(target)) throw new IOException("Đích là file, không phải thư mục.");
                string parent = Path.GetDirectoryName(target);
                while (!Directory.Exists(parent)) {
                    check();
                    if (String.IsNullOrEmpty(parent)) throw new IOException("Không tìm thấy thư mục tổ tiên truy cập được; chưa chạy lệnh.");
                    string next = Path.GetDirectoryName(parent);
                    if (String.Equals(next, parent, StringComparison.OrdinalIgnoreCase)) throw new IOException("Không xác minh được thư mục tổ tiên; chưa chạy lệnh.");
                    parent = next;
                }
                if (PathPolicy.HasLinkedAncestor(parent)) throw new IOException("Bỏ qua đường dẫn liên kết.");
                return;
            }
            if (PathPolicy.HasLinkedAncestor(target)) throw new IOException("Bỏ qua thư mục có tổ tiên là liên kết.");
            var pending = new Stack<string>(); pending.Push(target);
            while (pending.Count > 0)
            {
                check();
                string directory = pending.Pop();
                if ((File.GetAttributes(PathPolicy.IOPath(directory)) & FileAttributes.ReparsePoint) != 0) throw new IOException("Thư mục đã đổi thành đường dẫn liên kết; chưa chạy lệnh.");
                foreach (string ioEntry in Directory.EnumerateFileSystemEntries(PathPolicy.IOPath(directory)))
                {
                    string entry = PathPolicy.DisplayPath(ioEntry);
                    check(); FileAttributes attributes = File.GetAttributes(ioEntry);
                    checkedEntries++;
                    if (progress != null && (checkedEntries % 1000 == 0 || timer.ElapsedMilliseconds - lastReport >= 1000)) {
                        lastReport = timer.ElapsedMilliseconds; progress(checkedEntries, directory);
                    }
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Có đường dẫn liên kết bên trong đích; chưa chạy lệnh: " + entry);
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                }
            }
            check();
            if (progress != null) progress(checkedEntries, target);
        }
        private void ValidatePlan(CommandPlan plan)
        {
            var group = Definition(plan.GroupId);
            if (!String.Equals(group.Root, plan.Root, StringComparison.OrdinalIgnoreCase) || plan.Action != group.EffectiveAction) throw new IOException("Lệnh hoặc đích không khớp danh sách cho phép.");
            string expected = plan.Action == CommandAction.FvmVersion ? PathPolicy.Normalize(Path.Combine(group.Root, "versions", plan.Version ?? "")) : group.Root;
            if (!String.Equals(expected, plan.Target, StringComparison.OrdinalIgnoreCase)) throw new IOException("Đường dẫn đích đã bị thay đổi.");
            var fresh = Prepare(plan.GroupId, plan.Version);
            if (fresh.DisplayCommand != plan.DisplayCommand || fresh.Executable != plan.Executable || fresh.Arguments != plan.Arguments) throw new IOException("Lệnh đã thay đổi sau khi chuẩn bị; hãy chọn lại.");
            if ((group.Id == "gradle-caches" || group.Id == "gradle-daemon") && javaActive()) throw new IOException("Java/Gradle đang chạy. Đóng IDE và dừng daemon trước khi chạy lệnh.");
            if (group.Id == "codex-data" && codexActive()) throw new IOException("Codex/ChatGPT đang chạy. Đóng ứng dụng đó rồi chạy lại Dọn dẹp ổ cứng để reset .codex.");
            if (plan.Action == CommandAction.WindowsUpdateDownloads && !administrator()) throw new IOException("Mục Windows Update cần quyền quản trị. Đóng ứng dụng, nhấp phải DonDepOCung.exe và chọn Run as administrator. Chưa dừng dịch vụ hoặc xóa dữ liệu.");
        }
        internal static string BuildScript(CommandPlan plan, int validationTimeoutSeconds = 60)
        {
            if (validationTimeoutSeconds < 0) throw new ArgumentOutOfRangeException("validationTimeoutSeconds");
            string root = Quote(plan.Root), target = Quote(plan.Target);
            // Process.StandardInput may emit a console encoding BOM. Detect it before reading the job/audit gate.
            string script = "$ErrorActionPreference = 'Stop'\n[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)\n" +
                "[AppContext]::SetSwitch('Switch.System.IO.UseLegacyPathHandling', $false)\n[AppContext]::SetSwitch('Switch.System.IO.BlockLongPaths', $false)\n" +
                "$cleanSystemGateReader = New-Object System.IO.StreamReader([Console]::OpenStandardInput(), [Text.Encoding]::UTF8, $true)\n" +
                "if ($cleanSystemGateReader.ReadLine() -ne 'RUN') { exit 64 }\ntry {\n" +
                "function Get-CleanSystemIOPath([string]$path) {\n" +
                "    if ($path.StartsWith('\\\\?\\')) { return $path }\n" +
                "    if ($path.StartsWith('\\\\')) { return '\\\\?\\UNC\\' + $path.Substring(2) }\n" +
                "    return '\\\\?\\' + $path\n}\n";
            string validation = "$cleanSystemExpected = " + target + "\n" +
                "if (-not [IO.Directory]::Exists($cleanSystemExpected)) { if ([IO.File]::Exists($cleanSystemExpected)) { throw 'Target is a file.' }; if ($RequireExists) { throw 'Thu muc dich da thay doi.' }; Write-Output 'Thu muc khong ton tai; khong xoa du lieu.'; exit 0 }\n" +
                "$cleanSystemResolved = Get-Item -LiteralPath $cleanSystemExpected -Force -ErrorAction Stop\n" +
                "if (-not $cleanSystemResolved.PSIsContainer -or $cleanSystemResolved.FullName -ne $cleanSystemExpected) { throw 'Target mismatch.' }\n" +
                "$cleanSystemAncestor = $cleanSystemResolved\nwhile ($null -ne $cleanSystemAncestor) { if ($cleanSystemAncestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked ancestor.' }; $cleanSystemAncestor = $cleanSystemAncestor.Parent }\n" +
                "$cleanSystemPending = New-Object 'System.Collections.Generic.Stack[string]'\n$cleanSystemPending.Push($cleanSystemExpected)\n" +
                "$cleanSystemCheckTimer = [Diagnostics.Stopwatch]::StartNew()\n$cleanSystemChecked = 0\n$cleanSystemLastCheckReport = 0\n" +
                "[Console]::WriteLine('Dang kiem tra lai duong dan: ' + $cleanSystemExpected)\n" +
                "while ($cleanSystemPending.Count -gt 0) {\n" +
                "    if ($cleanSystemCheckTimer.Elapsed.TotalSeconds -ge " + validationTimeoutSeconds + ") { throw 'Kiem tra duong dan qua thoi gian; da dung thao tac.' }\n" +
                "    $cleanSystemDirectory = $cleanSystemPending.Pop()\n" +
                "    if ([IO.File]::GetAttributes((Get-CleanSystemIOPath $cleanSystemDirectory)) -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked directory changed.' }\n" +
                "    foreach ($cleanSystemEntry in [IO.Directory]::EnumerateFileSystemEntries((Get-CleanSystemIOPath $cleanSystemDirectory))) {\n" +
                "        if ($cleanSystemCheckTimer.Elapsed.TotalSeconds -ge " + validationTimeoutSeconds + ") { throw 'Kiem tra duong dan qua thoi gian; da dung thao tac.' }\n" +
                "        $cleanSystemChecked++\n" +
                "        if ($cleanSystemChecked % 1000 -eq 0 -or $cleanSystemCheckTimer.ElapsedMilliseconds - $cleanSystemLastCheckReport -ge 1000) {\n" +
                "            $cleanSystemLastCheckReport = $cleanSystemCheckTimer.ElapsedMilliseconds\n" +
                "            [Console]::WriteLine('Kiem tra lai: ' + $cleanSystemChecked + ' muc; ' + $cleanSystemDirectory)\n        }\n" +
                "        $cleanSystemAttributes = [IO.File]::GetAttributes($cleanSystemEntry)\n" +
                "        if ($cleanSystemAttributes -band [IO.FileAttributes]::ReparsePoint) { throw ('Linked descendant: ' + $cleanSystemEntry) }\n" +
                "        if ($cleanSystemAttributes -band [IO.FileAttributes]::Directory) { $cleanSystemPending.Push($cleanSystemEntry) }\n    }\n}\n" +
                "[Console]::WriteLine('Da kiem tra lai ' + $cleanSystemChecked + ' muc.')\n";
            script += "function Test-CleanSystemTree { param([switch]$RequireExists)\n" + validation + "}\nTest-CleanSystemTree\n";
            if (plan.Action == CommandAction.WindowsUpdateDownloads) script += WindowsUpdateCache.BuildBody(plan.Target, "Test-CleanSystemTree -RequireExists\n");
            else if (plan.Action == CommandAction.RemoveFolder || plan.Action == CommandAction.ClearContents)
                script += BuildCacheCleanup(plan.Target, plan.Action == CommandAction.RemoveFolder);
            else
            {
                if (plan.Action == CommandAction.GoModules) script += "$env:GOMODCACHE = " + root + "\n$env:GOTOOLCHAIN = 'local'\n";
                if (plan.Action == CommandAction.GoBuild) script += "$env:GOCACHE = " + root + "\n$env:GOTOOLCHAIN = 'local'\n";
                if (plan.Action == CommandAction.PubCache) script += "$env:PUB_CACHE = " + root + "\n$env:FLUTTER_SUPPRESS_ANALYTICS = 'true'\n";
                if (plan.Action == CommandAction.FvmVersion) script += "$env:FVM_CACHE_PATH = " + root + "\n& " + Quote(plan.Executable) + " list\nif ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }\n";
                script += "& " + Quote(plan.Executable) + " " + plan.Arguments + "\nexit $LASTEXITCODE\n";
            }
            return script + "} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }\n";
        }
        private static string BuildCacheCleanup(string target, bool removeRoot)
        {
            // No interactive PowerShell prompts. A per-file access/lock failure is logged and
            // skipped; changed links still stop the operation because scope cannot be trusted.
            return @"
$script:cleanSystemDeleted = 0
$script:cleanSystemSkipped = 0
function Write-CleanSystemSkipped([string]$path, [string]$reason) {
    $script:cleanSystemSkipped++
    [Console]::WriteLine('SKIPPED ' + $path + '; ' + $reason)
}
function Remove-CleanSystemCacheEntry([string]$path) {
    try {
        $ioPath = Get-CleanSystemIOPath $path
        $attributes = [IO.File]::GetAttributes($ioPath)
        if ($attributes -band [IO.FileAttributes]::ReparsePoint) { throw [InvalidOperationException]::new('Linked entry changed: ' + $path) }
        if ($attributes -band [IO.FileAttributes]::Directory) {
            foreach ($child in [IO.Directory]::EnumerateFileSystemEntries($ioPath)) { Remove-CleanSystemCacheEntry $child }
            if ([IO.File]::GetAttributes($ioPath) -band [IO.FileAttributes]::ReparsePoint) { throw [InvalidOperationException]::new('Linked directory changed: ' + $path) }
            [IO.Directory]::Delete($ioPath, $false)
        } else {
            if ($attributes -band [IO.FileAttributes]::ReadOnly) { [IO.File]::SetAttributes($ioPath, ($attributes -band (-bnot [IO.FileAttributes]::ReadOnly))) }
            [IO.File]::Delete($ioPath)
        }
        $script:cleanSystemDeleted++
        [Console]::WriteLine('DELETED ' + $path)
    } catch [UnauthorizedAccessException] { Write-CleanSystemSkipped $path $_.Exception.Message
    } catch [IO.IOException] { Write-CleanSystemSkipped $path $_.Exception.Message }
}
" + (removeRoot ? "Remove-CleanSystemCacheEntry " + Quote(target) + "\n" :
                "foreach ($cleanSystemEntry in [IO.Directory]::EnumerateFileSystemEntries((Get-CleanSystemIOPath " + Quote(target) + "))) { Remove-CleanSystemCacheEntry $cleanSystemEntry }\n") +
                "[Console]::WriteLine('SUMMARY deleted=' + $script:cleanSystemDeleted + '; skipped=' + $script:cleanSystemSkipped)\n" +
                "if ($script:cleanSystemSkipped -gt 0) { exit 2 }; exit 0\n";
        }
        public CommandResult Run(CommandPlan plan, string logDirectory, CancellationToken token, Action<string> progress, Action serviceBoundary = null)
        {
            var result = new CommandResult();
            var output = new StringBuilder(); object gate = new object(); Exception loggingError = null;
            try
            {
                token.ThrowIfCancellationRequested(); ValidatePlan(plan);
                if (plan.Action == CommandAction.OpenEditor) throw new InvalidOperationException("Mở trình soạn thảo bằng giao diện, không qua tiến trình dọn.");
                Directory.CreateDirectory(logDirectory);
                result.LogPath = Path.Combine(logDirectory, "command-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".log");
                using (var file = new FileStream(result.LogPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(file, new UTF8Encoding(true)))
                {
                    writer.AutoFlush = true;
                    Action<string> report = line => {
                        lock (gate)
                        {
                            if (loggingError == null) try { writer.WriteLine(DateTime.UtcNow.ToString("o") + " " + line); } catch (Exception e) { loggingError = e; }
                            if (output.Length < 48000) output.AppendLine(line.Length > 3000 ? line.Substring(0, 3000) : line);
                        }
                        if (progress != null) progress(line);
                    };
                    report("BEGIN " + plan.GroupId + "\nTARGET " + plan.Target + "\nCOMMAND\n" + plan.DisplayCommand);
                    if (loggingError != null) throw loggingError;
                    lock (gate) file.Flush(true);
                    report("Đang kiểm tra đường dẫn và các liên kết trước khi chạy…");
                    try {
                        ValidateTree(plan.Target, token, (count, directory) => report("Kiểm tra: " + count.ToString("N0") + " mục · " + directory));
                        ValidatePlan(plan); token.ThrowIfCancellationRequested();
                    } catch (Exception e) {
                        report("END precheck=" + (e is OperationCanceledException ? "canceled" : "failed") + "; started=False; " + e.Message);
                        lock (gate) file.Flush(true);
                        throw;
                    }
                    if (plan.Action == CommandAction.WindowsUpdateDownloads && serviceBoundary != null) serviceBoundary();
                    token.ThrowIfCancellationRequested();
                    string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
                    var info = new ProcessStartInfo(powershell, "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(BuildScript(plan)))) {
                        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                        WorkingDirectory = Path.GetDirectoryName(PathPolicy.Normalize(logDirectory))
                    };
                    bool recoveringServices = plan.Action == CommandAction.WindowsUpdateDownloads;
                    using (var job = new ProcessJob(!recoveringServices))
                    using (var process = new Process { StartInfo = info })
                    {
                        process.Start();
                        try { job.Attach(process); }
                        catch { if (!process.HasExited) process.Kill(); throw; }
                        // The script waits on stdin, so no cleanup/tool child can start before job assignment and audit flush.
                        string summaryTail = "";
                        Task stdout = Task.Run(() => ReadLines(process.StandardOutput, line => {
                            if (plan.Action == CommandAction.ClearContents || plan.Action == CommandAction.RemoveFolder) {
                                summaryTail += line;
                                var summary = Regex.Match(summaryTail, @"(?:^|\n)SUMMARY deleted=(\d+); skipped=(\d+)\r?\n");
                                if (summary.Success) result.Summary = "Đã xóa " + summary.Groups[1].Value + " file/thư mục; bỏ qua " + summary.Groups[2].Value + " file/thư mục.";
                                if (summaryTail.Length > 512) summaryTail = summaryTail.Substring(summaryTail.Length - 512);
                            }
                            report("OUT " + line);
                        }, ""));
                        Task stderr = Task.Run(() => ReadLines(process.StandardError, report, "ERR "));
                        if (token.IsCancellationRequested || loggingError != null) { result.Canceled = token.IsCancellationRequested; job.Stop(); }
                        else
                        {
                            report("START"); if (loggingError != null) { job.Stop(); throw loggingError; }
                            lock (gate) file.Flush(true);
                            result.Started = true;
                            process.StandardInput.WriteLine("RUN"); process.StandardInput.Flush(); process.StandardInput.Close();
                        }
                        var timer = Stopwatch.StartNew();
                        while (!process.WaitForExit(100))
                        {
                            if (!recoveringServices && (token.IsCancellationRequested || loggingError != null || timer.Elapsed > TimeSpan.FromMinutes(30)))
                            {
                                result.Canceled = token.IsCancellationRequested;
                                if (!result.Canceled && loggingError == null) result.Error = "Lệnh vượt quá thời gian 30 phút; đã dừng cây tiến trình.";
                                job.Stop(); break;
                            }
                        }
                        process.WaitForExit(); result.ExitCode = process.ExitCode;
                        // Prevent a detached child from continuing cleanup after its launcher exits.
                        job.Stop(); Task.WaitAll(stdout, stderr);
                        if (loggingError != null) throw new IOException(recoveringServices ? "Không ghi được nhật ký; đã chờ lệnh kết thúc bước khởi động lại dịch vụ." : "Không ghi được nhật ký; đã dừng lệnh.", loggingError);
                        bool skippedCacheItems = result.ExitCode == 2 && result.Summary != null &&
                            (plan.Action == CommandAction.ClearContents || plan.Action == CommandAction.RemoveFolder);
                        if (!result.Canceled && result.ExitCode != 0 && !skippedCacheItems && result.Error == null) result.Error = "Lệnh kết thúc với mã lỗi " + result.ExitCode + ". Xem đầu ra và nhật ký.";
                        report("END exit=" + result.ExitCode + "; canceled=" + result.Canceled);
                        if (loggingError != null) throw loggingError;
                        lock (gate) file.Flush(true);
                    }
                }
            }
            catch (OperationCanceledException) { result.Canceled = true; }
            catch (Exception e) { result.Error = e.Message; }
            lock (gate) result.Output = output.ToString();
            return result;
        }
        private static void ReadLines(StreamReader reader, Action<string> report, string prefix)
        {
            // Chunked reads avoid unbounded allocation from tools emitting a single enormous line.
            var buffer = new char[2048]; int count;
            while ((count = reader.Read(buffer, 0, buffer.Length)) > 0) report(prefix + new string(buffer, 0, count));
        }
    }

    internal sealed class ProcessJob : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits
        {
            public BasicLimits Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref ExtendedLimits limits, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(IntPtr job, uint code);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        private IntPtr handle;
        public ProcessJob(bool killOnClose = true)
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            var limits = new ExtendedLimits(); limits.Basic.LimitFlags = killOnClose ? 0x2000u : 0u; // KILL_ON_JOB_CLOSE; service recovery must finish if the UI exits.
            if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf(limits))) { int error = Marshal.GetLastWin32Error(); Dispose(); throw new Win32Exception(error); }
        }
        public void Attach(Process process) { if (!AssignProcessToJobObject(handle, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        public void Stop() { if (handle != IntPtr.Zero && !TerminateJobObject(handle, 1223)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        public void Dispose() { if (handle != IntPtr.Zero) { CloseHandle(handle); handle = IntPtr.Zero; } }
    }
}
