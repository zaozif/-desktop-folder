using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace DesktopFolders
{
    public sealed class Folder
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public DockEdge Edge { get; set; }
        public PanelAppearance Background { get; set; }
        public List<Entry> Entries { get; set; }

        public Folder() { Entries = new List<Entry>(); }
    }

    public sealed class Entry
    {
        public string Id { get; set; }
        public string FileName { get; set; }
        public string OriginalPath { get; set; }
        public string OriginalShortcutPath { get; set; }
        public string FolderTarget { get; set; }
        public string PendingFolderSource { get; set; }
        public string ShortcutSource { get; set; }
        public int? DesktopX { get; set; }
        public int? DesktopY { get; set; }
        public string ReturnedName { get; set; }

        [XmlIgnore]
        public string Name { get { return Path.GetFileNameWithoutExtension(FileName); } }

        [XmlIgnore]
        public Point? DesktopPosition
        {
            get { return DesktopX.HasValue && DesktopY.HasValue ? new Point(DesktopX.Value, DesktopY.Value) : (Point?)null; }
            set { DesktopX = value.HasValue ? value.Value.X : (int?)null; DesktopY = value.HasValue ? value.Value.Y : (int?)null; }
        }

        [XmlIgnore]
        public bool ReturnOnExit { get { return OriginalPath == null && ShortcutSource != "external" && PendingFolderSource == null; } }
    }

    internal sealed class Store
    {
        private readonly string root;
        private readonly string statePath;
        private readonly string preferencesPath;
        private readonly XmlSerializer serializer = new XmlSerializer(typeof(List<Folder>));
        private readonly XmlSerializer preferencesSerializer = new XmlSerializer(typeof(Preferences));
        public readonly string Desktop;
        private readonly string publicDesktop;
        public readonly List<Folder> Folders;
        public readonly bool IsNew;
        internal string DataDirectory { get { return root; } }
        public Preferences Preferences { get; private set; }

        public Store(string root, string desktop, string publicDesktop = null)
        {
            this.root = root;
            Desktop = desktop;
            this.publicDesktop = publicDesktop ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
            Directory.CreateDirectory(root);
            statePath = Path.Combine(root, "folders.xml");
            preferencesPath = Path.Combine(root, "preferences.xml");
            IsNew = !File.Exists(statePath);
            if (IsNew)
                Folders = new List<Folder>();
            else
                using (var input = File.OpenRead(statePath))
                    Folders = (List<Folder>)serializer.Deserialize(input);
            Preferences = new Preferences();
            if (File.Exists(preferencesPath))
                using (var input = File.OpenRead(preferencesPath))
                    Preferences = (Preferences)preferencesSerializer.Deserialize(input);
        }

        public static bool IsShortcut(string path)
        {
            var extension = Path.GetExtension(path);
            return new[] { ".lnk", ".url", ".appref-ms" }.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }

        public static bool CanImport(string path)
        {
            return Directory.Exists(path) || File.Exists(path) &&
                (IsShortcut(path) || String.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase));
        }

        public string LaunchPath(Entry entry)
        {
            return entry.OriginalPath ?? Path.Combine(root, "shortcuts", entry.Id, entry.FileName);
        }

        public void SaveExitReport(IEnumerable<string> notices)
        {
            File.WriteAllText(Path.Combine(root, "退出还原记录.txt"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") +
                Environment.NewLine + Environment.NewLine + String.Join(Environment.NewLine + Environment.NewLine, notices));
        }

        private string StoredFile(Entry entry)
        {
            return Path.Combine(root, "shortcuts", entry.Id, entry.FileName);
        }

        private static void Save(string path, XmlSerializer format, object value)
        {
            var pending = path + ".new";
            using (var output = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                format.Serialize(output, value);
                output.Flush(true);
            }
            if (File.Exists(path))
                File.Replace(pending, path, null);
            else
                File.Move(pending, path);
        }

        public void SavePreferences(Preferences preferences)
        {
            Save(preferencesPath, preferencesSerializer, preferences);
            Preferences = preferences;
        }

        private void Edit(Action apply, Action undo)
        {
            apply();
            try { Save(statePath, serializer, Folders); }
            catch
            {
                undo();
                throw;
            }
        }

        public void AddFolders(List<Folder> folders)
        {
            int start = Folders.Count;
            Edit(() => Folders.AddRange(folders), () => Folders.RemoveRange(start, folders.Count));
        }

        public void Rename(Folder folder, string name)
        {
            var old = folder.Name;
            Edit(() => folder.Name = name, () => folder.Name = old);
        }

        public void SetBackground(Folder folder, PanelAppearance appearance)
        {
            var original = folder.Background;
            Edit(() => folder.Background = appearance, () => folder.Background = original);
        }

        public void Position(Folder folder, int x, int y, DockEdge edge)
        {
            int oldX = folder.X, oldY = folder.Y;
            var oldEdge = folder.Edge;
            Edit(() => { folder.X = x; folder.Y = y; folder.Edge = edge; },
                () => { folder.X = oldX; folder.Y = oldY; folder.Edge = oldEdge; });
        }

        public void RemoveFolder(Folder folder)
        {
            if (folder.Entries.Count != 0)
                throw new InvalidOperationException("请先把文件夹里的应用移出。");
            int index = Folders.IndexOf(folder);
            Edit(() => Folders.RemoveAt(index), () => Folders.Insert(index, folder));
        }

        public bool IsDesktopItem(string path)
        {
            return (Directory.Exists(path) || IsShortcut(path)) && IsOnDesktop(path);
        }

        private bool IsOnDesktop(string path)
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            return String.Equals(directory, Desktop, StringComparison.OrdinalIgnoreCase) ||
                String.Equals(directory, publicDesktop, StringComparison.OrdinalIgnoreCase);
        }

        public string Import(Folder folder, string source, Point? position)
        {
            if (!CanImport(source) || Directory.Exists(source))
                throw new InvalidOperationException("请选择应用程序或 Windows 快捷方式。");

            source = Path.GetFullPath(source);
            var entry = new Entry { Id = Guid.NewGuid().ToString("N"), FileName = Path.GetFileName(source) };
            if (!IsShortcut(source))
            {
                entry.OriginalPath = source;
                if (!folder.Entries.Any(item => String.Equals(item.OriginalPath, source, StringComparison.OrdinalIgnoreCase)))
                    Edit(() => folder.Entries.Add(entry), () => folder.Entries.Remove(entry));
                return IsOnDesktop(source) ? "已加入「" + entry.Name + "」。桌面上的 EXE 是程序文件，仍保存在原处。" : null;
            }

            bool onDesktop = IsOnDesktop(source);
            var equivalent = folder.Entries.FirstOrDefault(item => item.OriginalPath == null &&
                String.Equals(item.FileName, entry.FileName, StringComparison.OrdinalIgnoreCase) && SameContents(StoredFile(item), source));
            if (equivalent != null)
            {
                if (!onDesktop)
                    return null;
                RecordDesktopOriginal(equivalent, source, position);
                return TakeOffDesktop(equivalent, source);
            }

            entry.ShortcutSource = onDesktop ? "desktop" : "external";
            entry.OriginalShortcutPath = source;
            entry.DesktopPosition = onDesktop ? position : null;
            var destination = StoredFile(entry);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            try
            {
                File.Copy(source, destination);
                File.SetAttributes(destination, File.GetAttributes(destination) & ~FileAttributes.ReadOnly);
                Edit(() => folder.Entries.Add(entry), () => folder.Entries.Remove(entry));
            }
            catch
            {
                CleanStoredFile(destination);
                throw;
            }

            return onDesktop ? TakeOffDesktop(entry, source) : null;
        }

        public FolderImport PlanFolderImport(string source, string storage)
        {
            source = Path.GetFullPath(source);
            if (!Directory.Exists(source))
                throw new DirectoryNotFoundException("找不到文件夹「" + source + "」。");
            if (String.Equals(source, Path.GetPathRoot(source), StringComparison.OrdinalIgnoreCase))
                throw new IOException("请选择盘符里的文件夹。");
            source = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!IsOnDesktop(source) || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                return new FolderImport(source, source);

            storage = Path.GetFullPath(storage).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (storage.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("收纳目录位于要移动的文件夹内。");
            var name = Path.GetFileName(source);
            var destination = Path.Combine(storage, name);
            if (String.Equals(destination, source, StringComparison.OrdinalIgnoreCase))
                return new FolderImport(source, source);
            for (int index = 2; Directory.Exists(destination) || File.Exists(destination); index++)
                destination = Path.Combine(storage, name + " (" + index + ")");
            return new FolderImport(source, destination);
        }

        public void ImportFolder(Folder folder, FolderImport plan, Point? position, Action<string, string> move)
        {
            if (!Directory.Exists(plan.Source))
                throw new DirectoryNotFoundException("原文件夹已不在确认时的位置。");
            if (folder.Entries.Any(item => String.Equals(item.FolderTarget, plan.Target, StringComparison.OrdinalIgnoreCase)))
                return;
            var entry = new Entry
            {
                Id = Guid.NewGuid().ToString("N"),
                FileName = Path.GetFileName(plan.Source) + ".lnk",
                ShortcutSource = plan.MovesOriginal ? "desktop" : "external",
                DesktopPosition = plan.MovesOriginal ? position : null,
                FolderTarget = plan.Target,
                PendingFolderSource = plan.MovesOriginal ? plan.Source : null
            };
            var link = StoredFile(entry);
            Directory.CreateDirectory(Path.GetDirectoryName(link));
            try
            {
                WindowsShell.CreateLink(link, plan.Target, true);
                Edit(() => folder.Entries.Add(entry), () => folder.Entries.Remove(entry));
            }
            catch
            {
                CleanStoredFile(link);
                throw;
            }
            if (!plan.MovesOriginal)
                return;
            try
            {
                if (File.Exists(plan.Target) || Directory.Exists(plan.Target))
                    throw new IOException("目标位置已被占用，请重新拖入确认。");
                move(plan.Source, plan.Target);
                if (Directory.Exists(plan.Source) || !Directory.Exists(plan.Target))
                    throw new IOException("Windows 未完成文件夹移动。");
            }
            catch
            {
                if (Directory.Exists(plan.Source))
                    DiscardPendingFolder(folder, entry);
                else if (Directory.Exists(plan.Target))
                {
                    FinishFolderMove(entry);
                    return;
                }
                throw;
            }
            FinishFolderMove(entry);
        }

        public void RecoverFolderImports()
        {
            foreach (var folder in Folders)
                foreach (var entry in folder.Entries.Where(item => item.PendingFolderSource != null).ToArray())
                {
                    if (Directory.Exists(entry.PendingFolderSource))
                        DiscardPendingFolder(folder, entry);
                    else if (Directory.Exists(entry.FolderTarget))
                        FinishFolderMove(entry);
                }
        }

        private void FinishFolderMove(Entry entry)
        {
            var link = StoredFile(entry);
            var refreshed = Path.Combine(Path.GetDirectoryName(link), Guid.NewGuid().ToString("N") + ".lnk");
            try
            {
                // Bind the final shortcut after Explorer can identify the target as a directory.
                WindowsShell.CreateLink(refreshed, entry.FolderTarget, true);
                File.Replace(refreshed, link, null);
            }
            finally { if (File.Exists(refreshed)) File.Delete(refreshed); }
            var source = entry.PendingFolderSource;
            Edit(() => entry.PendingFolderSource = null, () => entry.PendingFolderSource = source);
        }

        private void DiscardPendingFolder(Folder folder, Entry entry)
        {
            int index = folder.Entries.IndexOf(entry);
            Edit(() => folder.Entries.RemoveAt(index), () => folder.Entries.Insert(index, entry));
            CleanStoredFile(StoredFile(entry));
        }

        public IEnumerable<KeyValuePair<Entry, string>> LegacyPublicOriginals()
        {
            if (String.IsNullOrEmpty(publicDesktop))
                yield break;
            foreach (var entry in Folders.SelectMany(folder => folder.Entries))
            {
                if (entry.OriginalPath != null || entry.OriginalShortcutPath != null ||
                    (entry.ShortcutSource != null && entry.ShortcutSource != "external"))
                    continue;
                var source = Path.Combine(publicDesktop, entry.FileName);
                if (SameContents(StoredFile(entry), source))
                    yield return new KeyValuePair<Entry, string>(entry, source);
            }
        }

        public string CollectLegacyOriginal(Entry entry, string source, Point? position)
        {
            RecordDesktopOriginal(entry, source, position);
            return TakeOffDesktop(entry, source);
        }

        private void RecordDesktopOriginal(Entry entry, string source, Point? position)
        {
            var oldSource = entry.ShortcutSource;
            var oldPath = entry.OriginalShortcutPath;
            var oldPosition = entry.DesktopPosition;
            Edit(() =>
            {
                entry.ShortcutSource = "desktop";
                entry.OriginalShortcutPath = source;
                entry.DesktopPosition = position ?? oldPosition;
            }, () =>
            {
                entry.ShortcutSource = oldSource;
                entry.OriginalShortcutPath = oldPath;
                entry.DesktopPosition = oldPosition;
            });
        }

        private string TakeOffDesktop(Entry entry, string source)
        {
            // The group already contains an exact, saved copy before the original is removed.
            if (!File.Exists(source))
                return null;
            if (!SameContents(StoredFile(entry), source))
            {
                KeepDesktopReference(entry);
                return "快捷方式「" + entry.Name + "」在收纳时发生变化，原图标仍在桌面。";
            }
            try
            {
                File.Delete(source);
                return null;
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }

            KeepDesktopReference(entry);
            return "已加入「" + entry.Name + "」。Windows 未允许移走原快捷方式，移出分组时会使用已有图标。";
        }

        private void KeepDesktopReference(Entry entry)
        {
            Edit(() => entry.ShortcutSource = "desktop-reference", () => entry.ShortcutSource = "desktop");
        }

        public void MoveEntry(Folder from, Entry entry, Folder into, int insertionIndex)
        {
            int oldIndex = from.Entries.IndexOf(entry);
            if (from == into && oldIndex < insertionIndex)
                insertionIndex--;
            if (from == into && oldIndex == insertionIndex)
                return;
            Edit(
                () => { from.Entries.RemoveAt(oldIndex); into.Entries.Insert(insertionIndex, entry); },
                () => { into.Entries.Remove(entry); from.Entries.Insert(oldIndex, entry); });
        }

        public string Restore(Folder folder, Entry entry)
        {
            Directory.CreateDirectory(Desktop);
            var fileName = entry.OriginalPath == null ? entry.FileName : entry.Name + ".lnk";
            var destination = FindVisibleIcon(entry) ? null : AvailableDesktopPath(fileName);
            if (destination != null)
            {
                if (entry.OriginalPath == null)
                    File.Copy(StoredFile(entry), destination, false);
                else
                    WindowsShell.CreateLink(destination, entry.OriginalPath);
            }

            int oldIndex = folder.Entries.IndexOf(entry);
            try { Edit(() => folder.Entries.RemoveAt(oldIndex), () => folder.Entries.Insert(oldIndex, entry)); }
            catch
            {
                try { if (destination != null) File.Delete(destination); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                throw;
            }
            if (entry.OriginalPath == null)
                CleanStoredFile(StoredFile(entry));
            return destination;
        }

        private bool FindVisibleIcon(Entry entry)
        {
            if (entry.OriginalPath != null)
                return IsOnDesktop(entry.OriginalPath) && File.Exists(entry.OriginalPath);
            if (entry.FolderTarget != null && IsOnDesktop(entry.FolderTarget) && Directory.Exists(entry.FolderTarget))
                return true;
            if (entry.ShortcutSource == "desktop-reference" && File.Exists(entry.OriginalShortcutPath))
                return true;
            var stored = StoredFile(entry);
            return (entry.ReturnedName != null && SameContents(stored, Path.Combine(Desktop, entry.ReturnedName))) ||
                SameContents(stored, Path.Combine(Desktop, entry.FileName)) ||
                (!String.IsNullOrEmpty(publicDesktop) && SameContents(stored, Path.Combine(publicDesktop, entry.FileName)));
        }

        private string AvailableDesktopPath(string fileName)
        {
            var destination = Path.Combine(Desktop, fileName);
            for (int index = 2; DesktopNameTaken(Path.GetFileName(destination)); index++)
                destination = Path.Combine(Desktop, Path.GetFileNameWithoutExtension(fileName) +
                    " (" + index + ")" + Path.GetExtension(fileName));
            return destination;
        }

        private bool DesktopNameTaken(string name)
        {
            var personal = Path.Combine(Desktop, name);
            if (File.Exists(personal) || Directory.Exists(personal))
                return true;
            if (String.IsNullOrEmpty(publicDesktop))
                return false;
            var common = Path.Combine(publicDesktop, name);
            return File.Exists(common) || Directory.Exists(common);
        }

        public IEnumerable<Entry> ReturnedEntries
        {
            get { return Folders.SelectMany(folder => folder.Entries).Where(entry => entry.ReturnedName != null); }
        }

        public string ReturnUntilNextStart(Entry entry)
        {
            var source = StoredFile(entry);
            if (entry.ReturnedName != null)
            {
                var previous = Path.Combine(Desktop, entry.ReturnedName);
                if (SameContents(source, previous))
                    return previous;
            }
            if (FindVisibleIcon(entry))
                return null;
            Directory.CreateDirectory(Desktop);
            var destination = AvailableDesktopPath(entry.FileName);
            var previousName = entry.ReturnedName;
            // Record ownership before the copy so the next run can recover after interruption.
            Edit(() => entry.ReturnedName = Path.GetFileName(destination), () => entry.ReturnedName = previousName);
            File.Copy(source, destination, false);
            return destination;
        }

        public void Recollect(Entry entry, Point? currentPosition)
        {
            var restored = Path.Combine(Desktop, entry.ReturnedName);
            var name = entry.ReturnedName;
            var position = entry.DesktopPosition;
            if (SameContents(StoredFile(entry), restored))
                File.Delete(restored);
            else
                currentPosition = null;
            Edit(() =>
            {
                entry.ReturnedName = null;
                // A temporary Explorer slot must never replace a saved origin.
                if (!position.HasValue && currentPosition.HasValue)
                    entry.DesktopPosition = currentPosition;
            }, () => { entry.ReturnedName = name; entry.DesktopPosition = position; });
        }

        private static bool SameContents(string first, string second)
        {
            try
            {
                return File.Exists(first) && File.Exists(second) && new FileInfo(first).Length == new FileInfo(second).Length &&
                    File.ReadAllBytes(first).SequenceEqual(File.ReadAllBytes(second));
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static void CleanStoredFile(string path)
        {
            var directory = Path.GetDirectoryName(path);
            try
            {
                File.Delete(path);
                Directory.Delete(directory);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal sealed class FolderImport
    {
        public readonly string Source;
        public readonly string Target;
        public bool MovesOriginal { get { return !String.Equals(Source, Target, StringComparison.OrdinalIgnoreCase); } }

        public FolderImport(string source, string target)
        {
            Source = source;
            Target = target;
        }
    }
}
