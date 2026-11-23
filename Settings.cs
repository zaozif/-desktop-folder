using System;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Serialization;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DesktopFolders
{
    public class PanelAppearance
    {
        public int PanelOpacity { get; set; }
        public string PanelColor { get; set; }
        public string PanelImage { get; set; }
        public int ImageRotation { get; set; }
        public double CropX { get; set; }
        public double CropY { get; set; }
        public double CropWidth { get; set; }
        public double CropHeight { get; set; }

        [XmlIgnore]
        public Rect ImageCrop
        {
            get
            {
                if (!(CropX >= 0 && CropX < 1 && CropY >= 0 && CropY < 1 && CropWidth > 0 && CropHeight > 0 &&
                      CropX + CropWidth <= 1.000001 && CropY + CropHeight <= 1.000001))
                    return BackgroundPicture.FullArea;
                return new Rect(CropX, CropY, Math.Min(1 - CropX, CropWidth), Math.Min(1 - CropY, CropHeight));
            }
            set
            {
                CropX = value.X;
                CropY = value.Y;
                CropWidth = value.Width;
                CropHeight = value.Height;
            }
        }

        public PanelAppearance()
        {
            PanelOpacity = 100;
            PanelColor = "#F7F9FE";
            ImageCrop = BackgroundPicture.FullArea;
        }

        public PanelAppearance CopyBackground()
        {
            return new PanelAppearance
            {
                PanelOpacity = PanelOpacity, PanelColor = PanelColor, PanelImage = PanelImage,
                ImageRotation = ImageRotation, ImageCrop = ImageCrop
            };
        }
    }

    public sealed class Preferences : PanelAppearance
    {
        public int TileOpacity { get; set; }
        public bool RestoreAtExit { get; set; }
        public bool CustomizeTaskbar { get; set; }
        public int TaskbarOpacity { get; set; }

        public Preferences()
        {
            TileOpacity = 100;
            RestoreAtExit = true;
            TaskbarOpacity = 85;
        }

        public Preferences Copy() { return (Preferences)MemberwiseClone(); }
        public static double Opacity(int percent) { return Math.Min(100, Math.Max(20, percent)) / 100.0; }
    }

    internal sealed class StartupShortcut
    {
        private readonly string directory;
        private readonly string executable;
        private string LinkPath { get { return Path.Combine(directory, "桌面折页.lnk"); } }

        public StartupShortcut(string directory, string executable)
        {
            this.directory = directory;
            this.executable = executable;
        }

        public bool Enabled { get { return File.Exists(LinkPath); } }

        public void SetEnabled(bool enabled)
        {
            if (enabled)
            {
                Directory.CreateDirectory(directory);
                WindowsShell.CreateLink(LinkPath, executable);
            }
            else if (Enabled)
                File.Delete(LinkPath);
        }
    }

    internal sealed class PanelSurface
    {
        public readonly Grid Layer = new Grid();
        private readonly Border image = View.Rounded(null, 15);
        private readonly Border color = View.Rounded(View.Paper, 15);
        public Brush Text { get; private set; }

        public PanelSurface()
        {
            color.BorderBrush = View.Edge;
            color.BorderThickness = new Thickness(1);
            Layer.IsHitTestVisible = false;
            Layer.Children.Add(image);
            Layer.Children.Add(color);
        }

        public void Apply(PanelAppearance prefs, ImageSource source)
        {
            var tint = (Color)ColorConverter.ConvertFromString(prefs.PanelColor);
            var linearLuminance = .2126 * tint.ScR + .7152 * tint.ScG + .0722 * tint.ScB;
            Text = linearLuminance < .28 ? Brushes.White : View.Ink;
            image.Background = source == null ? null : new ImageBrush(source) { Stretch = Stretch.UniformToFill };
            tint.A = source == null ? (byte)255 : (byte)166;
            color.Background = new SolidColorBrush(tint);
            Layer.Opacity = Preferences.Opacity(prefs.PanelOpacity);
        }

    }

    internal sealed class SettingsDialog
    {
        private readonly Window window;
        private readonly Preferences global;
        private readonly PanelAppearance draft;
        private readonly CheckBox startup;
        private readonly PanelSurface previewPanel = new PanelSurface();
        private readonly Border previewTile;
        private readonly TextBlock previewLabel;
        private readonly TextBlock imageName;
        private readonly Button clearImage;
        private readonly Button editImage;
        private readonly Button colorButton;
        private ImageSource previewImage;
        private bool useGlobal;
        public Preferences Value { get { return global; } }
        public PanelAppearance FolderValue { get { return useGlobal ? null : draft; } }
        public bool StartWithWindows { get { return startup != null && startup.IsChecked == true; } }

        public SettingsDialog(Preferences preferences, ImageSource image, bool startsWithWindows, Folder folder = null,
            Action<bool, int> previewTaskbar = null)
        {
            bool forFolder = folder != null;
            global = preferences.Copy();
            draft = forFolder ? (folder.Background ?? preferences).CopyBackground() : (PanelAppearance)global;
            previewImage = image;
            window = View.Overlay(forFolder ? "文件夹展开背景" : "桌面折页设置", 542, forFolder ? 279 : 452);
            var body = View.Rounded(View.Paper, 15);
            body.Margin = new Thickness(5);
            body.BorderThickness = new Thickness(1);
            body.BorderBrush = View.Edge;
            body.Padding = new Thickness(20, 16, 20, 15);
            window.Content = body;
            var layout = new DockPanel();
            body.Child = layout;
            var title = View.Text(forFolder ? folder.Name + " · 展开背景" : "桌面折页设置", 21, View.Ink);
            title.FontWeight = FontWeights.SemiBold;
            title.Margin = new Thickness(0, 0, 0, 14);
            DockPanel.SetDock(title, Dock.Top);
            layout.Children.Add(title);

            var footer = new DockPanel();
            if (forFolder)
            {
                var reset = View.Button("使用全局背景", "使用全局背景", () =>
                {
                    useGlobal = true;
                    window.DialogResult = true;
                });
                reset.Width = 100;
                reset.Height = 34;
                DockPanel.SetDock(reset, Dock.Left);
                footer.Children.Add(reset);
            }
            var decisions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var close = View.Button("取消", "取消", () => window.DialogResult = false);
            var save = View.Button("保存", "保存设置", () => window.DialogResult = true);
            close.Width = save.Width = 68;
            close.Height = save.Height = 34;
            close.IsCancel = true;
            save.IsDefault = true;
            decisions.Children.Add(close);
            decisions.Children.Add(save);
            footer.Children.Add(decisions);
            DockPanel.SetDock(footer, Dock.Bottom);
            layout.Children.Add(footer);

            var preview = new StackPanel { Width = 167, Margin = new Thickness(14, 0, 0, 0) };
            DockPanel.SetDock(preview, Dock.Right);
            layout.Children.Add(preview);
            var previewHeading = View.Text("外观预览", 12, View.Secondary);
            previewHeading.Margin = new Thickness(0, 1, 0, 7);
            preview.Children.Add(previewHeading);
            var samples = View.Rounded(View.ColorBrush("#CEDCF3"), 12);
            samples.Height = forFolder ? 115 : 198;
            samples.Padding = new Thickness(9);
            preview.Children.Add(samples);
            var sampleLayout = new StackPanel();
            samples.Child = sampleLayout;
            if (!forFolder)
            {
                previewTile = View.Rounded(View.TileFill, 12);
                previewTile.BorderBrush = View.TileBorder;
                previewTile.BorderThickness = new Thickness(1);
                previewTile.Width = previewTile.Height = 48;
                previewTile.HorizontalAlignment = HorizontalAlignment.Left;
                previewTile.Margin = new Thickness(3, 8, 0, 12);
                previewTile.Child = View.AppIcon(null, 28);
                sampleLayout.Children.Add(previewTile);
            }
            var samplePanel = new Grid { Height = 90 };
            samplePanel.Children.Add(previewPanel.Layer);
            previewLabel = View.Text(forFolder ? folder.Name : "常用", 14, View.Ink);
            previewLabel.Margin = new Thickness(11, 9, 0, 0);
            samplePanel.Children.Add(previewLabel);
            var exampleIcon = View.AppIcon(null, 27) as FrameworkElement;
            exampleIcon.HorizontalAlignment = HorizontalAlignment.Left;
            exampleIcon.VerticalAlignment = VerticalAlignment.Bottom;
            exampleIcon.Margin = new Thickness(13, 0, 0, 11);
            samplePanel.Children.Add(exampleIcon);
            sampleLayout.Children.Add(samplePanel);

            var controls = new StackPanel();
            layout.Children.Add(controls);
            if (!forFolder)
                controls.Children.Add(OpacityControl("桌面图标不透明度", global.TileOpacity, value => global.TileOpacity = value));
            controls.Children.Add(OpacityControl("展开背景不透明度", draft.PanelOpacity, value => draft.PanelOpacity = value));
            var label = View.Text("面板背景", 13, View.Ink);
            label.Margin = new Thickness(0, 5, 0, 2);
            controls.Children.Add(label);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            controls.Children.Add(actions);
            colorButton = View.Button("颜色", "选择面板颜色", ChooseColor);
            var chooseImage = View.Button("选图片…", "选择面板背景图片", ChooseImage);
            editImage = View.Button("裁剪 / 旋转", "裁剪和旋转背景图片", EditImage);
            clearImage = View.Button("移除", "移除面板背景图片", () =>
            {
                draft.PanelImage = null;
                ResetImageEdits();
                previewImage = null;
                UpdatePreview();
            });
            foreach (var button in new[] { colorButton, chooseImage, editImage, clearImage })
            {
                button.MinWidth = button == colorButton ? 80 : button == editImage ? 76 : 61;
                button.Height = 27;
                actions.Children.Add(button);
            }
            imageName = View.Text("", 12, View.Secondary);
            imageName.Margin = new Thickness(0, 2, 0, 8);
            controls.Children.Add(imageName);
            if (!forFolder)
            {
                startup = new CheckBox { Content = "开机自启动", IsChecked = startsWithWindows, Margin = new Thickness(0, 5, 0, 8) };
                var restore = new CheckBox { Content = "退出时把收纳的快捷方式放回桌面", IsChecked = global.RestoreAtExit };
                restore.Checked += (s, e) => global.RestoreAtExit = true;
                restore.Unchecked += (s, e) => global.RestoreAtExit = false;
                controls.Children.Add(startup);
                controls.Children.Add(restore);

                var taskbar = new CheckBox
                {
                    Content = "自定义任务栏背景透明度", IsChecked = global.CustomizeTaskbar, Margin = new Thickness(0, 12, 0, 5)
                };
                var opacity = OpacityControl("任务栏背景不透明度", global.TaskbarOpacity, value =>
                {
                    global.TaskbarOpacity = value;
                    if (previewTaskbar != null) previewTaskbar(global.CustomizeTaskbar, value);
                });
                opacity.IsEnabled = global.CustomizeTaskbar;
                taskbar.Checked += (s, e) =>
                {
                    global.CustomizeTaskbar = opacity.IsEnabled = true;
                    if (previewTaskbar != null) previewTaskbar(true, global.TaskbarOpacity);
                };
                taskbar.Unchecked += (s, e) =>
                {
                    global.CustomizeTaskbar = opacity.IsEnabled = false;
                    if (previewTaskbar != null) previewTaskbar(false, global.TaskbarOpacity);
                };
                controls.Children.Add(taskbar);
                controls.Children.Add(opacity);
            }
            UpdatePreview();
        }

        private FrameworkElement OpacityControl(string label, int value, Action<int> changed)
        {
            var group = new StackPanel { Margin = new Thickness(0, 1, 0, 8) };
            var title = new DockPanel();
            var number = View.Text(value + "%", 12, View.Secondary);
            DockPanel.SetDock(number, Dock.Right);
            title.Children.Add(number);
            title.Children.Add(View.Text(label, 13, View.Ink));
            group.Children.Add(title);
            var slider = new Slider { Minimum = 20, Maximum = 100, Value = value, Height = 24, TickFrequency = 1, IsSnapToTickEnabled = true };
            AutomationProperties.SetName(slider, label);
            slider.ValueChanged += (s, e) =>
            {
                int percentage = (int)e.NewValue;
                changed(percentage);
                number.Text = percentage + "%";
                UpdatePreview();
            };
            group.Children.Add(slider);
            return group;
        }

        private void ChooseColor()
        {
            var color = (Color)ColorConverter.ConvertFromString(draft.PanelColor);
            using (var dialog = new Forms.ColorDialog { FullOpen = true, Color = Drawing.Color.FromArgb(color.R, color.G, color.B) })
            {
                if (dialog.ShowDialog() == Forms.DialogResult.OK)
                {
                    draft.PanelColor = String.Format("#{0:X2}{1:X2}{2:X2}", dialog.Color.R, dialog.Color.G, dialog.Color.B);
                    UpdatePreview();
                }
            }
        }

        private void ChooseImage()
        {
            var dialog = new OpenFileDialog { Title = "选择背景图片", Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff" };
            if (dialog.ShowDialog(window) != true)
                return;
            try
            {
                previewImage = BackgroundPicture.Open(dialog.FileName);
                draft.PanelImage = dialog.FileName;
                ResetImageEdits();
                UpdatePreview();
            }
            catch (Exception error)
            {
                MessageBox.Show(window, "无法打开这张图片。\n" + error.Message, "桌面折页", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void EditImage()
        {
            try
            {
                BitmapSource original = BackgroundPicture.Open(draft.PanelImage);
                var editor = new BackgroundEditor(draft, original);
                if (!editor.Show(window))
                    return;
                draft.ImageCrop = editor.Selection;
                draft.ImageRotation = editor.Rotation;
                previewImage = BackgroundPicture.Render(original, editor.Rotation, editor.Selection);
                UpdatePreview();
            }
            catch (Exception error)
            {
                MessageBox.Show(window, "无法编辑这张图片。\n" + error.Message, "桌面折页", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ResetImageEdits()
        {
            draft.ImageRotation = 0;
            draft.ImageCrop = BackgroundPicture.FullArea;
        }

        private void UpdatePreview()
        {
            if (previewTile != null)
                previewTile.Opacity = Preferences.Opacity(global.TileOpacity);
            previewPanel.Apply(draft, previewImage);
            previewLabel.Foreground = previewPanel.Text;
            imageName.Text = String.IsNullOrEmpty(draft.PanelImage) ? "纯色背景" : Path.GetFileName(draft.PanelImage);
            clearImage.IsEnabled = draft.PanelImage != null;
            editImage.IsEnabled = previewImage != null;
            colorButton.Content = "颜色 " + draft.PanelColor;
        }

        public bool Show()
        {
            var point = WindowsShell.Cursor;
            WindowsShell.Place(window, point.X, point.Y, true);
            return window.ShowDialog() == true;
        }
    }
}
