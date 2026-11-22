using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace DesktopFolders
{
    internal sealed class TaskbarOpacity : IDisposable
    {
        private const int ExtendedStyle = -20;
        private const int Layered = 0x00080000;
        private const uint UseAlpha = 0x00000002;
        private readonly Dictionary<IntPtr, OriginalAppearance> changed = new Dictionary<IntPtr, OriginalAppearance>();
        private byte targetAlpha = 255;

        public bool Set(bool enabled, int opacity)
        {
            if (!enabled || opacity >= 100)
            {
                targetAlpha = 255;
                Restore();
                return true;
            }
            targetAlpha = (byte)Math.Round(Math.Max(20, opacity) * 255.0 / 100);
            return Refresh();
        }

        public bool Refresh()
        {
            if (targetAlpha == 255)
                return true;
            var primary = FindWindow("Shell_TrayWnd", null);
            var explorer = Owner(primary);
            if (explorer == 0)
                return false;

            var bars = new List<IntPtr> { primary };
            for (var secondary = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_SecondaryTrayWnd", null);
                secondary != IntPtr.Zero;
                secondary = FindWindowEx(IntPtr.Zero, secondary, "Shell_SecondaryTrayWnd", null))
                if (Owner(secondary) == explorer)
                    bars.Add(secondary);

            foreach (var obsolete in changed.Keys.Where(window => !bars.Contains(window) || changed[window].Process != explorer).ToArray())
            {
                RestoreBar(obsolete, changed[obsolete]);
                changed.Remove(obsolete);
            }
            bool applied = true;
            foreach (var bar in bars)
                applied &= Apply(bar, explorer);
            return applied;
        }

        private bool Apply(IntPtr bar, uint explorer)
        {
            OriginalAppearance original;
            if (!changed.TryGetValue(bar, out original))
            {
                original = new OriginalAppearance
                {
                    Process = explorer, OriginallyLayered = (GetWindowLong(bar, ExtendedStyle) & Layered) != 0,
                    Alpha = 255, AppliedAlpha = 255
                };
                if (original.OriginallyLayered &&
                    !GetLayeredWindowAttributes(bar, out original.Color, out original.Alpha, out original.Flags))
                    return false;
            }
            else if (original.AppliedAlpha == targetAlpha)
            {
                uint currentColor, currentFlags;
                byte currentAlpha;
                if (GetLayeredWindowAttributes(bar, out currentColor, out currentAlpha, out currentFlags) &&
                    currentAlpha == targetAlpha && (currentFlags & UseAlpha) != 0)
                    return true;
            }

            int style = GetWindowLong(bar, ExtendedStyle);
            if ((style & Layered) == 0)
                SetWindowLong(bar, ExtendedStyle, style | Layered);
            if (!SetLayeredWindowAttributes(bar, original.Color, targetAlpha, original.Flags | UseAlpha))
            {
                RestoreBar(bar, original);
                changed.Remove(bar);
                return false;
            }
            original.AppliedAlpha = targetAlpha;
            changed[bar] = original;
            return true;
        }

        private void Restore()
        {
            foreach (var bar in changed)
                RestoreBar(bar.Key, bar.Value);
            changed.Clear();
        }

        private static void RestoreBar(IntPtr bar, OriginalAppearance original)
        {
            if (Owner(bar) != original.Process)
                return;
            var windowClass = WindowsShell.ClassName(bar);
            if (!String.Equals(windowClass, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(windowClass, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase))
                return;
            if (original.OriginallyLayered)
                SetLayeredWindowAttributes(bar, original.Color, original.Alpha, original.Flags);
            else
            {
                int currentStyle = GetWindowLong(bar, ExtendedStyle);
                if ((currentStyle & Layered) != 0)
                    SetWindowLong(bar, ExtendedStyle, currentStyle & ~Layered);
            }
            RedrawWindow(bar, IntPtr.Zero, IntPtr.Zero, 0x0185);
        }

        public void Dispose()
        {
            targetAlpha = 255;
            Restore();
        }

        private static uint Owner(IntPtr window)
        {
            uint process;
            return window != IntPtr.Zero && GetWindowThreadProcessId(window, out process) != 0 ? process : 0;
        }

        private sealed class OriginalAppearance
        {
            public uint Process, Color, Flags;
            public byte Alpha, AppliedAlpha;
            public bool OriginallyLayered;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string windowClass, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr previous, string windowClass, string title);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr window, int index, int style);
        [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr window, uint color, byte alpha, uint flags);
        [DllImport("user32.dll")] private static extern bool GetLayeredWindowAttributes(IntPtr window, out uint color, out byte alpha, out uint flags);
        [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr window, IntPtr region, IntPtr update, uint flags);
    }
}
