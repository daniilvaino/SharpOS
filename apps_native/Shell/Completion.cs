using System;
using System.Collections.Generic;
using System.Text;
using SharpOS.AppSdk;

namespace Shell
{
    /// <summary>
    /// Tab: the word under the cursor completed. In a command's place (the
    /// start of the line, after | ; &amp;) — builtins and the programs along
    /// PATH, without .EXE (step207); anywhere else — the names in the directory the
    /// word points into, a directory with a / after it; <c>$NA</c>, and the
    /// words after unset and export, — the environment's names (step208). Repeated Tab cycles
    /// through the candidates (the line editor's own behaviour).
    /// </summary>
    internal sealed unsafe class Completion : IAutoCompleteHandler
    {
        private readonly Executor _executor;

        public Completion(Executor executor) => _executor = executor;

        public char[] Separators { get; set; } = { ' ', '|', ';', '&', '>', '<' };

        public string[] GetSuggestions(string text, int index)
        {
            if (KqlQueryAt(text, out int queryStart)) return KqlSuggestions(text, index, queryStart);

            string word = text.Substring(index);
            string before = text.Substring(0, index).TrimEnd();
            bool command = before.Length == 0 || "|;&".IndexOf(before[before.Length - 1]) >= 0;

            var found = new List<string>();
            int dollar = word.LastIndexOf('$');
            bool braced = dollar >= 0 && dollar + 1 < word.Length && word[dollar + 1] == '{';
            string head = dollar >= 0 ? word.Substring(0, dollar + (braced ? 2 : 1)) : "";
            string prefix = word.Substring(head.Length);
            if (dollar >= 0 && (prefix.Length == 0 || Executor.IsName(prefix)))
            {
                // $NA, ${NA, "$NA: the environment's names (step208).
                foreach (string name in VariableNames())
                    if (StartsWith(name, prefix)) found.Add(head + name + (braced ? "}" : ""));
            }
            else if (!command && (FirstWord(before) == "unset" || FirstWord(before) == "export") && word.IndexOf('=') < 0)
            {
                foreach (string name in VariableNames())
                    if (StartsWith(name, word)) found.Add(name);
            }
            else if (command && word.IndexOf('/') < 0)
            {
                foreach (string builtin in Executor.Builtins)
                    if (StartsWith(builtin, word)) found.Add(builtin);
                string path = System.Environment.GetEnvironmentVariable("PATH") ?? "";
                foreach (string directory in path.Split(';'))
                {
                    if (directory.Length == 0) continue;
                    foreach (Entry e in Entries(directory.Replace('/', '\\')))
                    {
                        if (e.IsDirectory || !EndsWith(e.Name, ".EXE")) continue;
                        string program = e.Name.Substring(0, e.Name.Length - 4);
                        if (StartsWith(program, word) && !found.Contains(program)) found.Add(program);
                    }
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

        // Whether the end of the line is inside a quoted argument of KQL
        // (`PIPEGEN | KQL 'where Le`); where the query in it starts. The
        // quotes are followed as the line is, so a | inside them is the
        // query's, not a pipe.
        private static bool KqlQueryAt(string text, out int queryStart)
        {
            queryStart = -1;
            int commandStart = 0;
            char quote = '\0';
            int quoteStart = -1;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (quote != '\0')
                {
                    if (c == quote) quote = '\0';
                    else if (c == '\\' && quote == '"') i++;
                    continue;
                }
                if (c == '\\') { i++; continue; }
                if (c == '\'' || c == '"') { quote = c; quoteStart = i; continue; }
                if (c == '|' || c == ';' || c == '&') commandStart = i + 1;
            }
            if (quote == '\0') return false;

            string command = FirstWord(text.Substring(commandStart, quoteStart - commandStart).TrimStart());
            bool kql = (command.Length == 3 || command.Length == 7) && StartsWith("KQL.EXE", command);
            if (!kql) return false;
            queryStart = quoteStart + 1;
            return true;
        }

        // KQL's own completions for the query (KQL --complete, step208):
        // operators, functions, keywords — no columns, since the line has not
        // run and the input's type is not known. An interactive `… | KQL`
        // completes with them.
        private static string[] KqlSuggestions(string text, int index, int queryStart)
        {
            var lines = new List<string>();
            try
            {
                SharpOS.Std.Pipes.PipePair pair = SharpOS.Std.Pipes.Pipe.Create();
                Process kql;
                try
                {
                    kql = Process.Start("KQL", new[] { "--complete", text.Substring(queryStart) }, null, pair.WriteEnd);
                }
                finally
                {
                    pair.WriteEnd.Dispose();
                }
                using (SharpOS.Std.Pipes.RawPipeReader reader = pair.ReadEnd.Read())
                    foreach (var message in reader) lines.Add((string)message);
                kql.WaitForExit();
                kql.Dispose();
            }
            catch (InvalidOperationException) { return null; }   // no KQL along PATH
            catch (SharpOS.Std.Pipes.PipeException) { return null; }

            if (lines.Count == 0 || !int.TryParse(lines[0], out int start)) return null;
            int at = queryStart + start;
            if (at < index || at > text.Length) return null;
            string head = text.Substring(index, at - index);
            var result = new string[lines.Count - 1];
            for (int i = 1; i < lines.Count; i++) result[i - 1] = head + lines[i];
            return result;
        }

        private static List<string> VariableNames()
        {
            var names = new List<string>();
            foreach (string entry in SharpOS.Std.NoRuntime.EnvironmentStore.GetAll())
            {
                int eq = entry.IndexOf('=');
                if (eq > 0) names.Add(entry.Substring(0, eq));
            }
            return names;
        }

        // The command word of the last command in the text before the cursor.
        private static string FirstWord(string before)
        {
            int start = before.Length;
            while (start > 0 && "|;&".IndexOf(before[start - 1]) < 0) start--;
            string command = before.Substring(start).TrimStart();
            int space = command.IndexOf(' ');
            return space < 0 ? command : command.Substring(0, space);
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
