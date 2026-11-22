using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DesktopFolders
{
    internal static class WindowsShell
    {
        private const int Previous = 3;
        private const int Next = 2;
        private const uint NoSize = 0x0001;
        private const uint NoMove = 0x0002;
        private const uint NoZOrder = 0x0004;
        private const uint NoActivate = 0x0010;
        private const uint NoOwnerZOrder = 0x0200;

        public static Drawing.Point Cursor { get { return Forms.Cursor.Position; } }
        public static double SystemScale { get { return GetDpiForSystem() / 96.0; } }
        public static IntPtr Handle(Window window) { return new WindowInteropHelper(window).EnsureHandle(); }

        public static void KeepOnDesktop(Window window)
        {
            var handle = Handle(window);
            var source = HwndSource.FromHwnd(handle);
            source.AddHook(PreventActivation);
            var style = GetWindowLong(handle, -20);
            SetWindowLong(handle, -20, style | 0x08000000 | 0x00000080); // NOACTIVATE | TOOLWINDOW
        }

        private static IntPtr PreventActivation(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message != 0x0021)
                return IntPtr.Zero;
            handled = true;
            return new IntPtr(3); // MA_NOACTIVATE, still deliver the click.
        }

        public static IntPtr FindDesktop()
        {
            var shell = GetShellWindow();
            if (shell == IntPtr.Zero)
                return IntPtr.Zero;
            if (FindWindowEx(shell, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                return shell;
            for (var window = GetTopWindow(IntPtr.Zero); window != IntPtr.Zero; window = GetWindow(window, Next))
                if (ClassName(window) == "WorkerW" && IsWindowVisible(window) &&
                    FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                    return window;
            return shell;
        }

        public static bool IsDesktopAt(Drawing.Point point, IntPtr desktop)
        {
            if (desktop == IntPtr.Zero)
                return false;
            var root = TopWindowAt(point);
            return root == desktop || root == GetShellWindow();
        }

        public static IntPtr TopWindowAt(Drawing.Point point)
        {
            return GetAncestor(WindowFromPoint(point), 2);
        }

        public static void StackAboveDesktop(Window window, IntPtr desktop, IntPtr previousTile)
        {
            if (desktop == IntPtr.Zero)
                return;
            var handle = Handle(window);
            var predecessor = GetWindow(previousTile == IntPtr.Zero ? desktop : previousTile, Previous);
            if (IsIconic(handle))
                ShowWindow(handle, 4); // SW_SHOWNOACTIVATE
            if (predecessor == handle)
                return;
            // A desktop brought to the front can have only topmost windows above it.
            while (predecessor != IntPtr.Zero && (GetWindowLong(predecessor, -20) & 0x8) != 0)
                predecessor = GetWindow(predecessor, Previous);
            SetWindowPos(handle, predecessor,
                0, 0, 0, 0, NoSize | NoMove | NoActivate | NoOwnerZOrder);
        }

        public static void Place(Window window, int x, int y, bool center, int anchorWidth = 0, DockEdge edge = DockEdge.None)
        {
            var screen = Forms.Screen.GetWorkingArea(new Drawing.Point(x, y));
            var handle = Handle(window);
            // Move the HWND first so WPF measures in the destination monitor's DPI.
            SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, NoSize | NoZOrder | NoActivate);
            double scale = GetDpiForWindow(handle) / 96.0;
            if (scale == 0)
                scale = 1;
            int width = (int)Math.Ceiling(window.Width * scale);
            int height = (int)Math.Ceiling(window.Height * scale);
            if (center)
            {
                x += (anchorWidth - width) / 2;
                y -= height / 4;
            }
            switch (edge)
            {
                case DockEdge.Left: x = screen.Left; break;
                case DockEdge.Right: x = screen.Right - width; break;
                case DockEdge.Top: y = screen.Top; break;
                case DockEdge.Bottom: y = screen.Bottom - height; break;
            }
            x = Math.Max(screen.Left, Math.Min(screen.Right - width, x));
            y = Math.Max(screen.Top, Math.Min(screen.Bottom - height, y));
            SetWindowPos(handle, IntPtr.Zero, x, y, 0, 0, NoSize | NoZOrder | NoActivate);
        }

        public static Drawing.Rectangle Bounds(Window window)
        {
            Rect bounds;
            GetWindowRect(Handle(window), out bounds);
            return Drawing.Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        }

        public static string ClassName(IntPtr window)
        {
            var name = new StringBuilder(128);
            GetClassName(window, name, name.Capacity);
            return name.ToString();
        }

        public static void Open(string path, bool isExecutable)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
                throw new FileNotFoundException("找不到应用或快捷方式。");
            var start = new ProcessStartInfo(path) { UseShellExecute = true };
            if (isExecutable)
                start.WorkingDirectory = Path.GetDirectoryName(path);
            Process.Start(start);
        }

        public static void CreateLink(string path, string target, bool isDirectory = false)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell", true);
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = target;
            link.WorkingDirectory = isDirectory ? target : Path.GetDirectoryName(target);
            if (isDirectory)
            {
                var info = new StockIconInfo { Size = (uint)Marshal.SizeOf(typeof(StockIconInfo)) };
                if (SHGetStockIconInfo(3, 0, ref info) >= 0) // SIID_FOLDER, SHGSI_ICONLOCATION
                    link.IconLocation = info.Path + "," + info.IconIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            link.Save();
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct StockIconInfo
        {
            public uint Size;
            public IntPtr Icon;
            public int ImageIndex, IconIndex;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Path;
        }

        [DllImport("shell32.dll", ExactSpelling = true)]
        private static extern int SHGetStockIconInfo(uint identifier, uint flags, ref StockIconInfo info);

        public static void MoveDirectory(string source, string target, Window owner)
        {
            var parent = Path.GetDirectoryName(target);
            Directory.CreateDirectory(parent);
            var operation = (IFileOperation)Activator.CreateInstance(
                Type.GetTypeFromCLSID(new Guid("3AD05575-8857-4850-9277-11B85BDB8E09"), true));
            var shellItem = new Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            IntPtr item = IntPtr.Zero, destination = IntPtr.Zero;
            try
            {
                // Let Windows show progress in the app. Report errors here without secondary error prompts.
                const uint flags = 0x0200 | 0x0400 | 0x2000 | 0x00040000 | 0x00100000;
                operation.SetOperationFlags(flags | (owner == null ? 0x0004u : 0));
                if (owner != null)
                    operation.SetOwnerWindow(Handle(owner));
                Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(source, IntPtr.Zero, ref shellItem, out item));
                Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(parent, IntPtr.Zero, ref shellItem, out destination));
                operation.MoveItem(item, destination, Path.GetFileName(target), IntPtr.Zero);
                int result = operation.PerformOperations();
                bool aborted;
                int status = operation.GetAnyOperationsAborted(out aborted);
                if (result == unchecked((int)0x800704C7) || (result >= 0 && status >= 0 && aborted))
                    throw new OperationCanceledException("已取消 Windows 文件操作。");
                Marshal.ThrowExceptionForHR(result);
                Marshal.ThrowExceptionForHR(status);
            }
            finally
            {
                if (item != IntPtr.Zero) Marshal.Release(item);
                if (destination != IntPtr.Zero) Marshal.Release(destination);
                Marshal.ReleaseComObject(operation);
            }
        }

        // Unused members reserve their native vtable slots.
        [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOperation
        {
            void Advise();
            void Unadvise();
            void SetOperationFlags(uint flags);
            void SetProgressMessage();
            void SetProgressDialog();
            void SetProperties();
            void SetOwnerWindow(IntPtr window);
            void ApplyPropertiesToItem();
            void ApplyPropertiesToItems();
            void RenameItem();
            void RenameItems();
            void MoveItem(IntPtr source, IntPtr target, [MarshalAs(UnmanagedType.LPWStr)] string newName, IntPtr sink);
            void MoveItems();
            void CopyItem();
            void CopyItems();
            void DeleteItem();
            void DeleteItems();
            void NewItem();
            [PreserveSig] int PerformOperations();
            [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool aborted);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr binding, ref Guid iid, out IntPtr item);

        public static ImageSource Icon(string path)
        {
            var info = new ShellFileInfo();
            SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info), 0x100);
            if (info.Icon == IntPtr.Zero)
                return null;
            try
            {
                var image = Imaging.CreateBitmapSourceFromHIcon(info.Icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();
                return image;
            }
            finally { DestroyIcon(info.Icon); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ShellFileInfo
        {
            public IntPtr Icon;
            public int IconIndex;
            public uint Attributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetTopWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, int command);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Drawing.Point point);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string name, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int length);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect bounds);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SHGetFileInfo(string file, uint attributes, ref ShellFileInfo info, uint size, uint flags);
    }
}
