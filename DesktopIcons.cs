using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;

namespace DesktopFolders
{
    internal sealed class DesktopIcons : IDisposable
    {
        private readonly IFolderView view;
        private readonly Dictionary<string, DesktopItem> items =
            new Dictionary<string, DesktopItem>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<Point> virtualItems = new HashSet<Point>();
        public readonly bool AutoArrange;

        private DesktopIcons(IFolderView view)
        {
            this.view = view;
            AutoArrange = view.GetAutoArrange() != 1; // S_FALSE means manual positioning is allowed.
            var folderId = new Guid("000214E6-0000-0000-C000-000000000046");
            IntPtr folder;
            if (view.GetFolder(ref folderId, out folder) < 0 || folder == IntPtr.Zero)
                return;
            try
            {
                int count;
                if (view.ItemCount(2, out count) < 0) // SVGIO_ALLVIEW
                    return;
                for (int index = 0; index < count; index++)
                    ReadItem(folder, index);
            }
            finally { Marshal.Release(folder); }
        }

        public static DesktopIcons Open()
        {
            if (WindowsShell.FindDesktop() == IntPtr.Zero)
                return null;
            try
            {
                dynamic windows = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")));
                object location = 0; // CSIDL_DESKTOP
                object root = null;
                int handle;
                object dispatch = windows.FindWindowSW(ref location, ref root, 8, out handle, 1);
                if (dispatch == null)
                    return null;
                var service = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837"); // SID_STopLevelBrowser
                var browserId = typeof(IShellBrowser).GUID;
                IShellBrowser browser;
                if (((IServiceProvider)dispatch).QueryService(ref service, ref browserId, out browser) < 0)
                    return null;
                object shellView;
                if (browser.QueryActiveShellView(out shellView) < 0)
                    return null;
                return new DesktopIcons((IFolderView)shellView);
            }
            catch (COMException) { return null; }
            catch (RuntimeBinderException) { return null; }
            catch (InvalidCastException) { return null; }
        }

        private void ReadItem(IntPtr folder, int index)
        {
            IntPtr child;
            if (view.Item(index, out child) < 0 || child == IntPtr.Zero)
                return;
            try
            {
                Point position;
                if (view.GetItemPosition(child, out position) < 0)
                    return;
                var itemId = typeof(IShellItem).GUID;
                IShellItem shellItem;
                if (SHCreateItemWithParent(IntPtr.Zero, folder, child, ref itemId, out shellItem) >= 0)
                {
                    try
                    {
                        string path;
                        if (shellItem.GetDisplayName(0x80058000, out path) >= 0 &&
                            !String.IsNullOrEmpty(path) && !items.ContainsKey(path))
                        {
                            items.Add(path, new DesktopItem { Id = child, Position = position });
                            child = IntPtr.Zero;
                            return;
                        }
                    }
                    finally { Marshal.ReleaseComObject(shellItem); }
                }
                virtualItems.Add(position);
            }
            finally { if (child != IntPtr.Zero) Marshal.FreeCoTaskMem(child); }
        }

        public Point? Position(string path)
        {
            DesktopItem item;
            return items.TryGetValue(path, out item) ? item.Position : (Point?)null;
        }

        public int VisibleCount(IEnumerable<string> paths)
        {
            return paths.Count(path => items.ContainsKey(path));
        }

        // Only the organizer's own launcher can be moved out of an original slot.
        public void PlaceAvailable(IDictionary<string, Point> requested, string launcherShortcut)
        {
            if (AutoArrange)
                return;
            var desired = new HashSet<Point>(requested.Values);
            var reserved = new HashSet<Point>(requested.Where(pair => Position(pair.Key) == pair.Value).Select(pair => pair.Value));
            var moves = new Dictionary<DesktopItem, Point>();
            foreach (var request in requested)
            {
                DesktopItem item;
                if (items.TryGetValue(request.Key, out item) &&
                    item.Position != request.Value && reserved.Add(request.Value))
                    moves.Add(item, request.Value);
            }
            var occupied = new HashSet<Point>(virtualItems);
            foreach (var item in items.Values)
                if (!moves.ContainsKey(item))
                    occupied.Add(item.Position);

            DesktopItem launcher;
            if (launcherShortcut != null && items.TryGetValue(launcherShortcut, out launcher) &&
                !moves.ContainsKey(launcher) && desired.Contains(launcher.Position) && !virtualItems.Contains(launcher.Position) &&
                !items.Values.Any(item => item != launcher && item.Position == launcher.Position && !moves.ContainsKey(item)))
            {
                var spare = moves.Where(move => move.Value == launcher.Position)
                    .Select(move => (Point?)move.Key.Position).FirstOrDefault(point =>
                        !desired.Contains(point.Value) && !occupied.Contains(point.Value));
                if (spare.HasValue)
                {
                    moves.Add(launcher, spare.Value);
                    occupied.Remove(launcher.Position);
                }
            }
            // Treat the moving icons as a batch so that two returned shortcuts can swap slots.
            while (true)
            {
                var blocked = moves.Where(move => occupied.Contains(move.Value)).ToArray();
                if (blocked.Length == 0)
                    break;
                foreach (var move in blocked)
                {
                    moves.Remove(move.Key);
                    occupied.Add(move.Key.Position);
                }
            }
            var ordered = moves.ToArray();
            if (ordered.Length != 0)
                view.SelectAndPositionItems((uint)ordered.Length,
                    ordered.Select(move => move.Key.Id).ToArray(), ordered.Select(move => move.Value).ToArray(), 0x80);
        }

        public static void NotifyCreated(string path)
        {
            SHChangeNotify(2, 0x2005, path, IntPtr.Zero); // SHCNE_CREATE, PATHW | FLUSHNOWAIT
        }

        public void Dispose()
        {
            foreach (var item in items.Values) Marshal.FreeCoTaskMem(item.Id);
            items.Clear();
        }

        private sealed class DesktopItem
        {
            public IntPtr Id;
            public Point Position;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern void SHChangeNotify(int eventId, uint flags, string path, IntPtr other);

        [DllImport("shell32.dll")]
        private static extern int SHCreateItemWithParent(IntPtr parentId, IntPtr parent, IntPtr child,
            ref Guid iid, out IShellItem item);

        [ComImport, Guid("6d5140c1-7436-11ce-8034-00aa006009fa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IServiceProvider
        {
            [PreserveSig] int QueryService(ref Guid service, ref Guid iid, out IShellBrowser browser);
        }

        // Unused members reserve their native vtable slots.
        [ComImport, Guid("000214e2-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellBrowser
        {
            void GetWindow();
            void ContextSensitiveHelp();
            void InsertMenusSB();
            void SetMenuSB();
            void RemoveMenusSB();
            void SetStatusTextSB();
            void EnableModelessSB();
            void TranslateAcceleratorSB();
            void BrowseObject();
            void GetViewStateStream();
            void GetControlWindow();
            void SendControlMsg();
            [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object shellView);
        }

        [ComImport, Guid("cde725b0-ccc9-4519-917e-325d72fab4ce"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFolderView
        {
            void GetCurrentViewMode();
            void SetCurrentViewMode();
            [PreserveSig] int GetFolder(ref Guid iid, out IntPtr folder);
            [PreserveSig] int Item(int index, out IntPtr child);
            [PreserveSig] int ItemCount(uint flags, out int count);
            void Items();
            void GetSelectionMarkedItem();
            void GetFocusedItem();
            [PreserveSig] int GetItemPosition(IntPtr child, out Point position);
            void GetSpacing();
            void GetDefaultSpacing();
            [PreserveSig] int GetAutoArrange();
            void SelectItem();
            [PreserveSig] int SelectAndPositionItems(uint count,
                [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] children,
                [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] Point[] positions, uint flags);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler();
            void GetParent();
            [PreserveSig] int GetDisplayName(uint kind, [MarshalAs(UnmanagedType.LPWStr)] out string path);
        }
    }
}
