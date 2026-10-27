using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace CleanSystem
{
    internal static class TempElevation
    {
        private static bool ValidDays(int days) { return days == 0 || days == 1 || days == 7 || days == 14 || days == 30; }
        public static bool TryReadArguments(string[] args, out int days)
        {
            days = 0;
            if (args.Length == 0 || args[0] != "--windows-temp-admin") return false;
            if (args.Length != 2 || !Int32.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out days) || !ValidDays(days))
                throw new ArgumentException("Tham số mở Windows Temp không hợp lệ.");
            return true;
        }
        public static ProcessStartInfo CreateStartInfo(string executable, int days, IntPtr owner)
        {
            if (!ValidDays(days)) throw new ArgumentOutOfRangeException("days");
            return new ProcessStartInfo(Path.GetFullPath(executable), "--windows-temp-admin " + days.ToString(CultureInfo.InvariantCulture)) {
                UseShellExecute = true, Verb = "runas", WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable)),
                ErrorDialog = false, ErrorDialogParentHandle = owner
            };
        }
    }
}
