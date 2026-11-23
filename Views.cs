using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace DesktopFolders
{
    internal static partial class View
    {
        public static readonly Brush Ink = (Brush)Theme["Ink"];
        public static readonly Brush Secondary = (Brush)Theme["Secondary"];
        public static readonly Brush Paper = (Brush)Theme["Paper"];
        public static readonly Brush Edge = (Brush)Theme["Edge"];
        public static readonly Brush Tint = (Brush)Theme["Tint"];
        public static readonly Brush Accent = (Brush)Theme["Accent"];
        public static readonly Brush TileFill = ColorBrush("#121B2430");
        public static readonly Brush TileBorder = ColorBrush("#58FFFFFF");
        public static readonly FontFamily Font = new FontFamily("Microsoft YaHei UI, Segoe UI");

        public static Brush ColorBrush(string value)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            brush.Freeze();
            return brush;
        }

        public static TextBlock Text(string value, double size, Brush color)
        {
            return new TextBlock
            {
                Text = value, FontFamily = Font, FontSize = size,
                Foreground = color, TextTrimming = TextTrimming.CharacterEllipsis
            };
        }

        public static Button Button(object content, string label, Action click, bool primary = false)
        {
            var button = new Button
            {
                Content = content, Style = (Style)Theme[primary ? "PrimaryButton" : "SoftButton"], ToolTip = label, FontFamily = Font
            };
            AutomationProperties.SetName(button, label);
            if (click != null)
                button.Click += (sender, e) => click();
            return button;
        }

        public static MenuItem Menu(string name, Action action)
        {
            var menu = new MenuItem { Header = name, FontFamily = Font, Style = (Style)Theme["SoftMenuItem"] };
            menu.Click += (sender, e) => menu.Dispatcher.BeginInvoke(DispatcherPriority.Background, action);
            return menu;
        }

        public static Window Overlay(string title, double width, double height)
        {
            var window = new Window
            {
                Title = title, Width = width, Height = height, FontFamily = Font,
                WindowStyle = WindowStyle.None, WindowStartupLocation = WindowStartupLocation.Manual,
                ResizeMode = ResizeMode.NoResize, AllowsTransparency = true,
                Background = Brushes.Transparent, ShowInTaskbar = false
            };
            window.Resources.MergedDictionaries.Add(Theme);
            return window;
        }

        public static Border Rounded(Brush fill, double radius)
        {
            return new Border { Background = fill, CornerRadius = new CornerRadius(radius) };
        }

        public static UIElement AppIcon(ImageSource icon, int size)
        {
            if (icon != null)
                return new Image { Source = icon, Width = size, Height = size, Stretch = Stretch.Uniform };

            var card = Rounded(Tint, size / 5.0);
            card.Width = card.Height = size;
            var mark = new UniformGrid { Rows = 2, Columns = 2, Width = size * .48, Height = size * .48 };
            for (int i = 0; i < 4; i++)
                mark.Children.Add(new Border { Margin = new Thickness(1), Background = Accent, CornerRadius = new CornerRadius(1) });
            card.Child = mark;
            return card;
        }

    }

    internal sealed class FolderTile
    {
        public const int Width = 88;
        public const int Height = 92;
        private static readonly Brush RevealHitSurface = View.ColorBrush("#01000000");
        private static readonly Brush LightTabOutline = View.ColorBrush("#BCFFFFFF");
        private static readonly Brush DarkTabOutline = View.ColorBrush("#C91A1A1A");
        private static readonly Brush DarkTabInk = View.ColorBrush("#161616");
        public readonly Window Window;
        public readonly Folder Folder;
        private readonly Launcher launcher;
        private readonly Grid root;
        private readonly StackPanel stack;
        private readonly Border badge;
        private readonly TextBlock name;
        private readonly Border tab;
        private readonly TextBlock tabName;
        private readonly DispatcherTimer dismiss;
        private DockEdge edge;
        private bool? darkTabInk;
        private System.Drawing.Point start;
        private System.Drawing.Point original;
        private bool pressed;
        private bool moving;

        public FolderTile(Launcher launcher, Folder folder)
        {
            this.launcher = launcher;
            Folder = folder;
            Window = View.Overlay(Program.DisplayName + " · " + folder.Name, Width, Height);
            Window.ShowActivated = false;
            root = new Grid { Background = Brushes.Transparent, AllowDrop = true, Cursor = Cursors.Hand };
            stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
            badge = View.Rounded(View.TileFill, 16);
            badge.Width = badge.Height = 56;
            badge.BorderBrush = View.TileBorder;
            badge.BorderThickness = new Thickness(1);
            badge.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 10, Opacity = .12, ShadowDepth = 2 };
            stack.Children.Add(badge);
            name = View.Text(folder.Name, 12, Brushes.White);
            name.MaxWidth = Width - 3;
            name.FontWeight = FontWeights.Medium;
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.Margin = new Thickness(0, 5, 0, 0);
            name.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 5, Opacity = 1, ShadowDepth = 1 };
            stack.Children.Add(name);
            root.Children.Add(stack);
            tab = View.Rounded(RevealHitSurface, 5);
            tab.BorderBrush = LightTabOutline;
            tab.BorderThickness = new Thickness(1);
            tabName = View.Text(folder.Name, 12, Brushes.White);
            tabName.VerticalAlignment = VerticalAlignment.Center;
            tabName.HorizontalAlignment = HorizontalAlignment.Center;
            tabName.FontWeight = FontWeights.Medium;
            tabName.Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 3, Opacity = .9, ShadowDepth = 1 };
            tab.Child = tabName;
            root.Children.Add(tab);
            dismiss = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            dismiss.Tick += (s, e) =>
            {
                dismiss.Stop();
                if (!root.IsMouseOver && !root.ContextMenu.IsOpen && !pressed)
                    Reveal(false);
            };
            root.ContextMenu = View.MenuSurface();
            root.ContextMenu.Items.Add(View.Menu("打开文件夹", () => launcher.Toggle(this)));
            root.ContextMenu.Items.Add(View.Menu("重命名…", () => launcher.Rename(this)));
            root.ContextMenu.Items.Add(View.MenuSeparator());
            root.ContextMenu.Items.Add(View.Menu("新建文件夹…", launcher.NewFolder));
            root.ContextMenu.Items.Add(View.Menu("展开背景…", () => launcher.OpenFolderBackground(Folder)));
            root.ContextMenu.Items.Add(View.Menu("设置…", launcher.OpenSettings));
            var undo = View.Menu("撤销上次收纳", launcher.UndoLastImport);
            root.ContextMenu.Items.Add(undo);
            var remove = View.Menu("移除空文件夹", () => launcher.Remove(this));
            root.ContextMenu.Items.Add(remove);
            root.ContextMenu.Opened += (s, e) =>
            {
                undo.IsEnabled = launcher.CanUndoImport;
                remove.IsEnabled = Folder.Entries.Count == 0;
            };
            root.ContextMenu.Closed += (s, e) => ScheduleHide();
            root.MouseEnter += (s, e) =>
            {
                dismiss.Stop();
                if (edge != DockEdge.None) Reveal(true);
            };
            root.MouseLeave += (s, e) => ScheduleHide();
            root.MouseLeftButtonDown += Down;
            root.MouseMove += Move;
            root.MouseLeftButtonUp += Up;
            root.LostMouseCapture += (s, e) => pressed = false;
            root.DragOver += (s, e) =>
            {
                if (edge != DockEdge.None) Reveal(true);
                e.Effects = launcher.DropEffect(e.Data);
                badge.BorderBrush = e.Effects == DragDropEffects.None ? View.TileBorder : Brushes.White;
                e.Handled = true;
            };
            root.DragLeave += (s, e) => { badge.BorderBrush = View.TileBorder; ScheduleHide(); };
            root.Drop += (s, e) =>
            {
                badge.BorderBrush = View.TileBorder;
                ScheduleHide();
                launcher.Drop(Folder, Folder.Entries.Count, e);
            };
            Window.Content = root;
            Refresh();
            ApplyAppearance();
            WindowsShell.KeepOnDesktop(Window);
            ApplyPlacement();
            Window.Show();
            Window.Closed += (s, e) => dismiss.Stop();
        }

        public void ApplyPlacement()
        {
            SetEdge(Folder.Edge);
            WindowsShell.Place(Window, Folder.X, Folder.Y, false, 0, edge);
        }

        public void RefreshEdgeContrast()
        {
            if (edge == DockEdge.None || !Window.IsVisible || tab.ActualWidth <= 0 || tab.ActualHeight <= 0 ||
                PresentationSource.FromVisual(tab) == null)
                return;
            var topLeft = tab.PointToScreen(new Point(0, 0));
            var bottomRight = tab.PointToScreen(new Point(tab.ActualWidth, tab.ActualHeight));
            var pixelBounds = System.Drawing.Rectangle.FromLTRB((int)Math.Ceiling(topLeft.X), (int)Math.Ceiling(topLeft.Y),
                (int)Math.Floor(bottomRight.X), (int)Math.Floor(bottomRight.Y));
            var center = new System.Drawing.Point(pixelBounds.Left + pixelBounds.Width / 2, pixelBounds.Top + pixelBounds.Height / 2);
            bool vertical = DesktopDock.Vertical(edge);
            var handle = WindowsShell.Handle(Window);
            if (WindowsShell.TopWindowAt(center) != handle)
            {
                // The center may be click-through at low opacity. The outline still has visible pixels.
                var first = vertical ? new System.Drawing.Point(pixelBounds.Left, center.Y) :
                    new System.Drawing.Point(center.X, pixelBounds.Top);
                var second = vertical ? new System.Drawing.Point(pixelBounds.Right - 1, center.Y) :
                    new System.Drawing.Point(center.X, pixelBounds.Bottom - 1);
                if (WindowsShell.TopWindowAt(first) != handle || WindowsShell.TopWindowAt(second) != handle)
                    return;
            }

            double luminance;
            if (!EdgeContrast.TryRead(pixelBounds, vertical, out luminance))
                return;
            var dark = EdgeContrast.DarkInk(luminance, darkTabInk);
            if (darkTabInk == dark)
                return;
            darkTabInk = dark;
            tabName.Foreground = dark ? DarkTabInk : Brushes.White;
            tab.BorderBrush = dark ? DarkTabOutline : LightTabOutline;
            tabName.Effect = new DropShadowEffect
            {
                Color = dark ? Colors.White : Colors.Black, BlurRadius = 3, Opacity = .85, ShadowDepth = 1
            };
        }

        private void SetEdge(DockEdge value)
        {
            edge = value;
            bool docked = edge != DockEdge.None;
            bool vertical = DesktopDock.Vertical(edge);
            int thickness = DesktopDock.TabThickness;
            Window.Width = docked ? (vertical ? Width + thickness : DesktopDock.HorizontalLength) : Width;
            Window.Height = docked && !vertical ? Height + thickness : Height;
            tab.Width = vertical ? thickness : DesktopDock.HorizontalLength;
            tab.Height = vertical ? Height : thickness;
            tab.HorizontalAlignment = edge == DockEdge.Left ? HorizontalAlignment.Left :
                edge == DockEdge.Right ? HorizontalAlignment.Right : HorizontalAlignment.Center;
            tab.VerticalAlignment = edge == DockEdge.Bottom ? VerticalAlignment.Bottom : VerticalAlignment.Top;
            tabName.MaxWidth = (vertical ? Height : DesktopDock.HorizontalLength) - 10;
            tabName.LayoutTransform = vertical ? new RotateTransform(edge == DockEdge.Left ? -90 : 90) : Transform.Identity;
            tab.Visibility = docked ? Visibility.Visible : Visibility.Collapsed;
            stack.Margin = new Thickness(edge == DockEdge.Left ? thickness : 0,
                6 + (edge == DockEdge.Top ? thickness : 0), edge == DockEdge.Right ? thickness : 0, 0);
            Reveal(false);
        }

        private void Reveal(bool value)
        {
            var visible = edge == DockEdge.None || value;
            stack.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            root.Background = edge != DockEdge.None && value ? RevealHitSurface : Brushes.Transparent;
        }

        private void ScheduleHide()
        {
            if (edge != DockEdge.None)
            {
                dismiss.Stop();
                dismiss.Start();
            }
        }

        public void ApplyAppearance()
        {
            Window.Opacity = Preferences.Opacity(launcher.Preferences.TileOpacity);
        }

        public void Refresh()
        {
            Window.Title = Program.DisplayName + " · " + Folder.Name;
            AutomationProperties.SetName(root, Folder.Name);
            name.Text = Folder.Name;
            tabName.Text = Folder.Name;
            tab.ToolTip = Folder.Name;
            var previews = new UniformGrid { Rows = 2, Columns = 2, Margin = new Thickness(7) };
            for (int index = 0; index < 4; index++)
            {
                if (index < Folder.Entries.Count)
                    previews.Children.Add(View.AppIcon(launcher.Icon(Folder.Entries[index]), 16));
                else
                {
                    var slot = View.Rounded(View.ColorBrush("#35FFFFFF"), 4);
                    slot.Width = slot.Height = 13;
                    previews.Children.Add(slot);
                }
            }
            badge.Child = previews;
        }

        private void Down(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 1)
                return;
            start = Pointer(e);
            original = WindowsShell.Bounds(Window).Location;
            moving = false;
            pressed = root.CaptureMouse();
            e.Handled = pressed;
        }

        private void Move(object sender, MouseEventArgs e)
        {
            if (pressed && e.LeftButton == MouseButtonState.Pressed)
                UpdateDrag(Pointer(e));
        }

        private void UpdateDrag(System.Drawing.Point point)
        {
            int x = point.X - start.X;
            int y = point.Y - start.Y;
            if (!moving && Math.Abs(x) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            if (!moving)
            {
                launcher.ClosePanel();
                if (edge != DockEdge.None)
                {
                    var scale = VisualTreeHelper.GetDpi(Window).DpiScaleX;
                    SetEdge(DockEdge.None);
                    original = new System.Drawing.Point(start.X - (int)(Width * scale / 2), start.Y - (int)(Height * scale / 2));
                }
            }
            moving = true;
            root.Cursor = Cursors.SizeAll;
            WindowsShell.Place(Window, original.X + x, original.Y + y, false);
        }

        private void Up(object sender, MouseButtonEventArgs e)
        {
            if (!pressed)
                return;
            var cursor = Pointer(e);
            UpdateDrag(cursor);
            root.ReleaseMouseCapture();
            root.Cursor = Cursors.Hand;
            if (moving)
            {
                var point = WindowsShell.Bounds(Window).Location;
                launcher.PersistPosition(this, point, cursor);
            }
            else
                launcher.Toggle(this);
            e.Handled = true;
        }

        private System.Drawing.Point Pointer(MouseEventArgs e)
        {
            var point = root.PointToScreen(e.GetPosition(root));
            return new System.Drawing.Point((int)Math.Round(point.X), (int)Math.Round(point.Y));
        }
    }

    internal sealed class FolderPanel
    {
        public readonly Window Window;
        public readonly FolderTile Tile;
        private readonly Launcher launcher;
        private readonly UniformGrid grid;
        private readonly TextBlock title;
        private readonly TextBlock empty;
        private readonly ScrollViewer scroll;
        private readonly PanelSurface surface = new PanelSurface();
        private bool dragging;
        private bool menuOpen;
        private const int Columns = 4;
        private const int CellHeight = 90;

        public FolderPanel(Launcher launcher, FolderTile tile)
        {
            this.launcher = launcher;
            Tile = tile;
            Window = View.Overlay(tile.Folder.Name + " · " + Program.DisplayName, 396, 170);
            var background = View.Rounded(Brushes.Transparent, 16);
            background.Margin = new Thickness(7);
            background.BorderThickness = new Thickness(1);
            surface.Layer.Effect = new DropShadowEffect { Color = Color.FromRgb(36, 50, 71), Opacity = .19, BlurRadius = 19, ShadowDepth = 2 };
            var layout = new DockPanel { Margin = new Thickness(15, 11, 15, 13), AllowDrop = true, Background = Brushes.Transparent };
            var layers = new Grid();
            layers.Children.Add(surface.Layer);
            layers.Children.Add(layout);
            background.Child = layers;
            var header = new DockPanel { Height = 38, Margin = new Thickness(1, 0, 0, 7), LastChildFill = true };
            var x = View.Text("×", 21, View.Ink);
            var plus = View.Text("+", 21, View.Ink);
            var close = View.Button(x, "收起文件夹", launcher.ClosePanel);
            var add = View.Button(plus, "添加快捷方式", () => launcher.PickShortcuts(tile.Folder));
            close.Width = add.Width = 34;
            DockPanel.SetDock(close, Dock.Right);
            DockPanel.SetDock(add, Dock.Right);
            header.Children.Add(close);
            header.Children.Add(add);
            title = View.Text(tile.Folder.Name, 20, View.Ink);
            title.FontWeight = FontWeights.SemiBold;
            title.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(title);
            DockPanel.SetDock(header, Dock.Top);
            layout.Children.Add(header);

            var body = new Grid();
            empty = View.Text("拖入桌面快捷方式，或点右上角添加。", 13, View.Secondary);
            empty.HorizontalAlignment = HorizontalAlignment.Center;
            empty.VerticalAlignment = VerticalAlignment.Center;
            body.Children.Add(empty);
            BindInk(x);
            BindInk(plus);
            BindInk(empty);
            grid = new UniformGrid { Columns = Columns, VerticalAlignment = VerticalAlignment.Top };
            scroll = new ScrollViewer
            {
                Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            body.Children.Add(scroll);
            layout.Children.Add(body);
            layout.DragOver += (s, e) => { e.Effects = launcher.DropEffect(e.Data); e.Handled = true; };
            layout.Drop += (s, e) => launcher.Drop(tile.Folder, tile.Folder.Entries.Count, e);
            Window.Content = background;
            Window.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape) { e.Handled = true; launcher.ClosePanel(); }
            };
            Window.Deactivated += (s, e) => Window.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!Window.IsActive && !launcher.DialogOpen && !dragging && !menuOpen)
                    launcher.ClosePanel(this);
            }));
            ApplyAppearance();
            Refresh();
            var anchor = WindowsShell.Bounds(tile.Window);
            WindowsShell.Place(Window, anchor.X, anchor.Y, true, anchor.Width);
            Window.Show();
            Window.Activate();

            if (SystemParameters.ClientAreaAnimation)
            {
                var scale = new ScaleTransform(0.91, 0.91);
                background.RenderTransformOrigin = new Point(.5, .38);
                background.RenderTransform = scale;
                var animation = new DoubleAnimation(0.91, 1, TimeSpan.FromMilliseconds(135))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
            }
        }

        public void ApplyAppearance()
        {
            surface.Apply(Tile.Folder.Background ?? launcher.Preferences, launcher.GetPanelImage(Tile.Folder));
            title.Foreground = surface.Text;
        }

        private void BindInk(TextBlock text)
        {
            text.SetBinding(TextBlock.ForegroundProperty, new Binding("Foreground") { Source = title });
        }

        public void Refresh()
        {
            var folder = Tile.Folder;
            title.Text = folder.Name;
            Window.Title = folder.Name + " · " + Program.DisplayName;
            grid.Children.Clear();
            int rows = (folder.Entries.Count + Columns - 1) / Columns;
            var screen = System.Windows.Forms.Screen.GetWorkingArea(WindowsShell.Bounds(Tile.Window).Location);
            var dpi = VisualTreeHelper.GetDpi(Tile.Window).DpiScaleY;
            double contentLimit = Math.Max(CellHeight, Math.Min(4 * CellHeight + CellHeight / 2, screen.Height / dpi - 96));
            Window.Height = 91 + Math.Min(contentLimit, Math.Max(CellHeight, rows * CellHeight));
            empty.Visibility = rows == 0 ? Visibility.Visible : Visibility.Collapsed;
            scroll.Visibility = rows == 0 ? Visibility.Collapsed : Visibility.Visible;
            foreach (var entry in folder.Entries)
                grid.Children.Add(Card(folder, entry));
            if (rows != 0)
                for (int index = folder.Entries.Count; index < rows * Columns; index++)
                    grid.Children.Add(new Border { Height = CellHeight });
        }

        private UIElement Card(Folder folder, Entry entry)
        {
            var stack = new StackPanel { Width = 73 };
            var icon = View.AppIcon(launcher.Icon(entry), 35);
            stack.Children.Add(icon);
            var label = View.Text(entry.Name, 12, View.Ink);
            BindInk(label);
            label.TextAlignment = TextAlignment.Center;
            label.Margin = new Thickness(1, 8, 1, 0);
            stack.Children.Add(label);
            var button = View.Button(stack, entry.Name, null);
            button.ToolTip = entry.Name + " · 双击打开";
            AutomationProperties.SetHelpText(button, "双击打开，拖动排序");
            button.Margin = new Thickness(4, 2, 4, 4);
            button.ContextMenu = View.MenuSurface();
            button.ContextMenu.Items.Add(View.Menu("打开", () => launcher.Launch(entry)));
            button.ContextMenu.Items.Add(View.Menu("移回桌面", () => launcher.Restore(folder, entry)));
            button.ContextMenu.Opened += (s, e) => menuOpen = true;
            button.ContextMenu.Closed += (s, e) => menuOpen = false;
            var slot = new Grid { Height = CellHeight };
            slot.Children.Add(button);
            var marker = View.Rounded(View.Accent, 2);
            marker.Width = 3;
            marker.Margin = new Thickness(0, 10, 0, 11);
            marker.IsHitTestVisible = false;
            marker.Visibility = Visibility.Collapsed;
            slot.Children.Add(marker);

            Point start = new Point();
            bool doubleClick = false;
            button.PreviewMouseLeftButtonDown += (s, e) =>
            {
                start = e.GetPosition(button);
                doubleClick = e.ClickCount == 2;
            };
            button.Click += (s, e) =>
            {
                if (!doubleClick)
                    return;
                doubleClick = false;
                launcher.Launch(entry);
            };
            button.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    e.Handled = true;
                    launcher.Launch(entry);
                }
            };
            button.PreviewMouseMove += (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || !button.IsMouseCaptured || dragging)
                    return;
                Point point = e.GetPosition(button);
                if (Math.Abs(point.X - start.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(point.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance)
                    return;

                dragging = true;
                doubleClick = false;
                e.Handled = true;
                button.ReleaseMouseCapture();
                bool released = false;
                QueryContinueDragEventHandler detectRelease = (source, drag) =>
                {
                    released = !drag.EscapePressed && (drag.KeyStates & DragDropKeyStates.LeftMouseButton) == 0;
                };
                button.QueryContinueDrag += detectRelease;
                try
                {
                    var effect = DragDrop.DoDragDrop(button, new DataObject(Launcher.EntryFormat, entry.Id), DragDropEffects.Move);
                    if (effect == DragDropEffects.None && released && launcher.CursorOnDesktop())
                        launcher.Restore(folder, entry);
                }
                finally
                {
                    button.QueryContinueDrag -= detectRelease;
                    dragging = false;
                    marker.Visibility = Visibility.Collapsed;
                }
            };
            slot.DragOver += (s, e) =>
            {
                e.Effects = launcher.DropEffect(e.Data);
                bool anotherEntry = !e.Data.GetDataPresent(Launcher.EntryFormat) ||
                    e.Data.GetData(Launcher.EntryFormat) as string != entry.Id;
                marker.Visibility = anotherEntry && e.Effects != DragDropEffects.None ? Visibility.Visible : Visibility.Collapsed;
                marker.HorizontalAlignment = e.GetPosition(slot).X < slot.ActualWidth / 2 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                e.Handled = true;
            };
            slot.DragLeave += (s, e) => marker.Visibility = Visibility.Collapsed;
            slot.Drop += (s, e) =>
            {
                marker.Visibility = Visibility.Collapsed;
                int index = folder.Entries.IndexOf(entry);
                if (e.GetPosition(slot).X >= slot.ActualWidth / 2)
                    index++;
                launcher.Drop(folder, index, e);
            };
            return slot;
        }
    }
}
