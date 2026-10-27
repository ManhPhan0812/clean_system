using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace CleanSystem
{
    internal enum CleanupRule { Temporary, GradleDerived, GradleLogs, InspectOnly }

    internal sealed class CleanupGroup
    {
        public string Id { get; private set; }
        public string Name { get; private set; }
        public string Description { get; private set; }
        public string Root { get; private set; }
        public bool Selected { get; set; }
        public bool IsDeveloper { get; private set; }
        public CleanupRule Rule { get; private set; }
        public bool CanClean { get { return Rule != CleanupRule.InspectOnly && BlockedReason == null; } }
        public string Guidance { get; private set; }
        public string Commands { get; private set; }
        public string DocumentationUrl { get; private set; }
        // A root resolved from the environment can fall outside C: keep the row visible but never measure or delete it.
        public string BlockedReason { get; internal set; }
        public bool Present { get; internal set; }
        public bool ToolPresent { get; internal set; }
        public string RequiredTool { get { return CommandRunner.ToolFor(Id); } }
        // Visible list filter: a row is relevant when its folder exists or the tool that would create it is installed.
        public bool Applicable { get { return Present || (RequiredTool != null && ToolPresent); } }
        public bool CanRunCommand { get { return IsDeveloper && BlockedReason == null && CommandRunner.ActionFor(Id) != CommandAction.None && ToolPresent; } }
        public string RunButtonText { get { return CommandRunner.ButtonText(Id); } }
        public string RunHint
        {
            get
            {
                if (BlockedReason != null) return BlockedReason;
                string tool = RequiredTool;
                if (tool != null && !ToolPresent) return "Cần cài " + tool + " và có trong PATH để chạy lệnh này.";
                return "Chạy lệnh cho mục này sau khi xác nhận";
            }
        }
        public string AvailabilityText
        {
            get
            {
                if (BlockedReason != null) return BlockedReason;
                string tool = RequiredTool;
                if (Present) return tool != null && !ToolPresent ? "Có dữ liệu · cần " + tool + " trong PATH" : "";
                if (tool == null) return "Chưa có thư mục trên máy này";
                return ToolPresent ? "Chưa có dữ liệu · công cụ đã cài" : "Chưa cài công cụ · chưa có thư mục";
            }
        }
        internal void DetectEnvironment()
        {
            string tool = RequiredTool;
            Present = Directory.Exists(Root);
            ToolPresent = tool == null || ToolPresence.Has(tool);
        }
        internal void Block(string reason)
        {
            BlockedReason = reason; Selected = false;
        }
        public long TotalBytes { get; internal set; }
        public int TotalCount { get; internal set; }
        public int AgeSkippedCount { get; internal set; }
        public long AgeSkippedBytes { get; internal set; }
        public string AgeSkippedText { get { return AgeSkippedCount == 0 ? "" : "Giữ do tuổi file: " + AgeSkippedCount.ToString("N0") + " file (" + Format.Bytes(AgeSkippedBytes) + ")"; } }
        public bool Measured { get; internal set; }
        public bool Partial { get; internal set; }
        public string TotalSizeText { get { return Measured ? (Partial ? "≥ " : "") + Format.Bytes(TotalBytes) : "—"; } }
        public string State { get; set; }
        public List<FileCandidate> Files { get; private set; }
        public long Bytes { get { return Files.Sum(f => f.Length); } }
        public string SizeText { get { return CanClean ? Format.Bytes(Bytes) : "—"; } }
        public int Count { get { return Files.Count; } }
        public CleanupGroup(string id, string name, string description, string root,
            bool developer = false, CleanupRule rule = CleanupRule.Temporary, string guidance = "", string commands = "", string documentationUrl = "")
        {
            Id = id; Name = name; Description = description;
            Root = PathPolicy.Normalize(root);
            IsDeveloper = developer; Rule = rule; Guidance = guidance; Commands = commands; DocumentationUrl = documentationUrl;
            Selected = id == "user-temp" || id == "windows-temp" || id == "fixture";
            if (!CanClean || developer) Selected = false;
            State = "Chưa quét"; Files = new List<FileCandidate>();
        }
    }

    internal sealed class FileCandidate
    {
        public string Path { get; internal set; }
        public string Root { get; internal set; }
        public string GroupName { get; internal set; }
        public long Length { get; internal set; }
        public DateTime LastWriteUtc { get; internal set; }
        public DateTime CreatedUtc { get; internal set; }
        public uint Volume { get; internal set; }
        public ulong FileId { get; internal set; }
        public DateTime? ScanCutoffUtc { get; internal set; }
        public string SizeText { get { return Format.Bytes(Length); } }
        public string ModifiedText { get { return LastWriteUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm"); } }
    }

    internal sealed class ScanResult
    {
        public List<CleanupGroup> Groups = new List<CleanupGroup>();
        public int Inspected;
        public int Skipped;
        public bool Canceled;
        public bool Limited;
        public DateTime CutoffUtc;

        public static ScanResult MergeFolderScan(IEnumerable<CleanupGroup> displayedGroups, ScanResult update, string groupId)
        {
            if (update.Groups.Count != 1 || update.Groups[0].Id != groupId)
                throw new ArgumentException("Kết quả không khớp thư mục cần quét.");
            var groups = displayedGroups.ToList();
            int index = groups.FindIndex(g => g.Id == groupId);
            if (index < 0) throw new ArgumentException("Thư mục không có trong danh sách.");
            CleanupGroup replacement = update.Groups[0];
            replacement.Selected = replacement.CanClean && groups[index].Selected;
            // A canceled folder cannot supply deletion candidates; other completed folders remain usable.
            if (update.Canceled) replacement.Files.Clear();
            groups[index] = replacement;
            return new ScanResult {
                Groups = groups, CutoffUtc = update.CutoffUtc,
                Inspected = update.Inspected, Skipped = update.Skipped, Limited = update.Limited
            };
        }
    }

    internal sealed class CleanResult
    {
        public int Deleted;
        public int Skipped;
        public long Bytes;
        public bool Canceled;
        public string LogPath;
        public string Error;
    }

    internal static class Format
    {
        public static string Bytes(long value)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double size = value; int index = 0;
            while (size >= 1024 && index < units.Length - 1) { size /= 1024; index++; }
            return size.ToString(index == 0 ? "0" : "0.##", CultureInfo.GetCultureInfo("vi-VN")) + " " + units[index];
        }
    }

    internal static class PathPolicy
    {
        public static void EnableLongPaths()
        {
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
        }
        public static string IOPath(string path)
        {
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
            string full = System.IO.Path.GetFullPath(path);
            return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full.Substring(2) : @"\\?\" + full;
        }
        public static string DisplayPath(string path)
        {
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path.Substring(8);
            return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path.Substring(4) : path;
        }
        public static string Normalize(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("Đường dẫn trống.");
            return System.IO.Path.GetFullPath(path).TrimEnd('\\', '/');
        }
        public static bool IsWithin(string path, string root)
        {
            return Normalize(path).StartsWith(Normalize(root) + "\\", StringComparison.OrdinalIgnoreCase);
        }
        public static bool HasLinkedAncestor(string path)
        {
            string current = Normalize(path);
            while (!String.IsNullOrEmpty(current))
            {
                if ((File.GetAttributes(IOPath(current)) & FileAttributes.ReparsePoint) != 0) return true;
                DirectoryInfo parent = Directory.GetParent(current);
                current = parent == null ? null : parent.FullName;
            }
            return false;
        }
        public static bool IsOnC(string path)
        {
            return String.Equals(System.IO.Path.GetPathRoot(Normalize(path)), @"C:\", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static class RootResolution
    {
        // Tools document their own cache variable; a set variable wins over the default path so the row
        // measures (and pins commands to) the cache the tool really uses. Invalid values keep the default.
        internal static string Resolve(string fallback, params string[] variables)
        {
            foreach (string variable in variables)
            {
                string value;
                try { value = Environment.GetEnvironmentVariable(variable); }
                catch (Exception) { continue; }
                if (String.IsNullOrWhiteSpace(value)) continue;
                try
                {
                    value = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
                    if (!System.IO.Path.IsPathRooted(value)) continue;
                    return PathPolicy.Normalize(value);
                }
                catch (Exception) { }
            }
            return fallback;
        }
    }

    internal sealed class Cleaner
    {
        private readonly List<CleanupGroup> definitions;
        private readonly HashSet<string> allowedRoots;
        private readonly Func<bool> javaIsActive;
        private readonly Func<bool> administrator;
        private const int ScanLimit = 100000;

        // Production roots cannot be supplied by the UI or command-line arguments.
        public Cleaner(Func<bool> adminActivityProbe = null) : this(DefaultGroups(), null, adminActivityProbe, true) { }
        internal Cleaner(IEnumerable<CleanupGroup> groups, Func<bool> javaActivityProbe = null, Func<bool> adminActivityProbe = null, bool requireCDrive = false)
        {
            definitions = groups.ToList();
            foreach (var group in definitions)
            {
                if (requireCDrive && !PathPolicy.IsOnC(group.Root)) group.Block("Ngoài ổ C · chỉ hiển thị, không quét");
                group.DetectEnvironment();
            }
            // Cleanup never receives a root from the UI: only reviewed, cleanable C roots can be deleted.
            allowedRoots = new HashSet<string>(definitions
                .Where(g => g.CanClean && (!requireCDrive || PathPolicy.IsOnC(g.Root))).Select(g => g.Root), StringComparer.OrdinalIgnoreCase);
            javaIsActive = javaActivityProbe ?? JavaMayBeActive;
            administrator = adminActivityProbe ?? WindowsUpdateCache.IsAdministrator;
        }
        // Re-detect presence after the user installs a tool or creates a cache folder, without restarting.
        public void RefreshEnvironment()
        {
            ToolPresence.Reset();
            foreach (var group in definitions) group.DetectEnvironment();
        }
        public List<CleanupGroup> NewGroups()
        {
            return definitions.Select(g =>
            {
                var copy = new CleanupGroup(g.Id, g.Name, g.Description, g.Root, g.IsDeveloper, g.Rule, g.Guidance, g.Commands, g.DocumentationUrl);
                copy.BlockedReason = g.BlockedReason; copy.Present = g.Present; copy.ToolPresent = g.ToolPresent;
                return copy;
            }).ToList();
        }
        private static List<CleanupGroup> DefaultGroups()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            var groups = new List<CleanupGroup> {
                new CleanupGroup("user-temp", "File tạm của bạn", "Dữ liệu tạm trong AppData · không gồm Downloads", Path.Combine(local, "Temp")),
                new CleanupGroup("windows-temp", "File tạm Windows", "Cấp quyền quản trị để quét và dọn các mục hạn chế", Path.Combine(windows, "Temp")),
                new CleanupGroup("user-wer-archive", "Báo cáo lỗi đã lưu", "Báo cáo chẩn đoán cũ của tài khoản hiện tại", Path.Combine(local, @"Microsoft\Windows\WER\ReportArchive")),
                new CleanupGroup("user-wer-queue", "Báo cáo lỗi đang chờ", "Có thể mất báo cáo chưa gửi · tùy chọn", Path.Combine(local, @"Microsoft\Windows\WER\ReportQueue")),
                new CleanupGroup("system-wer-archive", "Báo cáo lỗi hệ thống", "Báo cáo chẩn đoán cũ trong ProgramData", Path.Combine(common, @"Microsoft\Windows\WER\ReportArchive")),
                new CleanupGroup("system-wer-queue", "Báo cáo hệ thống đang chờ", "Có thể mất báo cáo chưa gửi · tùy chọn", Path.Combine(common, @"Microsoft\Windows\WER\ReportQueue"))
            };
            groups.AddRange(DeveloperGroups());
            // System temp/report roots are only in scope on C:. A developer root that resolves to another
            // drive stays visible (blocked) instead of disappearing, so a custom cache is explained, not hidden.
            groups.RemoveAll(g => !g.IsDeveloper && !PathPolicy.IsOnC(g.Root));
            return groups;
        }

        private static IEnumerable<CleanupGroup> DeveloperGroups()
        {
            string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string gradle = RootResolution.Resolve(Path.Combine(user, ".gradle"), "GRADLE_USER_HOME");
            string goModules = RootResolution.Resolve(Path.Combine(user, @"go\pkg\mod"), "GOMODCACHE");
            string goBuild = RootResolution.Resolve(Path.Combine(local, "go-build"), "GOCACHE");
            string pubCache = RootResolution.Resolve(Path.Combine(local, @"Pub\Cache"), "PUB_CACHE");
            string fvm = RootResolution.Resolve(Path.Combine(user, "fvm"), "FVM_CACHE_PATH");
            string codex = RootResolution.Resolve(Path.Combine(user, ".codex"), "CODEX_HOME");
            const string gradleDoc = "https://docs.gradle.org/current/userguide/directory_layout.html";
            return new[] {
                new CleanupGroup(WindowsUpdateCache.GroupId, "Windows Update · Download", "Cache cập nhật đã tải · cần quyền quản trị", WindowsUpdateCache.Root, true, CleanupRule.InspectOnly,
                    WindowsUpdateCache.Guidance, WindowsUpdateCache.DisplayCommand(WindowsUpdateCache.Root), "https://learn.microsoft.com/en-us/troubleshoot/windows-client/installing-updates-features-roles/additional-resources-for-windows-update"),
                new CleanupGroup("gradle-caches", "Gradle · caches", "Dọn cache sinh lại được; giữ dependency và lock", Path.Combine(gradle, "caches"), true, CleanupRule.GradleDerived,
                    "Đóng Android Studio/IDE, dừng build và Gradle daemon trước khi dọn. Ứng dụng chỉ dọn file cũ trong build-cache-N, jars-N, transforms-N; giữ modules-2, metadata, lock và cấu hình. Lệnh PowerShell bên dưới là lựa chọn xóa toàn bộ thư mục caches, gồm cả dependency và metadata, không áp dụng ngưỡng tuổi file. Sau khi chạy, Gradle phải tạo lại cache và có thể tải lại dependency; build offline có thể không hoạt động. gradle.properties, wrapper và jdks nằm ngoài đích xóa. Trong dự án có Gradle Wrapper, dùng .\\gradlew.bat --stop; có thể cần dừng daemon của các phiên bản khác. Kiểm tra đường dẫn trước khi chạy; GRADLE_USER_HOME tùy chỉnh có thể nằm nơi khác.",
                    "# PowerShell: dừng daemon trong dự án Gradle trước khi xóa\n# .\\gradlew.bat --stop\n" + FolderResetCommand(Path.Combine(gradle, "caches")), gradleDoc),
                new CleanupGroup("gradle-daemon", "Gradle · daemon logs", "Chỉ dọn daemon-*.out.log; giữ registry.bin", Path.Combine(gradle, "daemon"), true, CleanupRule.GradleLogs,
                    "Dừng Gradle daemon của các phiên bản đang dùng và đóng IDE trước khi dọn; có thể dùng .\\gradlew.bat --stop trong từng dự án. Nút dọn trong ứng dụng chỉ xóa log cũ, giữ registry.bin và lock. Lệnh PowerShell bên dưới xóa toàn bộ thư mục daemon, gồm log, registry và lock, không áp dụng ngưỡng tuổi file. Gradle tạo lại dữ liệu daemon khi chạy tiếp. Không tự dừng tiến trình; kiểm tra đường dẫn và chắc rằng build/daemon đã dừng trước khi chạy lệnh.",
                    "# PowerShell: dừng daemon trong dự án Gradle trước khi xóa\n# .\\gradlew.bat --stop\n" + FolderResetCommand(Path.Combine(gradle, "daemon")), gradleDoc),
                new CleanupGroup("go-modules", "Go · module cache", "Dùng go clean -modcache · không gồm mã nguồn go/src", goModules, true, CleanupRule.InspectOnly,
                    "Đây là module cache ở vị trí mặc định, không phải toàn bộ thư mục go. File module thường chỉ đọc. Đóng build Go trước khi chạy. Nút Chạy lệnh dùng go clean -modcache và ghim GOMODCACHE vào đường dẫn hiển thị; Go sẽ cần tải lại dependency. Nếu từng sửa nguồn trong cache, sao lưu thay đổi trước.",
                    "go env GOMODCACHE\ngo clean -modcache", "https://go.dev/ref/mod#module-cache"),
                new CleanupGroup("go-build", "Go · build cache", "Dùng go clean -cache để dọn qua Go", goBuild, true, CleanupRule.InspectOnly,
                    "Kiểm tra vị trí thực tế bằng go env GOCACHE. Nút Chạy lệnh dùng go clean -cache và ghim GOCACHE vào đường dẫn hiển thị. Build sau sẽ mất thêm thời gian. Không xóa thư mục go/bin hoặc go/src.",
                    "go env GOCACHE\ngo clean -cache", "https://go.dev/cmd/go/"),
                new CleanupGroup("maven-repository", "Maven · repository", "Có thể chứa artifact nội bộ chỉ lưu tại máy", Path.Combine(user, @".m2\repository"), true, CleanupRule.InspectOnly,
                    "Không coi toàn bộ repository là dữ liệu có thể tải lại: mvn install có thể lưu artifact nội bộ chưa được publish. Nút Chạy lệnh thực hiện lựa chọn PowerShell xóa mọi nội dung repository, giữ thư mục gốc và .m2/settings.xml ở bên ngoài; cần xác nhận reset. Sao lưu artifact nội bộ trước khi chạy. Lệnh Maven tham khảo bên dưới là phương án khác: chạy trong dự án có pom.xml để purge dependency của dự án, không tự tải lại sau khi purge; không xóa toàn bộ repository.",
                    "mvn dependency:purge-local-repository -DreResolve=false", "https://maven.apache.org/plugins/maven-dependency-plugin/examples/purging-local-repository.html"),
                new CleanupGroup("pub-cache", "Flutter / Dart · Pub cache", "Dọn bằng Pub; công cụ global cũng có thể bị ảnh hưởng", pubCache, true, CleanupRule.InspectOnly,
                    "Đóng Flutter/Dart và IDE trước khi chạy. Nút Chạy lệnh dùng flutter pub cache clean --force sau bước xác nhận, ghim PUB_CACHE vào đường dẫn hiển thị. Sau khi dọn có thể cần flutter pub get và kích hoạt lại công cụ global như FVM. Nếu đã sửa package trong cache, sao lưu trước. Đây không phải Flutter SDK.",
                    "flutter pub cache clean", "https://dart.dev/tools/pub/cmd/pub-cache"),
                new CleanupGroup("fvm-sdks", "FVM · Flutter SDK", "SDK đã cài · chọn phiên bản khi gỡ", fvm, true, CleanupRule.InspectOnly,
                    "SDK không phải rác. Nút Chọn SDK / chạy liệt kê bản có trong fvm/versions để bạn chọn; sau xác nhận chạy fvm list rồi fvm remove cho đúng một phiên bản, ghim FVM_CACHE_PATH vào thư mục hiển thị. Kiểm tra .fvmrc của các dự án và bản global trước khi gỡ. Bản fork và cache tùy chỉnh cần quản lý bằng FVM. Không suy ra SDK chưa dùng từ tuổi file.",
                    "fvm list\nfvm remove <version>", "https://fvm.app/documentation/guides/basic-commands"),
                new CleanupGroup("generic-cache", ".cache · dữ liệu nhiều công cụ", "Reset mọi nội dung · kiểm tra dữ liệu cần giữ", Path.Combine(user, ".cache"), true, CleanupRule.InspectOnly,
                    "Nút Chạy lệnh xóa toàn bộ nội dung .cache, giữ thư mục gốc. Xác định công cụ tạo dữ liệu và sao lưu phần cần giữ trước khi xác nhận reset; không phải mọi dữ liệu trong .cache đều có thể tải lại."),
                new CleanupGroup("codex-data", ".codex · dữ liệu ứng dụng", "Reset dữ liệu · có thể mất cấu hình và lịch sử", codex, true, CleanupRule.InspectOnly,
                    "Nút Chạy lệnh xóa toàn bộ nội dung .codex, giữ thư mục gốc. Có thể mất cấu hình, xác thực, lịch sử, skills, plugins, worktrees và kết quả công việc chưa lưu. Đóng Codex/ChatGPT và sao lưu trước khi xác nhận reset. Khi phát hiện ứng dụng đó còn chạy, runner từ chối lệnh.", "", "https://learn.chatgpt.com/docs/config-file/config-basic"),
                new CleanupGroup("gemini-data", ".gemini · dữ liệu ứng dụng", "Reset dữ liệu · có thể mất cấu hình và phiên làm việc", Path.Combine(user, ".gemini"), true, CleanupRule.InspectOnly,
                    "Nút Chạy lệnh xóa toàn bộ nội dung .gemini, giữ thư mục gốc. Có thể mất cấu hình, xác thực và phiên làm việc. Đóng Gemini, kiểm tra dữ liệu cần giữ và sao lưu trước khi xác nhận reset."),
                new CleanupGroup("gitnexus-data", ".gitnexus · dữ liệu công cụ", "Xóa toàn bộ qua PowerShell · xem Cách dọn", Path.Combine(user, ".gitnexus"), true, CleanupRule.InspectOnly,
                    "Đường dẫn đang thống kê là .gitnexus trong thư mục tài khoản, không phải .gitnexus của một dự án. Dừng GitNexus server/MCP và các tác vụ index trước khi chạy. Nút Chạy lệnh xóa vĩnh viễn toàn bộ thư mục, gồm indexes, registry.json và mọi dữ liệu khác bên trong; cần lập chỉ mục/đăng ký lại khi dùng tiếp. Kiểm tra và sao lưu dữ liệu cần giữ trước khi xác nhận. Lệnh Remove-Item -Recurse -Force .gitnexus dùng đường dẫn tương đối; runner dùng đường dẫn tuyệt đối hiển thị tại đây. Để chỉ dọn index của một dự án, đối chiếu gitnexus clean trong đúng dự án.",
                    FolderResetCommand(Path.Combine(user, ".gitnexus")), "https://github.com/abhigyanpatwari/GitNexus/blob/main/gitnexus/src/cli/clean.ts"),
                new CleanupGroup("vscode-extensions", "VS Code · extensions", "Tiện ích đã cài · gỡ trong Extensions", Path.Combine(user, @".vscode\extensions"), true, CleanupRule.InspectOnly,
                    "Mở VS Code, vào Extensions (Ctrl+Shift+X), chọn tiện ích không dùng rồi Uninstall. Không xóa hàng loạt thư mục extensions."),
                new CleanupGroup("cursor-extensions", "Cursor · extensions", "Tiện ích đã cài · gỡ trong Extensions", Path.Combine(user, @".cursor\extensions"), true, CleanupRule.InspectOnly,
                    "Mở Cursor, vào Extensions (Ctrl+Shift+X), chọn tiện ích không dùng rồi Uninstall. Không xóa toàn bộ .cursor hoặc extensions.")
            };
        }

        private static string FolderResetCommand(string root)
        {
            string quoted = "'" + PathPolicy.Normalize(root).Replace("'", "''") + "'";
            // This is displayed/copied only. Validate the absolute target before the manual recursive delete.
            return "# PowerShell: xóa toàn bộ thư mục dưới đây (không qua Thùng rác)\n" +
                "& {\n" +
                "    $cleanSystemTarget = Get-Item -LiteralPath " + quoted + " -Force -ErrorAction Stop\n" +
                "    if (-not $cleanSystemTarget.PSIsContainer -or $cleanSystemTarget.FullName -ne " + quoted + ") { throw 'Sai thu muc dich.' }\n" +
                "    $cleanSystemAncestor = $cleanSystemTarget\n" +
                "    while ($null -ne $cleanSystemAncestor) {\n" +
                "        if ($cleanSystemAncestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Bo qua duong dan lien ket.' }\n" +
                "        $cleanSystemAncestor = $cleanSystemAncestor.Parent\n" +
                "    }\n" +
                "    Write-Host ('Xoa: ' + $cleanSystemTarget.FullName)\n" +
                "    Remove-Item -LiteralPath $cleanSystemTarget.FullName -Recurse -Force -ErrorAction Stop\n" +
                "}";
        }

        internal static bool MatchesRule(CleanupGroup group, string path)
        {
            if (!group.CanClean || !PathPolicy.IsWithin(path, group.Root)) return false;
            string relative = PathPolicy.Normalize(path).Substring(group.Root.Length + 1);
            if (group.Rule == CleanupRule.GradleLogs)
                return Regex.IsMatch(Path.GetFileName(path), @"^daemon-\d+\.out\.log$", RegexOptions.IgnoreCase);
            if (group.Rule == CleanupRule.GradleDerived)
                return Regex.IsMatch(relative, @"^(build-cache|jars|transforms)-\d+\\", RegexOptions.IgnoreCase) &&
                    !path.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(path).Equals("CACHEDIR.TAG", StringComparison.OrdinalIgnoreCase);
            return true;
        }
        internal static bool JavaMayBeActive()
        {
            foreach (string name in new[] { "java", "javaw", "gradle", "studio64" })
            {
                var processes = Process.GetProcessesByName(name);
                bool active = processes.Length > 0;
                foreach (var process in processes) process.Dispose();
                if (active) return true;
            }
            return false;
        }

        public ScanResult Scan(int days, CancellationToken token, Action<int> progress, bool developer = false, string groupId = null)
        {
            if (days < 0 || days > 365) throw new ArgumentOutOfRangeException("days");
            // Zero disables age retention only. File identity, links, attributes and exclusive
            // deletion handles are still checked at scan/cleanup time.
            var result = new ScanResult { Groups = NewGroups().Where(g => g.IsDeveloper == developer).ToList(), CutoffUtc = days == 0 ? DateTime.MaxValue : DateTime.UtcNow.AddDays(-days) };
            if (groupId != null)
            {
                result.Groups = result.Groups.Where(g => String.Equals(g.Id, groupId, StringComparison.Ordinal)).ToList();
                if (result.Groups.Count != 1) throw new ArgumentException("Thư mục không thuộc phạm vi quét hiện tại.", "groupId");
                if (groupId == WindowsUpdateCache.GroupId && !administrator()) throw new UnauthorizedAccessException(WindowsUpdateCache.AccessWarning);
            }
            foreach (var group in result.Groups)
            {
                if (token.IsCancellationRequested) { result.Canceled = true; break; }
                if (group.BlockedReason != null) { group.State = group.BlockedReason; continue; }
                if (group.Id == WindowsUpdateCache.GroupId && !administrator()) { group.State = "Cần Run as administrator · không được truy cập"; continue; }
                if (!Directory.Exists(group.Root)) { group.State = "Không có / không truy cập được"; continue; }
                group.Measured = true;
                int skippedBefore = result.Skipped;
                int groupInspected = 0;
                bool limited = false;
                bool javaActive = group.IsDeveloper && group.CanClean && javaIsActive();
                try
                {
                    if (PathPolicy.HasLinkedAncestor(group.Root)) { group.State = "Bỏ qua thư mục liên kết"; group.Partial = true; result.Skipped++; continue; }
                    var pending = new Stack<string>(); pending.Push(group.Root);
                    while (pending.Count > 0 && !token.IsCancellationRequested && !limited)
                    {
                        string directory = pending.Pop();
                        try
                        {
                            if (PathPolicy.HasLinkedAncestor(directory)) { result.Skipped++; group.Partial = true; continue; }
                            // Lazy enumeration and a hard limit keep memory bounded in huge Temp folders.
                            foreach (string ioEntry in Directory.EnumerateFileSystemEntries(PathPolicy.IOPath(directory)))
                            {
                                string entry = PathPolicy.DisplayPath(ioEntry);
                                if (token.IsCancellationRequested) break;
                                if (groupInspected >= ScanLimit) { limited = true; result.Limited = true; break; }
                                result.Inspected++; groupInspected++;
                                try
                                {
                                    FileAttributes attributes = File.GetAttributes(ioEntry);
                                    if ((attributes & FileAttributes.ReparsePoint) != 0) { result.Skipped++; group.Partial = true; continue; }
                                    if ((attributes & FileAttributes.Directory) != 0) { pending.Push(entry); continue; }
                                    var info = new FileInfo(ioEntry);
                                    group.TotalBytes += info.Length; group.TotalCount++;
                                    if (!group.CanClean || javaActive || !MatchesRule(group, entry)) continue;
                                    if (UnsafeAttributes(attributes)) { result.Skipped++; continue; }
                                    if (info.LastWriteTimeUtc > result.CutoffUtc || info.CreationTimeUtc > result.CutoffUtc) { result.Skipped++; group.AgeSkippedCount++; group.AgeSkippedBytes += info.Length; continue; }
                                    FileCandidate file = NativeFiles.Snapshot(entry, group.Root, group.Name, false);
                                    if (file.LastWriteUtc > result.CutoffUtc || file.CreatedUtc > result.CutoffUtc) { result.Skipped++; group.AgeSkippedCount++; group.AgeSkippedBytes += file.Length; continue; }
                                    file.ScanCutoffUtc = result.CutoffUtc;
                                    group.Files.Add(file);
                                }
                                catch (Exception e) { if (!ExpectedFileError(e)) throw; result.Skipped++; group.Partial = true; }
                                finally { if (result.Inspected % 250 == 0 && progress != null) progress(result.Inspected); }
                            }
                        }
                        catch (Exception e) { if (!ExpectedFileError(e)) throw; result.Skipped++; group.Partial = true; }
                    }
                    group.Partial = group.Partial || limited || token.IsCancellationRequested;
                    group.State = limited ? "Quét một phần (giới hạn)" : token.IsCancellationRequested ? "Đã dừng" : javaActive ? "Chưa dọn được · Java/Gradle đang chạy" : !group.CanClean ? "Chỉ thống kê · xem Cách dọn" : result.Skipped > skippedBefore ? "Đã quét · có mục bỏ qua" : "Đã quét";
                }
                catch (Exception e) { if (!ExpectedFileError(e)) throw; group.State = "Không truy cập được"; group.Partial = true; result.Skipped++; }
            }
            result.Canceled = token.IsCancellationRequested;
            return result;
        }

        public CleanResult Clean(IList<FileCandidate> files, DateTime cutoffUtc, string logDirectory, CancellationToken token, Action<int> progress)
        {
            var result = new CleanResult();
            try
            {
                Directory.CreateDirectory(logDirectory);
                result.LogPath = Path.Combine(logDirectory, "cleanup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tsv");
                // Fail closed: no file is deleted unless the audit log can first be written.
                using (var stream = new FileStream(result.LogPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(true)))
                {
                    writer.AutoFlush = true;
                    writer.WriteLine("time_utc\taction\tbytes\tpath\tdetail");
                    WriteLog(writer, "BEGIN", 0, "", "cutoff_utc=" + cutoffUtc.ToString("o") + "; planned=" + files.Count);
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    int processed = 0;
                    foreach (var file in files)
                    {
                        if (token.IsCancellationRequested) { result.Canceled = true; break; }
                        string error = null;
                        CleanupGroup policy = definitions.FirstOrDefault(g => String.Equals(g.Root, file.Root, StringComparison.OrdinalIgnoreCase));
                        if (!allowedRoots.Contains(file.Root) || !PathPolicy.IsWithin(file.Path, file.Root)) error = "Ngoài phạm vi cho phép.";
                        else if (policy == null || !MatchesRule(policy, file.Path)) error = "Mục này chỉ được thống kê hoặc không thuộc loại file được dọn.";
                        else if (policy.IsDeveloper && javaIsActive()) error = "Java/Gradle đang chạy. Đóng IDE và dừng daemon, sau đó quét lại.";
                        else if (!seen.Add(PathPolicy.Normalize(file.Path))) error = "File trùng lặp.";
                        else
                        {
                            WriteLog(writer, "ATTEMPT", file.Length, file.Path, "");
                            stream.Flush(true);
                            try { NativeFiles.DeleteUnchanged(file, file.ScanCutoffUtc.HasValue && file.ScanCutoffUtc.Value < cutoffUtc ? file.ScanCutoffUtc.Value : cutoffUtc); }
                            catch (Exception e) { if (!ExpectedFileError(e)) throw; error = e.Message; }
                        }
                        if (error == null)
                        {
                            result.Deleted++; result.Bytes += file.Length;
                            WriteLog(writer, "DELETED", file.Length, file.Path, "");
                        }
                        else { result.Skipped++; WriteLog(writer, "SKIPPED", file.Length, file.Path, error); }
                        processed++;
                        if (progress != null && (processed % 25 == 0 || processed == files.Count)) progress(processed);
                    }
                    WriteLog(writer, "END", result.Bytes, "", "deleted=" + result.Deleted + "; skipped=" + result.Skipped + "; canceled=" + result.Canceled);
                    stream.Flush(true);
                }
            }
            catch (Exception e) { result.Error = "Đã dừng dọn dẹp: " + e.Message; }
            return result;
        }

        internal static bool UnsafeAttributes(FileAttributes attributes)
        {
            return (attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory | FileAttributes.System | FileAttributes.ReadOnly | FileAttributes.Offline)) != 0;
        }
        internal static bool ExpectedFileError(Exception e)
        {
            return e is IOException || e is UnauthorizedAccessException || e is System.Security.SecurityException || e is Win32Exception || e is ArgumentException || e is NotSupportedException;
        }
        private static void WriteLog(StreamWriter writer, string action, long bytes, string path, string detail)
        {
            writer.WriteLine(DateTime.UtcNow.ToString("o") + "\t" + action + "\t" + bytes + "\t" + Escape(path) + "\t" + Escape(detail));
        }
        private static string Escape(string value) { return value.Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n"); }
    }

    internal static class NativeFiles
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct FileTime { public uint Low; public uint High; }
        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            public uint Attributes;
            public FileTime Creation, Access, Write;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint sharing, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, byte[] information, uint size);

        private static SafeFileHandle Open(string path, bool deleting)
        {
            // OPEN_REPARSE_POINT prevents following the final component; zero sharing blocks active readers/writers on deletion.
            var handle = CreateFile(PathPolicy.IOPath(path), deleting ? 0x00010080u : 0x00000080u, deleting ? 0u : 7u, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            return handle;
        }
        public static FileCandidate Snapshot(string path, string root, string group, bool deleting)
        {
            using (var handle = Open(path, deleting)) { return Read(handle, path, root, group); }
        }
        private static FileCandidate Read(SafeFileHandle handle, string path, string root, string group)
        {
            FileInformation info;
            if (!GetFileInformationByHandle(handle, out info)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (Cleaner.UnsafeAttributes((FileAttributes)info.Attributes) || info.Links != 1) throw new IOException("Bỏ qua file được bảo vệ hoặc có liên kết.");
            var buffer = new StringBuilder(32768);
            uint count = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
            if (count == 0 || count >= buffer.Capacity) throw new IOException("Không xác minh được đường dẫn thực tế.");
            string actual = buffer.ToString();
            if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual.Substring(4);
            if (!String.Equals(PathPolicy.Normalize(actual), PathPolicy.Normalize(path), StringComparison.OrdinalIgnoreCase) || !PathPolicy.IsWithin(actual, root))
                throw new IOException("Đường dẫn thực tế không thuộc phạm vi dọn dẹp.");
            return new FileCandidate {
                Path = PathPolicy.Normalize(path), Root = root, GroupName = group,
                Length = (long)(((ulong)info.SizeHigh << 32) | info.SizeLow),
                LastWriteUtc = ToDateTime(info.Write), CreatedUtc = ToDateTime(info.Creation),
                Volume = info.Volume, FileId = ((ulong)info.IndexHigh << 32) | info.IndexLow
            };
        }
        private static DateTime ToDateTime(FileTime time) { return DateTime.FromFileTimeUtc((long)(((ulong)time.High << 32) | time.Low)); }
        public static void DeleteUnchanged(FileCandidate expected, DateTime cutoffUtc)
        {
            if (PathPolicy.HasLinkedAncestor(expected.Path)) throw new IOException("Bỏ qua đường dẫn liên kết.");
            using (var handle = Open(expected.Path, true))
            {
                FileCandidate current = Read(handle, expected.Path, expected.Root, expected.GroupName);
                if (current.Volume != expected.Volume || current.FileId != expected.FileId || current.Length != expected.Length ||
                    current.LastWriteUtc != expected.LastWriteUtc || current.CreatedUtc != expected.CreatedUtc ||
                    current.LastWriteUtc > cutoffUtc || current.CreatedUtc > cutoffUtc)
                    throw new IOException("File đã thay đổi sau lần quét; hãy quét lại.");
                if (!SetFileInformationByHandle(handle, 4, new byte[] { 1 }, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
    }
}
