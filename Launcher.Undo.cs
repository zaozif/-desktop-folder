using System;
using System.Collections.Generic;
using System.Linq;
using Drawing = System.Drawing;

namespace DesktopFolders
{
    internal sealed partial class Launcher
    {
        private List<ImportUndo> lastImport = new List<ImportUndo>();

        public bool CanUndoImport { get { return lastImport.Any(item => FindImportFolder(item) != null); } }

        private Folder FindImportFolder(ImportUndo item)
        {
            return store.Folders.FirstOrDefault(folder => folder.Entries.Contains(item.Entry));
        }

        public void UndoLastImport()
        {
            if (DialogOpen || exiting || !CanUndoImport)
                return;
            var messages = new List<string>();
            var changed = new HashSet<Folder>();
            var returned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var positions = new Dictionary<string, Drawing.Point>(StringComparer.OrdinalIgnoreCase);
            var owner = panel == null ? null : panel.Window;
            DialogOpen = true;
            try
            {
                for (int index = lastImport.Count - 1; index >= 0; index--)
                {
                    var item = lastImport[index];
                    var folder = FindImportFolder(item);
                    if (folder == null)
                    {
                        lastImport.RemoveAt(index);
                        continue;
                    }
                    try
                    {
                        var path = store.UndoImport(folder, item, (source, target) => WindowsShell.MoveDirectory(source, target, owner));
                        lastImport.RemoveAt(index);
                        icons.Remove(item.Entry.Id);
                        changed.Add(folder);
                        if (actualDesktop && path != null && returned.Add(path) && item.Position.HasValue)
                            positions[path] = item.Position.Value;
                    }
                    catch (Exception error) { messages.Add(item.Entry.Name + "：" + error.Message); }
                }
            }
            finally { DialogOpen = false; }
            foreach (var folder in changed) Refresh(folder);
            UpdateTray();
            DesktopRestore.Begin(returned, positions, desktopShortcut, null);
            if (messages.Count != 0) Notify("这些项目暂未撤销，可处理后重试。\n" + String.Join("\n", messages));
        }
    }
}
