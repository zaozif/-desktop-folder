using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace DesktopFolders
{
    internal static partial class View
    {
        private static ResourceDictionary Theme { get { return ThemeResources.Value; } }

        private static class ThemeResources
        {
            internal static readonly ResourceDictionary Value = Load();

            private static ResourceDictionary Load()
            {
                using (var resource = typeof(View).Assembly.GetManifestResourceStream("DesktopFolders.ViewTheme.xaml"))
                    return (ResourceDictionary)XamlReader.Load(resource);
            }
        }

        public static Border DialogSurface(Thickness padding)
        {
            return new Border
            {
                Background = Paper, BorderBrush = Edge, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16),
                Margin = new Thickness(5), Padding = padding
            };
        }

        public static TextBlock DialogHeading(Window window, string caption, double bottom = 12)
        {
            var heading = Text(caption, 21, Ink);
            heading.FontWeight = FontWeights.SemiBold;
            heading.Margin = new Thickness(0, 0, 0, bottom);
            heading.Background = Brushes.Transparent;
            heading.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState != MouseButtonState.Pressed) return;
                e.Handled = true;
                window.DragMove();
            };
            return heading;
        }

        public static TabControl Sections()
        {
            return new TabControl { Style = (Style)Theme["SectionTabs"], Margin = new Thickness(0, 0, 0, 10), FontFamily = Font };
        }

        public static ContextMenu MenuSurface()
        {
            var menu = new ContextMenu { Style = (Style)Theme["SoftContextMenu"], FontFamily = Font };
            menu.Resources.MergedDictionaries.Add(Theme);
            return menu;
        }

        public static Separator MenuSeparator()
        {
            return new Separator { Style = (Style)Theme["SoftSeparator"] };
        }

        public static void ShowNotice(string title, string text, Window owner = null)
        {
            double height = Math.Min(356, Math.Max(180, 128 + text.Split('\n').Length * 20));
            var window = Overlay(title, 493, height);
            if (owner != null && owner.IsVisible)
                window.Owner = owner;
            var body = DialogSurface(new Thickness(21, 16, 21, 16));
            var layout = new DockPanel();
            body.Child = layout;
            window.Content = body;
            var heading = DialogHeading(window, title);
            DockPanel.SetDock(heading, Dock.Top);
            layout.Children.Add(heading);
            var ok = Button("关闭", "关闭", window.Close, primary: true);
            ok.Width = 72;
            ok.Height = 34;
            ok.HorizontalAlignment = HorizontalAlignment.Right;
            ok.IsDefault = ok.IsCancel = true;
            DockPanel.SetDock(ok, Dock.Bottom);
            layout.Children.Add(ok);
            layout.Children.Add(new TextBox
            {
                Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontSize = 13,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 0, 10)
            });
            var point = WindowsShell.Cursor;
            WindowsShell.Place(window, point.X, point.Y, true);
            window.ShowDialog();
        }
    }
}
