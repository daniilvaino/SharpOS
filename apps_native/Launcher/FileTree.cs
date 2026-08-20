// What the tree is made of, and where its children come from.
//
// One node per directory entry, read through the service table on demand —
// TreeView asks for children only when a folder is opened, so a volume is never
// walked in full just to draw its first level.
//
// The "five children then a summary" rule lives here rather than in the view:
// it is a property of what we choose to show, and TreeView has no opinion about
// it. The cut-off row is a node like any other, which is why it can be selected
// and explained instead of being a piece of drawing nobody can point at.

using SharpOS.AppSdk;
using System.Collections.Generic;

namespace Launcher
{
    internal enum NodeKind
    {
        Directory,
        Application,
        File,

        /// <summary>The tail of a folder that was cut short.</summary>
        More,
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
                case NodeKind.More: return "... " + Hidden.ToString() + " more";
                default: return Name;
            }
        }
    }

    internal static unsafe class FileTree
    {
        /// <summary>
        /// How many entries of one folder the tree shows before summarising the
        /// rest. A boot directory with fifty files would otherwise bury
        /// everything else the moment it opened.
        /// </summary>
        public const int MaxChildrenShown = 5;

        private const int MaxNameBytes = 256;
        private const uint MaxEntriesScanned = 512;

        /// <summary>Children of a node, for TreeView to ask on expansion.</summary>
        public static IEnumerable<FileNode> ChildrenOf(FileNode node)
        {
            // A file has no children, and neither does the summary row — it
            // stands for entries rather than containing them.
            if (node == null || !node.IsDirectory) return new List<FileNode>();

            return Read(node.Path);
        }

        public static List<FileNode> Read(string directory)
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
                         : NodeKind.File,
                });
            }

            if (all.Count <= MaxChildrenShown) return all;

            var shown = new List<FileNode>();
            for (int i = 0; i < MaxChildrenShown; i++) shown.Add(all[i]);

            shown.Add(new FileNode
            {
                Kind = NodeKind.More,
                Hidden = all.Count - MaxChildrenShown,
                Path = directory,
                Name = "",
            });

            return shown;
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
    }
}
