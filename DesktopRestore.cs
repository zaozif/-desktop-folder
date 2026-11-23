using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace DesktopFolders
{
    internal static class DesktopRestore
    {
        public static Action Begin(ICollection<string> returned, IDictionary<string, Point> positions,
            string launcherShortcut, Action<IList<string>, bool> done)
        {
            foreach (var path in returned)
                DesktopIcons.NotifyCreated(path);
            if (positions.Count == 0)
            {
                if (done != null) done(new string[0], false);
                return null;
            }

            int attempt = 0;
            int steady = 0;
            var missing = positions.Keys.ToArray();
            var check = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(180)
            };
            check.Tick += (sender, e) =>
            {
                attempt++;
                bool autoArrange = false;
                try
                {
                    using (var desktop = DesktopIcons.Open())
                    {
                        if (desktop != null)
                        {
                            autoArrange = desktop.AutoArrange;
                            missing = positions.Where(pair => desktop.Position(pair.Key) != pair.Value).Select(pair => pair.Key).ToArray();
                            bool ready = desktop.VisibleCount(returned) == returned.Count;
                            steady = ready && missing.Length == 0 ? steady + 1 : 0;
                            if (!autoArrange && missing.Length != 0 && (ready || attempt >= 6) && attempt < 14)
                                desktop.PlaceAvailable(positions, launcherShortcut);
                        }
                    }
                }
                catch (COMException) { steady = 0; }
                if (autoArrange || steady >= 2 || attempt >= 14)
                {
                    check.Stop();
                    if (done != null) done(missing, autoArrange);
                }
            };
            check.Start();
            return check.Stop;
        }
    }
}
