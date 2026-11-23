using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopFolders
{
    internal sealed class TaskbarTuning : IDisposable
    {
        private const string SettingsMap = @"Local\DesktopFolders.TaskbarAppearance.v3";
        private const string WatcherMarker = @"Local\DesktopFolders.TaskbarWatcher.v3.";
        private const string BridgeResource = "DesktopFolders.TaskbarTuning.x64.dll";
        private const int NotFound = unchecked((int)0x80070490);
        private static readonly Guid BridgeClass = new Guid("4f807566-6d8a-478c-b712-2fdeae02b668");
        private readonly string dataDirectory;
        private readonly TaskbarOpacity legacy = new TaskbarOpacity();
        private MemoryMappedFile mapping;
        private MemoryMappedViewAccessor settings;
        private Task<int> connection;
        private uint explorer;
        private int desired;
        private DateTime startedAt;
        private bool settingsUnavailable;

        public TaskbarTuning(string dataDirectory)
        {
            this.dataDirectory = dataDirectory;
        }

        public static bool SupportsAdvanced
        {
            get
            {
                return Environment.OSVersion.Version.Build >= 22000 &&
                    String.Equals(Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE"), "AMD64", StringComparison.OrdinalIgnoreCase);
            }
        }

        public string Status
        {
            get
            {
                if (settingsUnavailable)
                    return "无法发送任务栏外观设置。";
                if (desired == 0 || settings == null || settings.ReadInt32(4) == desired)
                    return null;
                if (connection != null && (connection.IsFaulted || (connection.IsCompleted && connection.Result < 0)))
                    return "无法连接 Windows 任务栏。";
                if (DateTime.UtcNow - startedAt >= TimeSpan.FromSeconds(5))
                    return "当前任务栏未能应用这些外观设置。";
                return explorer == 0 ? "正在等待 Windows 任务栏…" : null;
            }
        }

        public bool Set(Preferences preferences)
        {
            if (!SupportsAdvanced)
                return legacy.Set(preferences.CustomizeTaskbar, preferences.TaskbarOpacity);
            settingsUnavailable = false;
            int value = Pack(preferences);
            if (value == desired)
                return true;
            if (value != 0 && settings == null)
            {
                try
                {
                    mapping = MemoryMappedFile.CreateNew(SettingsMap, 8);
                    settings = mapping.CreateViewAccessor();
                }
                catch (IOException) { settingsUnavailable = true; }
                catch (UnauthorizedAccessException) { settingsUnavailable = true; }
                if (settingsUnavailable)
                {
                    if (mapping != null) mapping.Dispose();
                    mapping = null;
                    return false;
                }
            }
            desired = value;
            if (settings != null)
                settings.Write(0, value);
            if (value != 0)
            {
                startedAt = DateTime.UtcNow;
                if (connection != null && (connection.IsFaulted || (connection.IsCompleted && connection.Result < 0)))
                    explorer = 0;
                Refresh();
            }
            return true;
        }

        public void Refresh()
        {
            if (!SupportsAdvanced)
            {
                legacy.Refresh();
                return;
            }
            if (desired == 0)
                return;
            var bar = FindWindow("Shell_TrayWnd", null);
            uint process;
            if (bar == IntPtr.Zero || GetWindowThreadProcessId(bar, out process) == 0 || process == explorer)
                return;
            explorer = process;
            startedAt = DateTime.UtcNow;
            settings.Write(4, Int32.MinValue);
            connection = Task.Run(() => Attach(process));
        }

        private static int Percent(int value) { return Math.Max(0, Math.Min(value, 100)); }

        private static int Pack(Preferences preferences)
        {
            int brightness = Percent(preferences.TaskbarBrightness);
            int opacity = Percent(preferences.TaskbarOpacity);
            int icons = Percent(preferences.TaskbarIconsOpacity);
            if (!preferences.CustomizeTaskbar || (brightness == 100 && opacity == 100 && icons == 100 && preferences.TaskbarTopLine))
                return 0;
            return (1 << 22) | brightness | (opacity << 7) | (icons << 14) | (preferences.TaskbarTopLine ? 1 << 21 : 0);
        }

        private int Attach(uint process)
        {
            EventWaitHandle existing;
            if (EventWaitHandle.TryOpenExisting(WatcherMarker + process, out existing))
            {
                existing.Dispose();
                return 0;
            }

            string bridge = ExtractBridge();
            int result = NotFound;
            for (int endpoint = 1; endpoint <= 128 && result == NotFound; endpoint++)
                result = InitializeXamlDiagnosticsEx("VisualDiagConnection" + endpoint, process, "", bridge, BridgeClass, null);
            return result;
        }

        private string ExtractBridge()
        {
            byte[] image;
            using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(BridgeResource))
            using (var buffer = new MemoryStream())
            {
                if (resource == null)
                    throw new FileNotFoundException("任务栏组件未包含在程序中。");
                resource.CopyTo(buffer);
                image = buffer.ToArray();
            }
            string hash;
            using (var sha = SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(image)).Replace("-", "").ToLowerInvariant();
            var folder = Path.Combine(dataDirectory, "TaskbarNative");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "Taskbar-" + hash + ".dll");
            if (!File.Exists(path) || new FileInfo(path).Length != image.Length)
                File.WriteAllBytes(path, image);
            return path;
        }

        public void Dispose()
        {
            if (settings != null)
            {
                settings.Write(0, 0);
                settings.Dispose();
                mapping.Dispose();
            }
            legacy.Dispose();
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string windowClass, string title);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("Windows.UI.Xaml.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int InitializeXamlDiagnosticsEx(string endpoint, uint process, string diagnostics, string tapDll, Guid clsid, string data);
    }
}
