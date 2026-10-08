using System;
using System.Collections.Generic;
using System.Text;
using Internal.ReadLine;
using Internal.ReadLine.Abstractions;
using SharpOS.AppSdk;

namespace Shell
{
    /// <summary>
    /// The prompt's line: edited by vendor/ReadLine (cursor keys, Home/End,
    /// Delete, the Emacs Ctrl keys, history on Up/Down, Tab completion), with
    /// the history kept in a file between sessions.
    /// </summary>
    /// <remarks>
    /// The loop is the library's ReadLine.GetText with the keys a shell needs
    /// on top: Ctrl+C drops the line, Ctrl+D on an empty line ends the input,
    /// Ctrl+L clears the screen and draws the line again. A Ctrl key the
    /// editor has no action for is ignored rather than typed in as a control
    /// character.
    /// </remarks>
    internal sealed class LineReader
    {
        private const string HistoryPath = "\\sh_history";
        private const int HistoryKept = 500;

        private readonly IAutoCompleteHandler _completion;

        public List<string> History { get; } = new List<string>();

        public LineReader(IAutoCompleteHandler completion)
        {
            _completion = completion;
            LoadHistory();
        }

        /// <summary>One line; null at the end of the input (Ctrl+D on an empty line).</summary>
        public string Read(string prompt, int promptWidth)
        {
            KeyHandler editor = Begin(prompt, promptWidth);
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter)
                {
                    AppHost.WriteString("\n");
                    string line = editor.Text;
                    Remember(line);
                    return line;
                }

                if (key.Modifiers == ConsoleModifiers.Control)
                {
                    if (key.Key == ConsoleKey.C)
                    {
                        AppHost.WriteString("^C\n");
                        return "";
                    }
                    if (key.Key == ConsoleKey.D && editor.Text.Length == 0)
                    {
                        AppHost.WriteString("\n");
                        return null;
                    }
                    if (key.Key == ConsoleKey.L)
                    {
                        string text = editor.Text;
                        Console.Clear();
                        editor = Begin(prompt, promptWidth);
                        foreach (char c in text)
                            editor.Handle(new ConsoleKeyInfo(c, ConsoleKey.None, false, false, false));
                        continue;
                    }
                    if (!EditorHasAction(key.Key)) continue;
                }

                editor.Handle(key);
            }
        }

        private KeyHandler Begin(string prompt, int promptWidth)
        {
            Console2.Origin = promptWidth;
            AppHost.WriteString(prompt);
            return new KeyHandler(new Console2(), History, _completion);
        }

        // The Ctrl keys KeyHandler binds (A B D E F H K N P T U W; L is ours).
        private static bool EditorHasAction(ConsoleKey key)
            => key == ConsoleKey.A || key == ConsoleKey.B || key == ConsoleKey.D || key == ConsoleKey.E
               || key == ConsoleKey.F || key == ConsoleKey.H || key == ConsoleKey.K || key == ConsoleKey.N
               || key == ConsoleKey.P || key == ConsoleKey.T || key == ConsoleKey.U || key == ConsoleKey.W;

        // A line goes into the history unless it is blank or the same as the
        // one before it; the file gets it at once, so a session that ends by
        // power-off keeps what it typed.
        private void Remember(string line)
        {
            if (line.Trim().Length == 0) return;
            if (History.Count > 0 && History[History.Count - 1] == line) return;
            History.Add(line);
            try { System.IO.File.AppendAllText(HistoryPath, line + "\n"); }
            catch (System.IO.IOException) { }   // another shell writing it, or a full volume: memory only
        }

        // The last HistoryKept lines; a file grown to twice that is cut back.
        private void LoadHistory()
        {
            string[] lines;
            try
            {
                if (!System.IO.File.Exists(HistoryPath)) return;
                lines = Encoding.UTF8.GetString(System.IO.File.ReadAllBytes(HistoryPath)).Split('\n');
            }
            catch (System.IO.IOException) { return; }

            int count = 0;
            foreach (string l in lines) if (l.Length > 0) count++;
            int skip = count > HistoryKept ? count - HistoryKept : 0;
            foreach (string l in lines)
            {
                if (l.Length == 0) continue;
                if (skip > 0) { skip--; continue; }
                History.Add(l);
            }

            if (count < 2 * HistoryKept) return;
            var kept = new StringBuilder();
            foreach (string l in History) kept.Append(l).Append('\n');
            try { System.IO.File.WriteAllText(HistoryPath, kept.ToString()); }
            catch (System.IO.IOException) { }
        }
    }
}
