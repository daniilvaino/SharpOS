using System;
using System.Collections.Generic;
using System.Text;
using SharpOS.AppSdk;

namespace Shell
{
    /// <summary>
    /// Tab: the word under the cursor completed. In a command's place (the
    /// start of the line, after | ; &amp;) — builtins and the programs in
    /// \apps, without .EXE; anywhere else — the names in the directory the
    /// word points into, a directory with a / after it. Repeated Tab cycles
    /// through the candidates (the line editor's own behaviour).
    /// </summary>
    internal sealed unsafe class Completion : IAutoCompleteHandler
    {
        private readonly Executor _executor;

        public Completion(Executor executor) => _executor = executor;

        public char[] Separators { get; set; } = { ' ', '|', ';', '&', '>', '<' };

        public string[] GetSuggestions(string text, int index)
        {
            string word = text.Substring(index);
            string before = text.Substring(0, index).TrimEnd();
            bool command = before.Length == 0 || "|;&".IndexOf(before[before.Length - 1]) >= 0;

            var found = new List<string>();
            if (command && word.IndexOf('/') < 0)
            {
                foreach (string builtin in Executor.Builtins)
                    if (StartsWith(builtin, word)) found.Add(builtin);
                foreach (Entry e in Entries("\\apps"))
                {
                    if (e.IsDirectory || !EndsWith(e.Name, ".EXE")) continue;
                    string program = e.Name.Substring(0, e.Name.Length - 4);
                    if (StartsWith(program, word)) found.Add(program);
                }
            }
            else
            {
                int slash = word.LastIndexOf('/');
                string directoryPart = slash >= 0 ? word.Substring(0, slash + 1) : "";
                string namePart = word.Substring(slash + 1);
                string directory = directoryPart.Length == 0 ? _executor.WorkingDirectory
                                 : directoryPart == "/" ? "\\"
                                 : _executor.Resolve(directoryPart.Substring(0, directoryPart.Length - 1));
                foreach (Entry e in Entries(directory))
                    if (StartsWith(e.Name, namePart))
                        found.Add(directoryPart + e.Name + (e.IsDirectory ? "/" : ""));
            }

            // Ordinal order, so the cycle is the same every time.
            string[] sorted = found.ToArray();
            for (int i = 1; i < sorted.Length; i++)
            {
                string s = sorted[i];
                int j = i - 1;
                while (j >= 0 && string.CompareOrdinal(sorted[j], s) > 0) { sorted[j + 1] = sorted[j]; j--; }
                sorted[j + 1] = s;
            }
            return sorted;
        }

        private struct Entry
        {
            public string Name;
            public bool IsDirectory;
        }

        private static List<Entry> Entries(string directory)
        {
            var entries = new List<Entry>();
            byte* name = stackalloc byte[256];
            for (uint index = 0; ; index++)
            {
                if (AppHost.TryReadDirEntry(directory, index, name, 256, out FileEntry entry) != AppServiceStatus.Ok) break;
                int length = (int)(entry.NameLength < 256 ? entry.NameLength : 256);
                string text = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(name, length));
                if (text == "." || text == "..") continue;
                entries.Add(new Entry { Name = text, IsDirectory = entry.IsDirectory != 0 });
            }
            return entries;
        }

        // FAT names are not case-sensitive, and neither is matching them.
        private static bool StartsWith(string value, string prefix)
        {
            if (value.Length < prefix.Length) return false;
            for (int i = 0; i < prefix.Length; i++)
                if (Lower(value[i]) != Lower(prefix[i])) return false;
            return true;
        }

        private static bool EndsWith(string value, string suffix)
            => value.Length >= suffix.Length && StartsWith(value.Substring(value.Length - suffix.Length), suffix);

        private static char Lower(char c) => c >= 'A' && c <= 'Z' ? (char)(c + 32) : c;
    }
}
