using System;
using System.IO;

namespace DesktopFolders
{
    internal static class DesktopLaunchShortcut
    {
        private const string Marker = "打开桌面折页";

        public static string Ensure(string desktop, string executable)
        {
            Directory.CreateDirectory(desktop);
            var shellType = Type.GetTypeFromProgID("WScript.Shell", true);
            dynamic shell = Activator.CreateInstance(shellType);
            for (int index = 1; ; index++)
            {
                string name = index == 1 ? "桌面折页.lnk" : "桌面折页 (" + index + ").lnk";
                var path = Path.Combine(desktop, name);
                if (Directory.Exists(path))
                    continue;
                dynamic link = shell.CreateShortcut(path);
                if (File.Exists(path))
                {
                    var description = (string)link.Description;
                    var target = (string)link.TargetPath;
                    if (description == Marker && String.Equals(target, executable, StringComparison.OrdinalIgnoreCase) &&
                        String.Equals((string)link.IconLocation, executable + ",0", StringComparison.OrdinalIgnoreCase))
                        return path;
                    if (description != Marker && !IsSameApp(target, executable))
                        continue;
                }
                link.TargetPath = executable;
                link.WorkingDirectory = Path.GetDirectoryName(executable);
                link.IconLocation = executable + ",0";
                link.Description = Marker;
                link.Save();
                return path;
            }
        }

        private static bool IsSameApp(string target, string executable)
        {
            if (String.Equals(target, executable, StringComparison.OrdinalIgnoreCase))
                return true;
            if (String.IsNullOrEmpty(target) || !String.Equals(Path.GetDirectoryName(target),
                Path.GetDirectoryName(executable), StringComparison.OrdinalIgnoreCase))
                return false;
            var name = Path.GetFileName(target);
            return name == "桌面折页.exe" || (name.StartsWith("桌面折页_1.", StringComparison.Ordinal) &&
                name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        }
    }
}
