using System;
using System.IO;

namespace DesktopFolders
{
    internal static class DesktopLaunchShortcut
    {
        private const string Marker = "Open " + Program.DisplayName;
        private const string LegacyMarker = "打开桌面折页";

        public static string Ensure(string directory, string executable)
        {
            Directory.CreateDirectory(directory);
            var shell = CreateShell();
            var current = FindOwned(directory, Program.DisplayName, executable, shell);
            if (current != null)
            {
                UpdateLink(shell, current, executable);
                return current;
            }

            var legacy = FindOwned(directory, Program.LegacyName, executable, shell);
            for (int index = 1; ; index++)
            {
                var suffix = index == 1 ? ".lnk" : " (" + index + ").lnk";
                var path = Path.Combine(directory, Program.DisplayName + suffix);
                if (File.Exists(path) || Directory.Exists(path))
                    continue;
                UpdateLink(shell, legacy ?? path, executable);
                if (legacy != null)
                {
                    File.Move(legacy, path);
                }
                return path;
            }
        }

        public static string FindOwned(string directory, string executable)
        {
            if (!Directory.Exists(directory))
                return null;
            var shell = CreateShell();
            return FindOwned(directory, Program.DisplayName, executable, shell) ??
                FindOwned(directory, Program.LegacyName, executable, shell);
        }

        private static object CreateShell()
        {
            return Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
        }

        private static string FindOwned(string directory, string baseName, string executable, object shell)
        {
            foreach (var path in Directory.EnumerateFiles(directory, baseName + "*.lnk"))
            {
                string name = Path.GetFileNameWithoutExtension(path);
                if (!String.Equals(name, baseName, StringComparison.OrdinalIgnoreCase) &&
                    !(name.StartsWith(baseName + " (", StringComparison.OrdinalIgnoreCase) && name.EndsWith(")", StringComparison.Ordinal)))
                    continue;
                dynamic link = ((dynamic)shell).CreateShortcut(path);
                string description = link.Description;
                string target = link.TargetPath;
                if (description == Marker || description == LegacyMarker ||
                    String.Equals(target, executable, StringComparison.OrdinalIgnoreCase) ||
                    (baseName == Program.LegacyName && IsLegacyExecutable(target)))
                    return path;
            }
            return null;
        }

        private static bool IsLegacyExecutable(string target)
        {
            var name = Path.GetFileName(target);
            return String.Equals(name, Program.LegacyName + ".exe", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(name, "DesktopFolders.exe", StringComparison.OrdinalIgnoreCase) ||
                (!String.IsNullOrEmpty(name) && name.StartsWith(Program.LegacyName + "_1.", StringComparison.Ordinal) &&
                name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        }

        private static void UpdateLink(object shell, string path, string executable)
        {
            dynamic link = ((dynamic)shell).CreateShortcut(path);
            if ((string)link.Description == Marker && String.Equals((string)link.TargetPath, executable, StringComparison.OrdinalIgnoreCase) &&
                String.Equals((string)link.IconLocation, executable + ",0", StringComparison.OrdinalIgnoreCase))
                return;
            link.TargetPath = executable;
            link.WorkingDirectory = Path.GetDirectoryName(executable);
            link.IconLocation = executable + ",0";
            link.Description = Marker;
            link.Save();
        }
    }
}
