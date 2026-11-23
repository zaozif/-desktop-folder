using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

[assembly: AssemblyTitle(DesktopFolders.Program.DisplayName)]
[assembly: AssemblyProduct(DesktopFolders.Program.DisplayName)]

namespace DesktopFolders
{
    internal static class Program
    {
        public const string DisplayName = "Desktop Folders";
        public const string LegacyName = "桌面折页";

        [STAThread]
        public static void Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--desktop-shortcut")
            {
                try
                {
                    DesktopLaunchShortcut.Ensure(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory,
                        Environment.SpecialFolderOption.DoNotVerify), Assembly.GetEntryAssembly().Location);
                }
                catch (Exception) { Environment.ExitCode = 1; }
                return;
            }
            bool first;
            using (var mutex = new Mutex(true, @"Local\DesktopFolders.493586d2", out first))
            {
                if (!first)
                    return;
                string localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string legacyRoot = Path.Combine(localData, LegacyName);
                string root = Path.Combine(localData, DisplayName);
                if (!Directory.Exists(root) && Directory.Exists(legacyRoot))
                    root = legacyRoot;
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolderOption.DoNotVerify);
                Run(root, desktop);
            }
        }

        public static void Run(string root, string desktop)
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            try
            {
                using (var launcher = new Launcher(new Store(root, desktop)))
                    application.Run();
            }
            catch (Exception error)
            {
                MessageBox.Show("无法启动 " + DisplayName + "。\n" + error.Message, DisplayName, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    internal sealed partial class Launcher : IDisposable
    {
        public const string EntryFormat = "DesktopFolders.EntryId";
        private readonly Store store;
        private readonly StartupShortcut startup;
        private readonly bool actualDesktop;
        private readonly string desktopShortcut;
        private readonly TaskbarTuning taskbar;
        private readonly string folderStorage;
        private readonly List<FolderTile> tiles = new List<FolderTile>();
        private readonly Dictionary<string, ImageSource> icons = new Dictionary<string, ImageSource>();
        private readonly Forms.NotifyIcon tray;
        private readonly Drawing.Icon trayIcon;
        private readonly DispatcherTimer desktopTimer;
        private FolderPanel panel;
        private IntPtr desktop;
        private int ticks;
        private bool exiting;
        public bool DialogOpen { get; private set; }
        public Preferences Preferences { get { return store.Preferences; } }
        private ImageSource PanelImage { get; set; }

        public Launcher(Store store, StartupShortcut startup = null, string folderStorage = @"D:\desktop")
        {
            this.store = store;
            taskbar = new TaskbarTuning(store.DataDirectory);
            this.folderStorage = folderStorage;
            this.startup = startup ?? new StartupShortcut(Environment.GetFolderPath(Environment.SpecialFolder.Startup,
                Environment.SpecialFolderOption.DoNotVerify), Assembly.GetEntryAssembly().Location);
            actualDesktop = String.Equals(store.Desktop, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory,
                Environment.SpecialFolderOption.DoNotVerify), StringComparison.OrdinalIgnoreCase);
            if (store.IsNew)
                store.AddFolders(AllocateFolders(new[] { "常用" }));
            store.RecoverFolderImports();
            RecollectReturned();
            CollectLegacyPublicShortcuts();
            if (actualDesktop)
            {
                try { desktopShortcut = DesktopLaunchShortcut.Ensure(store.Desktop, Assembly.GetEntryAssembly().Location); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (COMException) { }
            }
            ReloadPanelImage();
            foreach (var folder in store.Folders)
                tiles.Add(new FolderTile(this, folder));
            if (this.startup.Enabled)
                RefreshStartupShortcut();
            trayIcon = Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetEntryAssembly().Location);
            tray = new Forms.NotifyIcon { Text = Program.DisplayName, Icon = trayIcon, Visible = true };
            UpdateTray();
            tray.DoubleClick += (s, e) =>
            {
                if (editingLayout)
                {
                    if (layoutWindow != null) layoutWindow.Activate();
                    return;
                }
                if (tiles.Count == 0)
                    NewFolder();
                else
                    Toggle(tiles[0]);
            };
            DesktopTick();
            desktopTimer = new DispatcherTimer(DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(260) };
            desktopTimer.Tick += (s, e) => DesktopTick();
            desktopTimer.Start();
            SystemEvents.DisplaySettingsChanged += DisplayChanged;
            ApplyTaskbar(Preferences);
        }

        private void DisplayChanged(object sender, EventArgs e)
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (editingLayout)
                    return;
                ClosePanel();
                foreach (var tile in tiles)
                    tile.ApplyPlacement();
            }));
        }

        private void DesktopTick()
        {
            int tick = ticks++;
            bool refresh = tick % 8 == 0;
            if (refresh || (desktop != IntPtr.Zero && !WindowsShell.IsWindowVisible(desktop)))
                desktop = WindowsShell.FindDesktop();
            if (refresh)
                taskbar.Refresh();
            if (editingLayout || exiting)
                return;
            IntPtr previous = IntPtr.Zero;
            foreach (var tile in tiles)
            {
                if (!tile.Window.IsVisible)
                    continue;
                WindowsShell.StackAboveDesktop(tile.Window, desktop, previous);
                if (tick % 4 == 0)
                    tile.RefreshEdgeContrast();
                previous = WindowsShell.Handle(tile.Window);
            }
        }

        private void UpdateTray()
        {
            var old = tray.ContextMenuStrip;
            var menu = new Forms.ContextMenuStrip();
            if (!editingLayout)
            {
                AddTrayAction(menu, "新建文件夹…", NewFolder);
                if (tiles.Count != 0)
                {
                    var folders = new Forms.ToolStripMenuItem("打开文件夹");
                    foreach (var tile in tiles)
                        folders.DropDownItems.Add(tile.Folder.Name, null, (s, e) => Toggle(tile));
                    menu.Items.Add(folders);
                }
                AddTrayAction(menu, "调整原始布局…", OpenLayoutEditor);
                AddTrayAction(menu, "查看上次退出还原结果…", ShowLastRestore);
                AddTrayAction(menu, "设置…", OpenSettings);
            }
            else
                AddTrayAction(menu, "继续调整布局", () => { if (layoutWindow != null) layoutWindow.Activate(); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            AddTrayAction(menu, "退出 " + Program.DisplayName, Exit);
            tray.ContextMenuStrip = menu;
            if (old != null)
                Application.Current.Dispatcher.BeginInvoke(new Action(old.Dispose));
        }

        private static void AddTrayAction(Forms.ContextMenuStrip menu, string caption, Action action)
        {
            menu.Items.Add(caption, null, (s, e) =>
                Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, action));
        }

        private List<Folder> AllocateFolders(IEnumerable<string> names)
        {
            var occupied = tiles.Select(tile => WindowsShell.Bounds(tile.Window)).ToList();
            var scale = WindowsShell.SystemScale;
            var size = new Drawing.Size((int)Math.Ceiling(FolderTile.Width * scale), (int)Math.Ceiling(FolderTile.Height * scale));
            var folders = new List<Folder>();
            foreach (var name in names)
            {
                var point = DefaultPosition(occupied, size);
                occupied.Add(new Drawing.Rectangle(point, size));
                folders.Add(new Folder { Id = Guid.NewGuid().ToString("N"), Name = name, X = point.X, Y = point.Y });
            }
            return folders;
        }

        private static Drawing.Point DefaultPosition(List<Drawing.Rectangle> occupied, Drawing.Size size)
        {
            var screen = Forms.Screen.PrimaryScreen.WorkingArea;
            int columns = Math.Max(1, (screen.Width - 24) / (size.Width + 14));
            int rows = Math.Max(1, (screen.Height - 44) / (size.Height + 9));
            var point = new Drawing.Point(screen.Right - size.Width - 21, screen.Top + 22);
            for (int index = 0; index < columns * rows; index++)
            {
                var candidate = new Drawing.Point(point.X - (index / rows) * (size.Width + 14),
                    point.Y + (index % rows) * (size.Height + 9));
                var bounds = new Drawing.Rectangle(candidate, size);
                if (occupied.All(other => !other.IntersectsWith(bounds)))
                    return candidate;
            }
            return point;
        }

        public ImageSource Icon(Entry entry)
        {
            ImageSource value;
            if (!icons.TryGetValue(entry.Id, out value))
            {
                value = WindowsShell.Icon(entry.FolderTarget ?? store.LaunchPath(entry));
                icons.Add(entry.Id, value);
            }
            return value;
        }

        public void Toggle(FolderTile tile)
        {
            if (panel != null && panel.Tile == tile)
            {
                ClosePanel();
                return;
            }
            ClosePanel();
            panel = new FolderPanel(this, tile);
        }

        public void ClosePanel() { ClosePanel(panel); }

        public void ClosePanel(FolderPanel candidate)
        {
            if (candidate == null || panel != candidate)
                return;
            panel = null;
            candidate.Window.Close();
        }

        public void PersistPosition(FolderTile tile, Drawing.Point point, Drawing.Point cursor)
        {
            var screen = Forms.Screen.FromPoint(cursor);
            var edge = DesktopDock.At(cursor, screen);
            if (edge != DockEdge.None)
            {
                var span = WindowsShell.Bounds(tile.Window);
                bool vertical = DesktopDock.Vertical(edge);
                int length = vertical ? span.Height : (int)Math.Ceiling(DesktopDock.HorizontalLength *
                    VisualTreeHelper.GetDpi(tile.Window).DpiScaleX);
                var area = screen.WorkingArea;
                var nearby = tiles.Where(other => other != tile && other.Folder.Edge == edge &&
                    Forms.Screen.FromHandle(WindowsShell.Handle(other.Window)).DeviceName == screen.DeviceName)
                    .Select(other => WindowsShell.Bounds(other.Window)).ToArray();
                int start = DesktopDock.Slot(vertical ? cursor.Y - length / 2 : cursor.X - length / 2,
                    vertical ? area.Top : area.Left, vertical ? area.Bottom : area.Right, length, nearby, vertical);
                point = vertical ? new Drawing.Point(cursor.X, start) : new Drawing.Point(start, cursor.Y);
            }
            WithFeedback(() => store.Position(tile.Folder, point.X, point.Y, edge));
            tile.ApplyPlacement();
        }

        public void NewFolder()
        {
            ClosePanel();
            var names = AskNames("新建文件夹", "", true);
            if (names == null)
                return;
            WithFeedback(() =>
            {
                var folders = AllocateFolders(names);
                store.AddFolders(folders);
                foreach (var folder in folders)
                    tiles.Add(new FolderTile(this, folder));
                DesktopTick();
                UpdateTray();
                if (folders.Count == 1)
                    Toggle(tiles[tiles.Count - 1]);
            });
        }

        public void Rename(FolderTile tile)
        {
            ClosePanel();
            var names = AskNames("重命名文件夹", tile.Folder.Name, false);
            var name = names == null ? null : names[0];
            if (name == null || name == tile.Folder.Name)
                return;
            WithFeedback(() =>
            {
                store.Rename(tile.Folder, name);
                tile.Refresh();
                UpdateTray();
            });
        }

        public void Remove(FolderTile tile)
        {
            WithFeedback(() =>
            {
                store.RemoveFolder(tile.Folder);
                if (panel != null && panel.Tile == tile)
                    ClosePanel();
                tiles.Remove(tile);
                tile.Window.Close();
                UpdateTray();
            });
        }

        private string[] AskNames(string title, string existing, bool batch)
        {
            var window = View.Overlay(title, 364, batch ? 288 : 176);
            var surface = View.Rounded(View.Paper, 13);
            surface.BorderBrush = View.Edge;
            surface.BorderThickness = new Thickness(1);
            surface.Margin = new Thickness(5);
            surface.Padding = new Thickness(19, 13, 19, 13);
            var stack = new DockPanel();
            var heading = View.Text(title, 18, View.Ink);
            heading.FontWeight = FontWeights.SemiBold;
            heading.Margin = new Thickness(0, 0, 0, 12);
            DockPanel.SetDock(heading, Dock.Top);
            stack.Children.Add(heading);
            if (batch)
            {
                var hint = View.Text("每行一个名称，最多 36 个字。", 13, View.Secondary);
                hint.Margin = new Thickness(0, 0, 0, 8);
                DockPanel.SetDock(hint, Dock.Top);
                stack.Children.Add(hint);
            }
            var input = new TextBox
            {
                Text = existing, MaxLength = batch ? 2048 : 36, Height = batch ? 124 : 34,
                FontSize = 14, FontFamily = View.Font, AcceptsReturn = batch,
                VerticalScrollBarVisibility = batch ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
                Padding = new Thickness(7, 4, 7, 3), BorderBrush = View.Accent
            };
            System.Windows.Automation.AutomationProperties.SetName(input, "文件夹名称");
            DockPanel.SetDock(input, Dock.Top);
            stack.Children.Add(input);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 13, 0, 0) };
            var cancel = View.Button("取消", "取消", () => window.DialogResult = false);
            Func<string[]> names = () => input.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
            Func<bool> valid = () =>
            {
                var lines = names();
                return lines.Length != 0 && lines.All(line => line.Length <= 36);
            };
            var accept = View.Button("确定", "确定", () =>
            {
                if (valid())
                    window.DialogResult = true;
            });
            cancel.IsCancel = true;
            accept.IsDefault = true;
            accept.IsEnabled = valid();
            cancel.Width = accept.Width = 68;
            cancel.Height = accept.Height = 30;
            input.TextChanged += (s, e) => accept.IsEnabled = valid();
            buttons.Children.Add(cancel);
            buttons.Children.Add(accept);
            stack.Children.Add(buttons);
            surface.Child = stack;
            window.Content = surface;
            FocusManager.SetFocusedElement(window, input);
            window.ContentRendered += (s, e) => window.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                window.Activate();
                Keyboard.Focus(input);
                input.SelectAll();
            }));
            var point = WindowsShell.Cursor;
            WindowsShell.Place(window, point.X, point.Y, true);
            DialogOpen = true;
            try { return window.ShowDialog() == true ? names() : null; }
            finally { DialogOpen = false; }
        }

        public void PickShortcuts(Folder folder)
        {
            var current = panel;
            var dialog = new OpenFileDialog
            {
                Title = "添加快捷方式或应用",
                InitialDirectory = store.Desktop,
                Multiselect = true,
                DereferenceLinks = false,
                Filter = "快捷方式或应用|*.lnk;*.url;*.appref-ms;*.exe"
            };
            DialogOpen = true;
            try
            {
                if (current != null && dialog.ShowDialog(current.Window) == true)
                    Import(folder, dialog.FileNames);
            }
            finally { DialogOpen = false; }
        }

        public DragDropEffects DropEffect(IDataObject data)
        {
            if (data.GetDataPresent(EntryFormat))
                return DragDropEffects.Move;
            var files = data.GetData(DataFormats.FileDrop) as string[];
            return files != null && files.Any(Store.CanImport) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        public void Drop(Folder folder, int index, DragEventArgs e)
        {
            e.Effects = DropEffect(e.Data);
            e.Handled = true;
            if (e.Effects == DragDropEffects.None)
                return;
            var id = e.Data.GetData(EntryFormat) as string;
            if (id == null)
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                Application.Current.Dispatcher.BeginInvoke(new Action(() => Import(folder, files)));
                return;
            }

            var source = store.Folders.FirstOrDefault(item => item.Entries.Any(entry => entry.Id == id));
            if (source == null)
            {
                e.Effects = DragDropEffects.None;
                return;
            }
            var shortcut = source.Entries.First(entry => entry.Id == id);
            Application.Current.Dispatcher.BeginInvoke(new Action(() => WithFeedback(() =>
            {
                store.MoveEntry(source, shortcut, folder, index);
                Refresh(source);
                if (source != folder)
                    Refresh(folder);
            })));
        }

        private void Import(Folder folder, IEnumerable<string> paths)
        {
            if (editingLayout || exiting)
                return;
            var messages = new List<string>();
            using (var snapshot = actualDesktop && paths.Any(store.IsDesktopItem) ? DesktopIcons.Open() : null)
            foreach (var path in paths)
            {
                if (String.Equals(path, desktopShortcut, StringComparison.OrdinalIgnoreCase))
                {
                    messages.Add(Program.DisplayName + " 的启动图标留在桌面，方便退出后再次打开。");
                    continue;
                }
                if (!Store.CanImport(path))
                {
                    messages.Add("无法加入「" + Path.GetFileName(path) + "」。请选择文件夹、快捷方式或应用程序。");
                    continue;
                }
                try
                {
                    var position = snapshot == null ? null : snapshot.Position(Path.GetFullPath(path));
                    if (Directory.Exists(path))
                        ImportFolder(folder, path, position);
                    else
                    {
                        var message = store.Import(folder, path, position);
                        if (message != null)
                            messages.Add(message);
                    }
                }
                catch (Exception error) { messages.Add("无法加入「" + Path.GetFileName(path) + "」。" + error.Message); }
            }
            Refresh(folder);
            if (messages.Count != 0)
                Notify(String.Join("\n", messages));
        }

        private void ImportFolder(Folder folder, string path, Drawing.Point? position)
        {
            var plan = store.PlanFolderImport(path, folderStorage);
            var owner = panel == null ? null : panel.Window;
            bool wasOpen = DialogOpen;
            DialogOpen = true;
            try
            {
                if (plan.MovesOriginal && !FolderMoveDialog.Confirm(plan, owner))
                    return;
                var tile = tiles.FirstOrDefault(item => item.Folder == folder);
                var progressOwner = owner ?? (tile == null ? null : tile.Window);
                store.ImportFolder(folder, plan, position, (source, target) => WindowsShell.MoveDirectory(source, target, progressOwner));
            }
            catch (OperationCanceledException)
            {
                if (Directory.Exists(plan.Target))
                    throw new IOException("移动已停止。请检查原位置和目标位置中是否有部分文件。\n" +
                        plan.Source + "\n" + plan.Target);
            }
            catch (IOException error)
            {
                if (plan.MovesOriginal && Directory.Exists(plan.Source) && Directory.Exists(plan.Target))
                    throw new IOException(error.Message + "\n请检查原位置和目标位置。\n" + plan.Source + "\n" + plan.Target, error);
                throw;
            }
            finally { DialogOpen = wasOpen; }
        }

        private void Refresh(Folder folder)
        {
            var tile = tiles.FirstOrDefault(item => item.Folder == folder);
            if (tile == null)
                return;
            tile.Refresh();
            if (panel != null && panel.Tile == tile)
            {
                panel.Refresh();
                var bounds = WindowsShell.Bounds(tile.Window);
                WindowsShell.Place(panel.Window, bounds.X, bounds.Y, true, bounds.Width);
            }
        }

        public bool CursorOnDesktop() { return WindowsShell.IsDesktopAt(WindowsShell.Cursor, desktop); }

        public void Restore(Folder folder, Entry entry)
        {
            WithFeedback(() =>
            {
                var path = store.Restore(folder, entry);
                icons.Remove(entry.Id);
                Refresh(folder);
                if (actualDesktop && path != null && entry.DesktopPosition.HasValue)
                    DesktopRestore.Begin(new[] { path }, new Dictionary<string, Drawing.Point> { { path, entry.DesktopPosition.Value } },
                        desktopShortcut, null);
            });
        }

        private void RecollectReturned(bool learnMissingPositions = true)
        {
            var entries = store.ReturnedEntries.ToList();
            if (entries.Count == 0)
                return;
            var messages = new List<string>();
            using (var snapshot = actualDesktop && learnMissingPositions ? DesktopIcons.Open() : null)
                foreach (var entry in entries)
                {
                    try
                    {
                        var path = Path.Combine(store.Desktop, entry.ReturnedName);
                        store.Recollect(entry, snapshot == null ? null : snapshot.Position(path));
                    }
                    catch (Exception error) { messages.Add(entry.Name + "：" + error.Message); }
                }
            if (messages.Count != 0)
                Application.Current.Dispatcher.BeginInvoke(new Action(() => Notify("部分快捷方式仍在桌面上。\n" + String.Join("\n", messages))));
        }

        private void CollectLegacyPublicShortcuts()
        {
            var pending = store.LegacyPublicOriginals().ToArray();
            if (pending.Length == 0)
                return;
            var messages = new List<string>();
            using (var snapshot = actualDesktop ? DesktopIcons.Open() : null)
                foreach (var shortcut in pending)
                {
                    try
                    {
                        var message = store.CollectLegacyOriginal(shortcut.Key, shortcut.Value,
                            snapshot == null ? null : snapshot.Position(shortcut.Value));
                        if (message != null)
                            messages.Add(message);
                    }
                    catch (Exception error) { messages.Add(shortcut.Key.Name + "：" + error.Message); }
                }
            if (messages.Count != 0)
                Application.Current.Dispatcher.BeginInvoke(new Action(() => Notify(String.Join("\n", messages))));
        }

        public void Exit()
        {
            if (exiting)
                return;
            exiting = true;
            try
            {
                ClosePanel();
                StopLayoutEditor();
                if (Preferences.RestoreAtExit)
                    ReturnDesktopShortcuts(FinishExit);
                else
                {
                    RecollectReturned(false);
                    Application.Current.Shutdown();
                }
            }
            catch (Exception error) { FinishExit(new[] { error.Message }); }
        }

        private void FinishExit(IEnumerable<string> report)
        {
            try { store.SaveExitReport(report); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally { Application.Current.Shutdown(); }
        }

        public void OpenSettings()
        {
            if (DialogOpen || exiting)
                return;
            var dialog = new SettingsDialog(Preferences, PanelImage, startup.Enabled, previewTaskbar: value =>
                ApplyTaskbar(value), advancedTaskbar: TaskbarTuning.SupportsAdvanced, taskbarStatus: () => taskbar.Status,
                lastRestore: store.ReadExitReport);
            DialogOpen = true;
            bool editLayout = false;
            try
            {
                bool accepted = dialog.Show();
                WithFeedback(() =>
                {
                    var update = accepted ? dialog.Value : Preferences.Copy();
                    if (dialog.WindowPosition.HasValue)
                    {
                        update.SettingsWindowX = dialog.WindowPosition.Value.X;
                        update.SettingsWindowY = dialog.WindowPosition.Value.Y;
                    }
                    if (accepted || update.SettingsWindowX != Preferences.SettingsWindowX || update.SettingsWindowY != Preferences.SettingsWindowY)
                        store.SavePreferences(update);
                    if (!accepted)
                        return;
                    ReloadPanelImage();
                    foreach (var tile in tiles)
                        tile.ApplyAppearance();
                    if (panel != null)
                        panel.ApplyAppearance();
                    if (startup.Enabled != dialog.StartWithWindows)
                        startup.SetEnabled(dialog.StartWithWindows);
                    if (!ApplyTaskbar(Preferences))
                        Notify("Windows 暂时无法应用任务栏背景透明度。");
                    editLayout = dialog.EditLayoutAfterSave;
                });
            }
            finally
            {
                ApplyTaskbar(Preferences);
                DialogOpen = false;
            }
            if (editLayout) OpenLayoutEditor();
        }

        private bool ApplyTaskbar(Preferences value)
        {
            return !actualDesktop || taskbar.Set(value);
        }

        public void OpenFolderBackground(Folder folder)
        {
            var dialog = new SettingsDialog(Preferences, GetPanelImage(folder), false, folder);
            DialogOpen = true;
            try
            {
                if (!dialog.Show())
                    return;
                WithFeedback(() =>
                {
                    store.SetBackground(folder, dialog.FolderValue);
                    if (panel != null && panel.Tile.Folder == folder)
                        panel.ApplyAppearance();
                });
            }
            finally { DialogOpen = false; }
        }

        public ImageSource GetPanelImage(Folder folder)
        {
            return folder.Background == null ? PanelImage : LoadPanelImage(folder.Background);
        }

        private void ReloadPanelImage()
        {
            PanelImage = LoadPanelImage(Preferences);
        }

        private static ImageSource LoadPanelImage(PanelAppearance appearance)
        {
            try { return BackgroundPicture.Render(appearance); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (FileFormatException) { return null; }
        }

        private void RefreshStartupShortcut()
        {
            try { startup.SetEnabled(true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (COMException) { }
        }

        public void Launch(Entry entry)
        {
            WithFeedback(() =>
            {
                WindowsShell.Open(entry.FolderTarget ?? store.LaunchPath(entry), entry.OriginalPath != null);
                ClosePanel();
            });
        }

        private void WithFeedback(Action action)
        {
            try { action(); }
            catch (Exception error) { Notify(error.Message); }
        }

        private void Notify(string text)
        {
            bool wasOpen = DialogOpen;
            DialogOpen = true;
            try
            {
                if (panel != null)
                    MessageBox.Show(panel.Window, text, Program.DisplayName, MessageBoxButton.OK, MessageBoxImage.Information);
                else
                    MessageBox.Show(text, Program.DisplayName, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally { DialogOpen = wasOpen; }
        }

        public void Dispose()
        {
            desktopTimer.Stop();
            taskbar.Dispose();
            SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            tray.Visible = false;
            tray.ContextMenuStrip.Dispose();
            tray.Dispose();
            trayIcon.Dispose();
        }
    }
}
