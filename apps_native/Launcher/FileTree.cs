// What the tree is made of, and where its children come from.
//
// One node per directory entry, read through the service table on demand —
// TreeView asks for children only when a folder is opened, so a volume is never
// walked in full just to draw its first level.
//
// Two different listings, on purpose. The folder you are standing in is listed
// in FULL — it is what you came to read. A folder peeked at inside the tree is
// cut to a few entries with a row saying how many were left out, because a peek
// is a hint: let a fifty-entry folder answer in full and the tree becomes a flat
// wall with everything else pushed off the screen.
//
// That cut-off row is a node like any other, so it can be selected and explained
// rather than being a piece of drawing nobody can point at.

using SharpOS.AppSdk;
using System;
using System.Collections.Generic;

namespace Launcher
{
    internal enum NodeKind
    {
        Directory,
        Application,

        /// <summary>An assembly for the kernel's hosted runtime, not an image.</summary>
        ManagedApplication,

        File,

        /// <summary>The tail of a folder that was cut short.</summary>
        More,

        /// <summary>The way back to the parent folder.</summary>
        Up,
    }

    internal sealed class FileNode
    {
        public string Name = "";
        public string Path = "";
        public NodeKind Kind;

        /// <summary>How many entries were left out; only set on a More node.</summary>
        public int Hidden;

        public bool IsDirectory => Kind == NodeKind.Directory;

        /// <summary>How the tree labels this node.</summary>
        public override string ToString()
        {
            switch (Kind)
            {
                case NodeKind.Directory: return Name + "\\";
                case NodeKind.Application: return Name + "  *";
                case NodeKind.ManagedApplication: return Name + "  #";
                case NodeKind.More: return "... " + Hidden.ToString() + " more";
                case NodeKind.Up: return "..  (up)";
                default: return Name;
            }
        }
    }

    internal static unsafe class FileTree
    {
        /// <summary>
        /// How many entries a PREVIEW shows before summarising the rest.
        /// </summary>
        /// <remarks>
        /// Applies to a folder peeked at inside the tree, not to the folder you
        /// are standing in — that one is listed in full. The two want opposite
        /// things: where you are is what you came to read, while a peek is a
        /// hint, and a fifty-entry hint pushes everything else off the screen
        /// and makes the tree flat.
        /// </remarks>
        public const int PreviewChildren = 5;

        private const int MaxNameBytes = 256;
        private const uint MaxEntriesScanned = 512;

        /// <summary>Children of a node, for TreeView to ask on expansion.</summary>
        public static IEnumerable<FileNode> ChildrenOf(FileNode node)
        {
            // A file has no children, and neither does the summary row — it
            // stands for entries rather than containing them.
            if (node == null || !node.IsDirectory) return new List<FileNode>();

            // A preview: cut short, with a row saying how much was left out.
            return Read(node.Path, PreviewChildren);
        }

        /// <summary>The folder containing <paramref name="directory"/>, or null at the root.</summary>
        public static string? ParentOf(string directory)
        {
            if (directory == null || directory.Length <= 1) return null;

            int cut = directory.LastIndexOf('\\');
            if (cut < 0) return null;
            if (cut == 0) return "\\";      // one level below the volume root

            return directory.Substring(0, cut);
        }

        /// <summary>
        /// Entries of a folder. <paramref name="maxShown"/> of 0 or less lists
        /// everything; a positive value cuts the list short and adds a row
        /// naming the remainder.
        /// </summary>
        public static List<FileNode> Read(string directory, int maxShown = 0)
        {
            var all = new List<FileNode>();
            byte* nameBuffer = stackalloc byte[MaxNameBytes];

            for (uint index = 0; index < MaxEntriesScanned; index++)
            {
                AppServiceStatus status = AppHost.TryReadDirEntry(
                    directory, index, nameBuffer, MaxNameBytes, out FileEntry entry);

                if (status == AppServiceStatus.EndOfDirectory) break;
                if (status != AppServiceStatus.Ok) continue;

                string name = string.FromAscii(nameBuffer, entry.NameLength);
                if (name.Length == 0) continue;

                // "." and ".." are the directory naming itself and its parent;
                // the tree already knows both.
                if (name == "." || name == "..") continue;

                bool isDirectory = entry.IsDirectory != 0;

                all.Add(new FileNode
                {
                    Name = name,
                    Path = Combine(directory, name),
                    Kind = isDirectory ? NodeKind.Directory
                         : IsApplication(name) ? NodeKind.Application
                         : IsManagedApplication(name) ? NodeKind.ManagedApplication
                         : NodeKind.File,
                });
            }

            Sort(all);

            if (maxShown <= 0 || all.Count <= maxShown) return all;

            var shown = new List<FileNode>();
            for (int i = 0; i < maxShown; i++) shown.Add(all[i]);

            shown.Add(new FileNode
            {
                Kind = NodeKind.More,
                Hidden = all.Count - maxShown,
                Path = directory,
                Name = "",
            });

            return shown;
        }

        /// <summary>
        /// Folders first, then applications, then everything else; by name
        /// within each group.
        /// </summary>
        /// <remarks>
        /// The order answers the two questions a launcher is opened with, in
        /// the order they are asked: where can I go, and what can I run. A
        /// directory in the order the file system happens to store it answers
        /// neither, and on FAT that order is arbitrary.
        ///
        /// Sorted on an array because that is where Array.Sort is; the list is
        /// rebuilt from it rather than sorted in place.
        /// </remarks>
        private static void Sort(List<FileNode> entries)
        {
            if (entries.Count < 2) return;

            FileNode[] items = entries.ToArray();
            System.Array.Sort(items, Compare);

            entries.Clear();
            for (int i = 0; i < items.Length; i++) entries.Add(items[i]);
        }

        private static int Compare(FileNode a, FileNode b)
        {
            int byKind = Rank(a.Kind) - Rank(b.Kind);
            if (byKind != 0) return byKind;

            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }

        private static int Rank(NodeKind kind)
        {
            switch (kind)
            {
                case NodeKind.Up: return 0;
                case NodeKind.Directory: return 1;
                case NodeKind.Application: return 2;
                case NodeKind.ManagedApplication: return 3;
                case NodeKind.More: return 5;   // always last: it stands for the tail
                default: return 4;              // plain files
            }
        }

        public static string Combine(string directory, string name)
        {
            if (directory.Length == 0) return name;
            if (directory[directory.Length - 1] == '\\') return directory + name;
            return directory + "\\" + name;
        }

        /// <summary>
        /// Something the launcher can start. Decided by extension, which is all
        /// the directory tells us — the kernel makes the real decision when it
        /// looks for the MZ magic at launch.
        /// </summary>
        public static bool IsApplication(string name)
        {
            if (name.Length < 4) return false;

            string tail = name.Substring(name.Length - 4);
            return tail == ".EXE" || tail == ".exe";
        }

        /// <summary>
        /// An assembly the hosted runtime can run. Told apart by extension for
        /// the same reason as above — the directory says nothing else, and the
        /// kernel decides for real when it hands the file to the runtime.
        /// </summary>
        public static bool IsManagedApplication(string name)
        {
            if (name.Length < 4) return false;

            string tail = name.Substring(name.Length - 4);
            return tail == ".DLL" || tail == ".dll";
        }
    }
}
