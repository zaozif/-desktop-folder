using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using System.Windows.Media;

namespace DesktopFolders
{
    internal static class TrayTheme
    {
        private static readonly MenuColors Colors = new MenuColors();
        private static readonly Forms.ToolStripRenderer Renderer = new Forms.ToolStripProfessionalRenderer(Colors);
        private static readonly Drawing.Font Font = new Drawing.Font("Microsoft YaHei UI", 9);

        public static Forms.ContextMenuStrip CreateMenu()
        {
            return new Forms.ContextMenuStrip
            {
                Renderer = Renderer, Font = Font, ForeColor = Color(View.Ink), BackColor = Color(View.Paper),
                ShowImageMargin = false, ShowCheckMargin = false, Padding = new Forms.Padding(5, 4, 5, 4)
            };
        }

        private static Drawing.Color Color(Brush brush)
        {
            var value = ((SolidColorBrush)brush).Color;
            return Drawing.Color.FromArgb(value.A, value.R, value.G, value.B);
        }

        private sealed class MenuColors : Forms.ProfessionalColorTable
        {
            public MenuColors() { UseSystemColors = false; }
            public override Drawing.Color ToolStripDropDownBackground { get { return Color(View.Paper); } }
            public override Drawing.Color MenuBorder { get { return Color(View.Edge); } }
            public override Drawing.Color MenuItemBorder { get { return Color(View.Tint); } }
            public override Drawing.Color MenuItemSelected { get { return Color(View.Tint); } }
            public override Drawing.Color MenuItemPressedGradientBegin { get { return Color(View.Tint); } }
            public override Drawing.Color MenuItemPressedGradientMiddle { get { return Color(View.Tint); } }
            public override Drawing.Color MenuItemPressedGradientEnd { get { return Color(View.Tint); } }
            public override Drawing.Color SeparatorDark { get { return Color(View.Edge); } }
            public override Drawing.Color SeparatorLight { get { return Color(View.Paper); } }
        }
    }
}
