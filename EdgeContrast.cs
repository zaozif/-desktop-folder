using System;
using System.ComponentModel;
using System.Drawing;

namespace DesktopFolders
{
    internal static class EdgeContrast
    {
        public static bool TryRead(Rectangle bounds, bool vertical, out double luminance)
        {
            luminance = 0;
            if (bounds.Width < 8 || bounds.Height < 8)
                return false;
            try
            {
                using (var pixels = new Bitmap(bounds.Width, bounds.Height))
                using (var surface = Graphics.FromImage(pixels))
                {
                    surface.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
                    luminance = ReadMargins(pixels, vertical);
                }
                return true;
            }
            catch (Win32Exception) { return false; }
            catch (ArgumentException) { return false; }
        }

        internal static double ReadMargins(Bitmap image, bool vertical)
        {
            int length = vertical ? image.Height : image.Width;
            int thickness = vertical ? image.Width : image.Height;
            // Sample the transparent margins of the tab, clear of the border and its text.
            int inset = Math.Max(2, (int)(thickness * .12));
            var samples = new double[10];
            for (int index = 0; index < 5; index++)
            {
                int along = (int)((length - 1) * (.16 + index * .17));
                var first = vertical ? image.GetPixel(inset, along) : image.GetPixel(along, inset);
                var second = vertical ? image.GetPixel(thickness - 1 - inset, along) : image.GetPixel(along, thickness - 1 - inset);
                samples[index * 2] = RelativeLuminance(first);
                samples[index * 2 + 1] = RelativeLuminance(second);
            }
            Array.Sort(samples);
            return (samples[4] + samples[5]) / 2;
        }

        public static bool DarkInk(double luminance, bool? previous)
        {
            // The gap prevents flicker when a live wallpaper hovers around the crossover.
            double threshold = previous == true ? .15 : previous == false ? .21 : .179;
            return luminance >= threshold;
        }

        private static double RelativeLuminance(Color color)
        {
            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }

        private static double Linear(byte value)
        {
            double channel = value / 255.0;
            return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4);
        }
    }
}
