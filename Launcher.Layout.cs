using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Drawing = System.Drawing;

namespace DesktopFolders
{
    internal sealed partial class Launcher
    {
        private bool editingLayout;
        private Window layoutWindow;
        private Action stopLayoutRestore;

        private void OpenLayoutEditor()
        {
            if (DialogOpen || exiting || !actualDesktop)
                return;
            WithFeedback(() =>
            {
                if (!store.LayoutEntries.Any())
                    throw new InvalidOperationException("文件夹里还没有可调整布局的桌面快捷方式。");
                using (var snapshot = DesktopIcons.Open())
                {
                    if (snapshot == null)
                        throw new InvalidOperationException("暂时无法读取 Windows 桌面，请稍后再试。");
                    if (snapshot.AutoArrange)
                        throw new InvalidOperationException("请在桌面右键菜单的「查看」中取消「自动排列图标」，再调整布局。");
                }

                ClosePanel();
                editingLayout = DialogOpen = true;
                foreach (var tile in tiles) tile.Window.Hide();
                UpdateTray();
                try
                {
                    stopLayoutRestore = ReturnDesktopShortcuts(report =>
                    {
                        try { ShowLayoutEditor(report); }
                        catch (Exception error)
                        {
                            CompleteLayout(false);
                            Notify(error.Message);
                        }
                    });
                }
                catch
                {
                    CompleteLayout(false);
                    throw;
                }
            });
        }

        private Action ReturnDesktopShortcuts(Action<IEnumerable<string>> completed)
        {
            var returned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var placements = new Dictionary<string, Drawing.Point>(StringComparer.OrdinalIgnoreCase);
            var withoutPosition = new List<string>();
            var errors = new List<string>();
            foreach (var entry in store.LayoutEntries)
            {
                try
                {
                    var path = store.ReturnUntilNextStart(entry) ?? store.VisibleLayoutPath(entry);
                    if (path == null)
                    {
                        errors.Add(entry.Name + "：未找到能匹配的桌面图标。");
                        continue;
                    }
                    if (!returned.Add(path))
                        continue;
                    if (entry.DesktopPosition.HasValue && actualDesktop)
                        placements.Add(path, entry.DesktopPosition.Value);
                    else
                        withoutPosition.Add(Path.GetFileNameWithoutExtension(path));
                }
                catch (Exception error) { errors.Add(entry.Name + "：" + error.Message); }
            }

            return DesktopRestore.Begin(returned, placements, desktopShortcut, (missed, automatic) =>
            {
                var report = new List<string>
                {
                    String.Format("桌面快捷方式 {0} 个，回到原位 {1} 个。", returned.Count, placements.Count - missed.Count)
                };
                if (missed.Count != 0)
                    report.Add((automatic ? "Windows 自动排列了这些图标" : "未能回到原位") + "\n" +
                        String.Join("\n", missed.Select(Path.GetFileNameWithoutExtension)));
                if (withoutPosition.Count != 0)
                    report.Add("没有已保存的桌面坐标\n" + String.Join("\n", withoutPosition));
                if (errors.Count != 0)
                    report.Add("放回桌面时遇到问题\n" + String.Join("\n", errors));
                completed(report);
            });
        }

        private void ShowLayoutEditor(IEnumerable<string> report)
        {
            var window = View.Overlay("调整原始布局", 442, 174);
            layoutWindow = window;
            window.Topmost = true;
            var body = View.DialogSurface(new Thickness(18, 13, 18, 13));
            window.Content = body;
            var layout = new DockPanel();
            body.Child = layout;
            var title = View.DialogHeading(window, window.Title, 0);
            DockPanel.SetDock(title, Dock.Top);
            layout.Children.Add(title);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = View.Button("取消", "取消调整布局", () => CompleteLayout(false));
            var save = View.Button("保存并重新收纳", "保存并重新收纳", () => CompleteLayout(true), primary: true);
            save.Margin = new Thickness(6, 0, 0, 0);
            cancel.Height = save.Height = 34;
            cancel.Width = 68;
            save.MinWidth = 122;
            actions.Children.Add(cancel);
            actions.Children.Add(save);
            DockPanel.SetDock(actions, Dock.Bottom);
            layout.Children.Add(actions);
            var hint = View.Text("把快捷方式拖到想要的位置。保存后，退出时按新位置还原。", 13, View.Secondary);
            hint.TextWrapping = TextWrapping.Wrap;
            hint.Margin = new Thickness(0, 7, 0, 3);
            hint.ToolTip = String.Join("\n\n", report);
            layout.Children.Add(hint);
            window.PreviewKeyDown += (s, e) => { if (e.Key == Key.Escape) CompleteLayout(false); };
            window.Closed += (s, e) =>
            {
                if (layoutWindow != window) return;
                layoutWindow = null;
                window.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => CompleteLayout(false)));
            };
            var point = WindowsShell.Cursor;
            WindowsShell.Place(window, point.X, point.Y, true);
            window.Show();
        }

        private void CompleteLayout(bool save)
        {
            if (!editingLayout || exiting)
                return;
            WithFeedback(() =>
            {
                if (save)
                {
                    var positions = new Dictionary<Entry, Drawing.Point>();
                    using (var snapshot = DesktopIcons.Open())
                    {
                        if (snapshot == null)
                            throw new InvalidOperationException("暂时无法读取桌面，请稍后再保存。");
                        foreach (var entry in store.LayoutEntries)
                        {
                            var path = store.VisibleLayoutPath(entry);
                            var position = path == null ? null : snapshot.Position(path);
                            if (position.HasValue) positions.Add(entry, position.Value);
                        }
                    }
                    if (positions.Count == 0)
                        throw new InvalidOperationException("还没有读取到快捷方式的位置，请稍后再保存。");
                    store.RecordLayout(positions);
                }
                StopLayoutEditor();
                try { RecollectReturned(false); }
                finally
                {
                    DialogOpen = false;
                    foreach (var tile in tiles)
                    {
                        tile.ApplyPlacement();
                        tile.Window.Show();
                    }
                    UpdateTray();
                }
            });
        }

        private void StopLayoutEditor()
        {
            editingLayout = false;
            if (stopLayoutRestore != null) stopLayoutRestore();
            stopLayoutRestore = null;
            var window = layoutWindow;
            layoutWindow = null;
            if (window != null && window.IsVisible) window.Close();
        }

        private void ShowLastRestore()
        {
            if (DialogOpen || exiting)
                return;
            WithFeedback(() =>
            {
                DialogOpen = true;
                try { View.ShowNotice("上次退出还原结果", store.ReadExitReport()); }
                finally { DialogOpen = false; }
            });
        }
    }
}
