using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace DesktopFolders
{
    internal static class BackgroundPicture
    {
        public static readonly Rect FullArea = new Rect(0, 0, 1, 1);
        public const double MinimumSelection = .015;

        public static BitmapSource Open(string path)
        {
            if (String.IsNullOrEmpty(path))
                return null;
            using (var stream = File.OpenRead(path))
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = 1800;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
        }

        public static int NormalizeRotation(int angle)
        {
            return ((angle / 90 % 4 + 4) % 4) * 90;
        }

        public static Rect TurnSelection(Rect area, int direction)
        {
            return direction > 0 ? new Rect(Math.Max(0, 1 - area.Bottom), area.Left, area.Height, area.Width) :
                new Rect(area.Top, Math.Max(0, 1 - area.Right), area.Height, area.Width);
        }

        public static BitmapSource Rotate(BitmapSource source, int angle)
        {
            int rotation = NormalizeRotation(angle);
            if (rotation == 0)
                return source;
            var rotated = new TransformedBitmap(source, new RotateTransform(rotation));
            rotated.Freeze();
            return rotated;
        }

        public static BitmapSource Render(BitmapSource source, int angle, Rect area)
        {
            if (source == null)
                return null;
            var rotated = Rotate(source, angle);
            if (area == FullArea)
                return rotated;
            int x = Math.Max(0, Math.Min(rotated.PixelWidth - 1, (int)Math.Floor(area.Left * rotated.PixelWidth)));
            int y = Math.Max(0, Math.Min(rotated.PixelHeight - 1, (int)Math.Floor(area.Top * rotated.PixelHeight)));
            int right = Math.Max(x + 1, Math.Min(rotated.PixelWidth, (int)Math.Ceiling(area.Right * rotated.PixelWidth)));
            int bottom = Math.Max(y + 1, Math.Min(rotated.PixelHeight, (int)Math.Ceiling(area.Bottom * rotated.PixelHeight)));
            var cropped = new CroppedBitmap(rotated, new Int32Rect(x, y, right - x, bottom - y));
            cropped.Freeze();
            return cropped;
        }

        public static BitmapSource Render(PanelAppearance preferences)
        {
            return Render(Open(preferences.PanelImage), preferences.ImageRotation, preferences.ImageCrop);
        }
    }

    internal sealed class BackgroundEditor
    {
        private enum DragMode { None, Draw, Move, Resize }

        private readonly Window window;
        private readonly BitmapSource source;
        private readonly Canvas canvas;
        private readonly Image image;
        private readonly System.Windows.Shapes.Path shade;
        private readonly Rectangle frame;
        private readonly Rectangle[] handles = new Rectangle[4];
        private readonly TextBlock status;
        private Rect imageBounds;
        private DragMode dragMode;
        private Point start;
        private Point opposite;
        private Rect previous;
        public Rect Selection { get; private set; }
        public int Rotation { get; private set; }

        public BackgroundEditor(PanelAppearance preferences, BitmapSource source)
        {
            this.source = source;
            Selection = preferences.ImageCrop;
            Rotation = BackgroundPicture.NormalizeRotation(preferences.ImageRotation);
            window = View.Overlay("裁剪和旋转背景", 594, 456);
            var body = View.DialogSurface(new Thickness(17, 13, 17, 13));
            window.Content = body;
            var layout = new DockPanel();
            body.Child = layout;

            var heading = new StackPanel { Margin = new Thickness(0, 1, 0, 10) };
            var title = View.DialogHeading(window, "裁剪和旋转背景", 0);
            heading.Children.Add(title);
            var note = View.Text("拖出选区或拉动四角。按住 Shift 可在选区内重新框选。", 13, View.Secondary);
            note.Margin = new Thickness(0, 4, 0, 0);
            heading.Children.Add(note);
            DockPanel.SetDock(heading, Dock.Top);
            layout.Children.Add(heading);

            var controls = new DockPanel { Height = 43, Margin = new Thickness(0, 8, 0, 0) };
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var button in new[]
            {
                View.Button("左转 90°", "向左旋转 90 度", () => Turn(-1)),
                View.Button("右转 90°", "向右旋转 90 度", () => Turn(1)),
                View.Button("重置", "重置裁剪和旋转", Reset)
            })
            {
                button.MinWidth = 72;
                button.Height = 33;
                actions.Children.Add(button);
            }
            DockPanel.SetDock(actions, Dock.Left);
            controls.Children.Add(actions);

            var decisions = new StackPanel { Orientation = Orientation.Horizontal };
            var cancel = View.Button("取消", "取消编辑", () => window.DialogResult = false);
            var apply = View.Button("应用", "应用裁剪和旋转", () => window.DialogResult = true, primary: true);
            apply.Margin = new Thickness(6, 0, 0, 0);
            cancel.IsCancel = true;
            apply.IsDefault = true;
            foreach (var button in new[] { cancel, apply })
            {
                button.MinWidth = 63;
                button.Height = 33;
                decisions.Children.Add(button);
            }
            DockPanel.SetDock(decisions, Dock.Right);
            controls.Children.Add(decisions);
            status = View.Text("", 12, View.Secondary);
            status.VerticalAlignment = VerticalAlignment.Center;
            status.HorizontalAlignment = HorizontalAlignment.Center;
            controls.Children.Add(status);
            DockPanel.SetDock(controls, Dock.Bottom);
            layout.Children.Add(controls);

            var preview = View.Rounded(View.ColorBrush("#303D4F"), 12);
            preview.Padding = new Thickness(11);
            canvas = new Canvas { Background = Brushes.Transparent, ClipToBounds = true, Cursor = Cursors.Cross };
            preview.Child = canvas;
            layout.Children.Add(preview);
            image = new Image { Stretch = Stretch.Fill, IsHitTestVisible = false };
            shade = new System.Windows.Shapes.Path { Fill = View.ColorBrush("#B61A2536"), IsHitTestVisible = false };
            frame = new Rectangle { Stroke = Brushes.White, StrokeThickness = 1.5, IsHitTestVisible = false };
            canvas.Children.Add(image);
            canvas.Children.Add(shade);
            canvas.Children.Add(frame);
            for (int index = 0; index < handles.Length; index++)
            {
                var handle = new Rectangle
                {
                    Width = 8, Height = 8, Fill = Brushes.White, Stroke = View.Accent,
                    StrokeThickness = 1, RadiusX = 1, RadiusY = 1, IsHitTestVisible = false
                };
                canvas.Children.Add(handle);
                handles[index] = handle;
            }
            canvas.SizeChanged += (sender, e) => FitImage();
            canvas.MouseLeftButtonDown += BeginDrag;
            canvas.MouseMove += Drag;
            canvas.MouseLeftButtonUp += EndDrag;
            canvas.LostMouseCapture += (sender, e) => dragMode = DragMode.None;
            UpdateImage();
        }

        public bool Show(Window owner)
        {
            window.Owner = owner;
            var bounds = WindowsShell.Bounds(owner);
            WindowsShell.Place(window, bounds.Left + bounds.Width / 2, bounds.Top + 30, true);
            return window.ShowDialog() == true;
        }

        private void Turn(int direction)
        {
            Rotation = BackgroundPicture.NormalizeRotation(Rotation + 90 * direction);
            Selection = BackgroundPicture.TurnSelection(Selection, direction);
            UpdateImage();
        }

        private void Reset()
        {
            Rotation = 0;
            Selection = BackgroundPicture.FullArea;
            UpdateImage();
        }

        private void UpdateImage()
        {
            image.Source = BackgroundPicture.Rotate(source, Rotation);
            FitImage();
        }

        private void FitImage()
        {
            var bitmap = (BitmapSource)image.Source;
            if (canvas.ActualWidth <= 12 || canvas.ActualHeight <= 12)
                return;
            double scale = Math.Min((canvas.ActualWidth - 12) / bitmap.PixelWidth, (canvas.ActualHeight - 12) / bitmap.PixelHeight);
            double width = bitmap.PixelWidth * scale;
            double height = bitmap.PixelHeight * scale;
            imageBounds = new Rect((canvas.ActualWidth - width) / 2, (canvas.ActualHeight - height) / 2, width, height);
            Place(image, imageBounds);
            DrawSelection();
        }

        private void DrawSelection()
        {
            var bounds = new Rect(imageBounds.X + Selection.X * imageBounds.Width, imageBounds.Y + Selection.Y * imageBounds.Height,
                Selection.Width * imageBounds.Width, Selection.Height * imageBounds.Height);
            var mask = new GeometryGroup { FillRule = FillRule.EvenOdd };
            mask.Children.Add(new RectangleGeometry(imageBounds));
            mask.Children.Add(new RectangleGeometry(bounds));
            shade.Data = mask;
            Place(frame, bounds);
            var corners = Corners(bounds);
            for (int index = 0; index < corners.Length; index++)
            {
                Canvas.SetLeft(handles[index], corners[index].X - handles[index].Width / 2);
                Canvas.SetTop(handles[index], corners[index].Y - handles[index].Height / 2);
            }
            status.Text = Rotation + "°";
        }

        private static void Place(FrameworkElement element, Rect bounds)
        {
            Canvas.SetLeft(element, bounds.X);
            Canvas.SetTop(element, bounds.Y);
            element.Width = bounds.Width;
            element.Height = bounds.Height;
        }

        private static Point[] Corners(Rect area)
        {
            return new[] { area.TopLeft, area.TopRight, area.BottomLeft, area.BottomRight };
        }

        private void BeginDrag(object sender, MouseButtonEventArgs e)
        {
            var point = e.GetPosition(canvas);
            if (imageBounds.Width == 0 || imageBounds.Height == 0)
                return;
            var hitArea = imageBounds;
            hitArea.Inflate(8, 8);
            if (!hitArea.Contains(point))
                return;

            start = ToRelative(point);
            previous = Selection;
            var corners = Corners(previous);
            dragMode = DragMode.Draw;
            for (int index = 0; index < corners.Length; index++)
            {
                var corner = corners[index];
                if (Math.Abs(corner.X - start.X) * imageBounds.Width <= 9 &&
                    Math.Abs(corner.Y - start.Y) * imageBounds.Height <= 9)
                {
                    opposite = corners[3 - index];
                    dragMode = DragMode.Resize;
                    break;
                }
            }
            if (dragMode == DragMode.Draw && previous != BackgroundPicture.FullArea &&
                previous.Contains(start) && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
                dragMode = DragMode.Move;
            if (!canvas.CaptureMouse())
                dragMode = DragMode.None;
            e.Handled = true;
        }

        private void Drag(object sender, MouseEventArgs e)
        {
            if (dragMode == DragMode.None)
                return;
            var point = ToRelative(e.GetPosition(canvas));
            if (dragMode == DragMode.Move)
            {
                var delta = point - start;
                Selection = new Rect(Clamp(previous.X + delta.X, 1 - previous.Width),
                    Clamp(previous.Y + delta.Y, 1 - previous.Height), previous.Width, previous.Height);
            }
            else
            {
                var anchor = dragMode == DragMode.Resize ? opposite : start;
                var candidate = new Rect(anchor, point);
                if (candidate.Width < BackgroundPicture.MinimumSelection || candidate.Height < BackgroundPicture.MinimumSelection)
                    return;
                Selection = candidate;
            }
            DrawSelection();
        }

        private void EndDrag(object sender, MouseButtonEventArgs e)
        {
            if (dragMode == DragMode.None)
                return;
            Drag(sender, e);
            canvas.ReleaseMouseCapture();
            e.Handled = true;
        }

        private Point ToRelative(Point point)
        {
            return new Point(Clamp((point.X - imageBounds.X) / imageBounds.Width, 1),
                Clamp((point.Y - imageBounds.Y) / imageBounds.Height, 1));
        }

        private static double Clamp(double value, double high)
        {
            return Math.Min(Math.Max(0, high), Math.Max(0, value));
        }
    }
}
