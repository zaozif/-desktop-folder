using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DesktopFolders
{
    internal static class FolderMoveDialog
    {
        public static bool Confirm(FolderImport plan, Window owner)
        {
            var window = View.Overlay("确认移动文件夹", 514, 307);
            if (owner != null)
                window.Owner = owner;
            var body = View.Rounded(View.Paper, 15);
            body.Margin = new Thickness(5);
            body.BorderBrush = View.Edge;
            body.BorderThickness = new Thickness(1);
            body.Padding = new Thickness(19, 14, 19, 14);
            window.Content = body;
            var layout = new DockPanel();
            body.Child = layout;
            var title = View.Text("确认移动文件夹", 20, View.Ink);
            title.FontWeight = FontWeights.SemiBold;
            title.Margin = new Thickness(0, 0, 0, 10);
            DockPanel.SetDock(title, Dock.Top);
            layout.Children.Add(title);

            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = View.Button("取消", "取消移动", () => window.DialogResult = false);
            var move = View.Button("移动并加入", "移动并加入", () => window.DialogResult = true);
            cancel.IsCancel = true;
            cancel.Width = 68;
            move.Width = 100;
            move.Height = cancel.Height = 34;
            footer.Children.Add(cancel);
            footer.Children.Add(move);
            DockPanel.SetDock(footer, Dock.Bottom);
            layout.Children.Add(footer);

            var details = new StackPanel();
            AddPath(details, "原位置", plan.Source);
            AddPath(details, "移动到", plan.Target);
            var note = View.Text("折页里会添加指向新位置的快捷方式。移出折页时，桌面会得到快捷方式。", 13, View.Secondary);
            note.TextWrapping = TextWrapping.Wrap;
            note.Margin = new Thickness(0, 1, 0, 0);
            details.Children.Add(note);
            layout.Children.Add(details);

            FocusManager.SetFocusedElement(window, cancel);
            var cursor = WindowsShell.Cursor;
            WindowsShell.Place(window, cursor.X, cursor.Y, true);
            return window.ShowDialog() == true;
        }

        private static void AddPath(Panel panel, string label, string path)
        {
            var heading = View.Text(label, 12, View.Secondary);
            heading.Margin = new Thickness(0, 0, 0, 2);
            panel.Children.Add(heading);
            var text = new TextBox
            {
                Text = path, IsReadOnly = true, IsTabStop = false, FontFamily = View.Font, FontSize = 13,
                Foreground = View.Ink, Background = View.ColorBrush("#EEF2F9"), BorderThickness = new Thickness(0),
                Padding = new Thickness(7, 5, 7, 5), TextWrapping = TextWrapping.Wrap,
                MaxHeight = 55, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 0, 0, 7)
            };
            System.Windows.Automation.AutomationProperties.SetName(text, label);
            panel.Children.Add(text);
        }
    }
}
