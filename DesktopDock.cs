using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using Forms = System.Windows.Forms;

namespace DesktopFolders
{
    public enum DockEdge { None, Left, Right, Top, Bottom }

    internal static class DesktopDock
    {
        public const int TabThickness = 23;
        public const int HorizontalLength = 108;

        public static bool Vertical(DockEdge edge) { return edge == DockEdge.Left || edge == DockEdge.Right; }

        public static DockEdge At(Point cursor, Forms.Screen screen)
        {
            var others = Forms.Screen.AllScreens.Where(other => other.DeviceName != screen.DeviceName).Select(other => other.Bounds);
            return At(cursor, screen.WorkingArea, screen.Bounds, others, (int)Math.Ceiling(20 * WindowsShell.SystemScale));
        }

        public static DockEdge At(Point cursor, Rectangle work, Rectangle screen, IEnumerable<Rectangle> otherScreens, int threshold)
        {
            var edges = new[]
            {
                new { Edge = DockEdge.Left, Distance = cursor.X - work.Left, Outside = new Point(screen.Left - 1, cursor.Y) },
                new { Edge = DockEdge.Right, Distance = work.Right - 1 - cursor.X, Outside = new Point(screen.Right, cursor.Y) },
                new { Edge = DockEdge.Top, Distance = cursor.Y - work.Top, Outside = new Point(cursor.X, screen.Top - 1) },
                new { Edge = DockEdge.Bottom, Distance = work.Bottom - 1 - cursor.Y, Outside = new Point(cursor.X, screen.Bottom) }
            };
            var best = edges.Where(edge => edge.Distance <= threshold && !otherScreens.Any(rectangle => rectangle.Contains(edge.Outside)))
                .OrderBy(edge => edge.Distance).FirstOrDefault();
            return best == null ? DockEdge.None : best.Edge;
        }

        public static int Slot(int desired, int start, int end, int length, IEnumerable<Rectangle> neighbors, bool vertical)
        {
            var available = neighbors.Select(rectangle => vertical
                ? new { Start = rectangle.Top, End = rectangle.Bottom }
                : new { Start = rectangle.Left, End = rectangle.Right }).ToArray();
            int max = Math.Max(start, end - length);
            desired = Math.Max(start, Math.Min(max, desired));
            var candidates = new[] { desired, start, max }.Concat(available.SelectMany(span => new[] { span.Start - length - 3, span.End + 3 }));
            return candidates.Where(point => point >= start && point <= max &&
                available.All(span => point + length + 2 <= span.Start || point >= span.End + 2))
                .OrderBy(point => Math.Abs(point - desired)).DefaultIfEmpty(desired).First();
        }
    }
}
