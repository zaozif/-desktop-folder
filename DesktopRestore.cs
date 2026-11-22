using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Threading;

namespace DesktopFolders
{
    internal static class DesktopRestore
    {
        public static void Begin(ICollection<string> returned, IDictionary<string, Point> positions,
            string launcherShortcut, Action<int, bool> done)
        {
            if (positions.Count == 0)
            {
                if (done != null) done(0, false);
                return;
            }
            foreach (var path in returned)
                DesktopIcons.NotifyCreated(path);

            int attempt = 0;
            int steady = 0;
            int lastCount = 0;
            var check = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(180)
            };
            check.Tick += (sender, e) =>
            {
                attempt++;
                bool autoArrange = false;
                using (var desktop = DesktopIcons.Open())
                {
                    if (desktop != null)
                    {
                        autoArrange = desktop.AutoArrange;
                        lastCount = desktop.CountAtOriginalPositions(positions);
                        bool ready = desktop.VisibleCount(returned) == returned.Count;
                        steady = ready && lastCount == positions.Count ? steady + 1 : 0;
                        if (!autoArrange && lastCount < positions.Count && (ready || attempt >= 6) && attempt < 14)
                            desktop.PlaceAvailable(positions, launcherShortcut);
                    }
                }
                if (autoArrange || steady >= 2 || attempt >= 14)
                {
                    check.Stop();
                    if (done != null) done(positions.Count - lastCount, autoArrange);
                }
            };
            check.Start();
        }
    }
}
