using System;
using System.IO;
using System.Security.Principal;

namespace CleanSystem
{
    internal static class WindowsUpdateCache
    {
        public const string GroupId = "windows-update-downloads";
        public const string AccessWarning = "Bạn chưa chạy ứng dụng bằng quyền quản trị.\n\nKhông thể truy cập mục Windows Update · Download.\nĐóng ứng dụng, nhấp phải DonDepOCung.exe rồi chọn Run as administrator.";
        public const string Warning = "Không thực hiện khi Windows đang tải hoặc cài đặt bản cập nhật, hay đang yêu cầu khởi động lại để hoàn tất cập nhật. Nếu có lỗi file đang được sử dụng, không nên cố xóa.";
        public const string Guidance = "Lưu ý: " + Warning + "\n\nMở ứng dụng bằng Run as administrator để chạy mục này. Chỉ xóa nội dung SoftwareDistribution\\Download, giữ thư mục gốc và DataStore. Dịch vụ wuauserv và bits được khởi động lại trong bước cuối, kể cả khi dừng dịch vụ hoặc xóa gặp lỗi. Có thể bấm Dừng khi đang kiểm tra thư mục. Khi lệnh bắt đầu chạy, đợi dịch vụ khởi động lại trước khi đóng ứng dụng. Windows có thể phải tải lại các gói cập nhật.";
        public static string Root { get { return PathPolicy.Normalize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"SoftwareDistribution\Download")); } }
        public static bool IsAdministrator()
        {
            using (var identity = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        public static string DisplayCommand(string root)
        {
            return "try {\n    Stop-Service wuauserv -Force -NoWait -ErrorAction Stop\n" +
                "    (Get-Service wuauserv).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))\n" +
                "    Stop-Service bits -Force -NoWait -ErrorAction Stop\n" +
                "    (Get-Service bits).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))\n" +
                "    Get-ChildItem -LiteralPath " + CommandRunner.Quote(root) + " -Force -ErrorAction Stop |\n" +
                "        Remove-Item -Recurse -Force -ErrorAction Stop\n} finally {\n" +
                "    # Thử khởi động lại cả hai dịch vụ, kể cả khi một dịch vụ bị lỗi\n" +
                "    foreach ($name in @('wuauserv', 'bits')) {\n" +
                "        try { Start-Service $name -ErrorAction Stop } catch { Write-Error $_ -ErrorAction Continue }\n    }\n}";
        }
        internal static string BuildBody(string root, string validation)
        {
            // Read-only WUA preflight; failure or an active installer/reboot requirement prevents service changes.
            return "function Remove-CleanSystemUpdateContents([string]$directory) {\n" +
                "    if ($cleanSystemDeleteTimer.Elapsed.TotalMinutes -ge 5) { throw 'Don cache vuot 5 phut; da dung xoa de khoi dong lai dich vu.' }\n" +
                "    $directory = Get-CleanSystemIOPath $directory\n" +
                "    if ([IO.File]::GetAttributes($directory) -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked directory changed before deletion.' }\n" +
                "    foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($directory)) {\n" +
                "        if ($cleanSystemDeleteTimer.Elapsed.TotalMinutes -ge 5) { throw 'Don cache vuot 5 phut; da dung xoa de khoi dong lai dich vu.' }\n" +
                "        $attributes = [IO.File]::GetAttributes($entry)\n" +
                "        if ($attributes -band [IO.FileAttributes]::ReparsePoint) { throw ('Linked entry before deletion: ' + $entry) }\n" +
                "        if ($attributes -band [IO.FileAttributes]::Directory) {\n" +
                "            Remove-CleanSystemUpdateContents $entry\n            [IO.Directory]::Delete($entry, $false)\n" +
                "        } else {\n" +
                "            if ($attributes -band [IO.FileAttributes]::ReadOnly) { [IO.File]::SetAttributes($entry, ($attributes -band (-bnot [IO.FileAttributes]::ReadOnly))) }\n" +
                "            [IO.File]::Delete($entry)\n        }\n" +
                "        $script:cleanSystemDeleted++\n" +
                "        if ($script:cleanSystemDeleted % 100 -eq 0 -or $cleanSystemDeleteTimer.ElapsedMilliseconds - $script:cleanSystemLastDeleteReport -ge 1000) {\n" +
                "            $script:cleanSystemLastDeleteReport = $cleanSystemDeleteTimer.ElapsedMilliseconds\n" +
                "            [Console]::WriteLine('Da xoa ' + $script:cleanSystemDeleted + ' muc.')\n        }\n    }\n}\n" +
                "[Console]::WriteLine('Dang kiem tra trang thai Windows Update...')\n" +
                "$cleanSystemSystemInfo = New-Object -ComObject Microsoft.Update.SystemInfo -ErrorAction Stop\n" +
                "if ($cleanSystemSystemInfo.RebootRequired) { throw 'Windows Update dang yeu cau khoi dong lai. Khong don cache.' }\n" +
                "$cleanSystemSession = New-Object -ComObject Microsoft.Update.Session -ErrorAction Stop\n" +
                "if ($cleanSystemSession.CreateUpdateInstaller().IsBusy) { throw 'Windows dang cai dat/go ban cap nhat. Khong don cache.' }\n" +
                "foreach ($cleanSystemName in @('wuauserv', 'bits')) {\n" +
                "    $cleanSystemService = Get-Service -Name $cleanSystemName -ErrorAction Stop\n" +
                "    if ($cleanSystemService.StartType -eq 'Disabled') { throw ('Dich vu bi tat: ' + $cleanSystemName + '. Khong thay doi cau hinh dich vu.') }\n" +
                "    if ($cleanSystemService.Status -notin @('Running', 'Stopped')) { throw ('Dich vu dang chuyen trang thai: ' + $cleanSystemName) }\n}\n" +
                "$cleanSystemFailure = $null\n$cleanSystemRestartErrors = New-Object 'System.Collections.Generic.List[string]'\n" +
                "try {\n" +
                "    foreach ($cleanSystemName in @('wuauserv', 'bits')) {\n" +
                "        Write-Output ('STOP ' + $cleanSystemName)\n" +
                "        Stop-Service -Name $cleanSystemName -Force -NoWait -ErrorAction Stop\n" +
                "        (Get-Service -Name $cleanSystemName -ErrorAction Stop).WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))\n    }\n" +
                // Revalidate after stopping services, before any recursive deletion.
                validation +
                "    $cleanSystemDeleteTimer = [Diagnostics.Stopwatch]::StartNew()\n$script:cleanSystemDeleted = 0\n$script:cleanSystemLastDeleteReport = 0\n" +
                "    Remove-CleanSystemUpdateContents " + CommandRunner.Quote(root) + "\n" +
                "    [Console]::WriteLine('Da don ' + $script:cleanSystemDeleted + ' muc trong Download.')\n" +
                "} catch { $cleanSystemFailure = $_.Exception.Message } finally {\n" +
                "    foreach ($cleanSystemName in @('wuauserv', 'bits')) {\n" +
                "        try {\n" +
                "            [Console]::WriteLine('START SERVICE ' + $cleanSystemName)\n" +
                "            Start-Service -Name $cleanSystemName -ErrorAction Stop\n" +
                "            (Get-Service -Name $cleanSystemName -ErrorAction Stop).WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, [TimeSpan]::FromSeconds(30))\n" +
                "            Write-Output ('RESTARTED ' + $cleanSystemName)\n" +
                "        } catch { $cleanSystemRestartErrors.Add($cleanSystemName + ': ' + $_.Exception.Message) }\n    }\n}\n" +
                "if ($cleanSystemRestartErrors.Count -gt 0) { $cleanSystemFailure = $cleanSystemFailure + ' Khong khoi dong lai duoc dich vu: ' + ($cleanSystemRestartErrors -join '; ') }\n" +
                "if ($cleanSystemFailure) { throw ($cleanSystemFailure + ' Da dung thao tac; khong thu ep xoa. Kiem tra Windows Update va nhat ky.') }\n" +
                "Write-Output 'Da don noi dung Download va khoi dong lai wuauserv/bits.'\nexit 0\n";
        }
    }
}
