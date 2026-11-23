using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DesktopFolders
{
    internal sealed class TaskbarOpacity : IDisposable
    {
        private const int AccentPolicyAttribute = 19;
        private const int TransparentGradient = 2;
        private const uint DwmCompositionChanged = 0x031e;
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
            var lightTheme = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "SystemUsesLightTheme", 1);
            uint tint = Object.Equals(lightTheme, 0) ? 0u : 0xffffffu;
            var background = new AccentPolicy { State = TransparentGradient, Flags = 2, Color = ((uint)targetAlpha << 24) | tint };
            bool applied = true;
            foreach (var bar in bars)
                applied &= Apply(bar, explorer, background);
            return applied;
        }

        private bool Apply(IntPtr bar, uint explorer, AccentPolicy background)
        {
            OriginalAppearance original;
            if (!changed.TryGetValue(bar, out original))
            {
                original = new OriginalAppearance { Process = explorer };
                original.Read = AccessAccent(bar, ref original.Policy, false);
            }

            // Reapply when Explorer refreshes its own appearance, for example after opening Start.
            if (!AccessAccent(bar, ref background, true))
                return false;
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
            var policy = original.Policy;
            AccessAccent(bar, ref policy, true);
            if (!original.Read)
                PostMessage(bar, DwmCompositionChanged, new IntPtr(1), IntPtr.Zero);
        }

        private static bool AccessAccent(IntPtr bar, ref AccentPolicy policy, bool set)
        {
            int size = Marshal.SizeOf(typeof(AccentPolicy));
            var data = new CompositionAttribute { Attribute = AccentPolicyAttribute, Policy = Marshal.AllocHGlobal(size), Size = (uint)size };
            try
            {
                Marshal.StructureToPtr(policy, data.Policy, false);
                if (set)
                    return SetWindowCompositionAttribute(bar, ref data);
                if (!GetWindowCompositionAttribute(bar, ref data))
                    return false;
                policy = (AccentPolicy)Marshal.PtrToStructure(data.Policy, typeof(AccentPolicy));
                return true;
            }
            finally { Marshal.FreeHGlobal(data.Policy); }
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
            public uint Process;
            public AccentPolicy Policy;
            public bool Read;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public int State, Flags;
            public uint Color;
            public int Animation;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CompositionAttribute
        {
            public int Attribute;
            public IntPtr Policy;
            public uint Size;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string windowClass, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr previous, string windowClass, string title);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
        [DllImport("user32.dll")] private static extern bool GetWindowCompositionAttribute(IntPtr window, ref CompositionAttribute data);
        [DllImport("user32.dll")] private static extern bool SetWindowCompositionAttribute(IntPtr window, ref CompositionAttribute data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    }
}
