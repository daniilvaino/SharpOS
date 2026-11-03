using System.Collections.Generic;
using System.Text;
using SharpOS.AppSdk;
using SharpOS.Std.Pipes;

namespace Shell
{
    // The side panel at the prompt (step219, a person's tool since step237):
    // what the last commands showed — objects, strings, bytes — kept after
    // the messages are gone, to look through. Objects open the panel, as
    // tmux splits a window; text and bytes wait in it. A run of objects of
    // one type is a table, one object a tree; a node opens and closes. The
    // panel is a pane of its own: the kernel gives it the keys (Ctrl+Tab),
    // F12 shows and hides it, and a thread here reads its keys and draws it.
    internal sealed class Inspector : IScreenInspector
    {
        // ---- settings: the environment, read where used, so `export` takes at once ----
        //
        // INSPECTOR          auto: objects open the panel; on: open from the start;
        //                    off: F12 and `inspect` only
        // INSPECTOR_WIDTH    the panel's width, percent of the screen (10-80)
        // INSPECTOR_KEEP     commands kept (1-256)
        // INSPECTOR_DEPTH    levels a lone object opens first; auto: as many as fit
        // INSPECTOR_SUMMARY  on: a run of objects is its first line and a summary line

        private static string Setting(string name, string fallback)
        {
            string value = System.Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(value) ? fallback : value.Trim().ToLowerInvariant();
        }

        private static int Number(string name, int fallback, int least, int most)
        {
            string value = Setting(name, "");
            int n = 0;
            if (value.Length == 0 || value.Length > 6) return fallback;
            foreach (char c in value)
            {
                if (c < '0' || c > '9') return fallback;
                n = n * 10 + (c - '0');
            }
            return n < least ? least : n > most ? most : n;
        }

        private static string Mode => Setting("INSPECTOR", "auto");

        /// <summary>Commands kept, the newest last.</summary>
        internal static int Kept => Number("INSPECTOR_KEEP", 16, 1, 256);

        private static int WidthPercent => Number("INSPECTOR_WIDTH", 40, 10, 80);

        // 0: as many levels as the panel holds.
        private static int Depth => Setting("INSPECTOR_DEPTH", "auto") == "auto" ? 0 : Number("INSPECTOR_DEPTH", 0, 1, InspectNode.Depth);

        public bool Summary => Setting("INSPECTOR_SUMMARY", "on") != "off";

        // Nodes kept per command, characters of text, bytes.
        private const int NodeBudget = 100_000, TextChars = 512 * 1024, ByteCount = 256 * 1024;
        private const int CellWidth = 24, MaxColumns = 16;

        private sealed class Result
        {
            public int Number;
            public string Line;
            public readonly List<Item> Items = new List<Item>();
            public int Budget = NodeBudget;
            public int Cursor, Top;
        }

        // A run of objects of one type, or text: strings and bytes.
        private sealed class Item
        {
            public string Type;
            public List<InspectNode> Nodes;
            public int Count;                 // objects shown; Nodes holds the ones kept
            public bool Tree;                 // a run shown as trees rather than a table
            public bool Opened;               // a lone object was opened once, as it first shows
            public StringBuilder Text;
            public byte[] Bytes;
            public int ByteLength, BytesLeft;
            public int TextLeft;
        }

        // A line of the panel: what it shows, and what it stands for.
        private struct Row
        {
            public string Text;
            public InspectNode Node;
            public Item Item;
            public int Parent;                // the row of the node above, -1 at a root
            public bool Selectable;
        }

        private readonly object _lock = new object();
        private readonly List<Result> _results = new List<Result>();
        private int _numbers;
        private string _pending;              // the command started, before it showed anything
        private bool _pendingTaken;
        private Result _current;              // the command the panel shows
        private bool _dirty = true;
        private bool _opened;                 // objects opened the panel for this command
        private int _perRow = 16;             // bytes on a hex row: 8 when sixteen do not fit
        private int _fit = 23;                // rows under the header: how deep a lone object first opens

        private Inspector() { }

        /// <summary>An inspector, or null when the kernel has no panels.</summary>
        internal static unsafe Inspector Create()
        {
            if (AppHost.Terminal.SidePanelColumns < 0) return null;
            var inspector = new Inspector();
            // The keys' reader from now on: Ctrl+Tab and F12 reach a panel here.
            byte* none = stackalloc byte[1];
            AppHost.Terminal.ReadSideKeys(none, 0);
            System.Threading.Tasks.Task.Run(inspector.Pump);
            if (Mode == "on") Open();
            return inspector;
        }

        /// <summary>Takes the screen back from the panel.</summary>
        internal void Hide() => AppHost.Terminal.SidePanel(0);

        // ---- what the screen shows ----

        /// <summary>A command starts; its first message makes it a result.</summary>
        internal void Begin(string line)
        {
            lock (_lock)
            {
                _pending = line;
                _pendingTaken = false;
                _opened = false;
            }
            if (Mode == "on") Open();
        }

        public string Mark
        {
            get
            {
                lock (_lock) return "#" + Pending().Number.ToString();
            }
        }

        // The result of the command running now, made on its first message.
        private Result Pending()
        {
            if (!_pendingTaken || _results.Count == 0)
            {
                _pendingTaken = true;
                var result = new Result { Number = ++_numbers, Line = _pending ?? "" };
                _results.Add(result);
                int kept = Kept;
                while (_results.Count > kept) _results.RemoveAt(0);
                _current = result;
            }
            return _results[_results.Count - 1];
        }

        public unsafe void Take(View root, bool terminalText)
        {
            lock (_lock)
            {
                Result result = Pending();
                _dirty = true;
                if (root.TryGetChars(out char* chars, out int count))
                {
                    AddText(result, new string(new System.ReadOnlySpan<char>(chars, count)), !terminalText);
                    return;
                }
                if (root.TryGetBytes(out byte* bytes, out int length))
                {
                    AddBytes(result, bytes, length);
                    return;
                }
                ViewKind kind = root.Kind;
                if (kind != ViewKind.Object && kind != ViewKind.Array)
                {
                    AddText(result, InspectNode.Plain(root.ToScreenHead()), true);
                    return;
                }
                string type = root.TypeName;
                Item last = result.Items.Count > 0 ? result.Items[result.Items.Count - 1] : null;
                if (last == null || last.Nodes == null || last.Type != type)
                {
                    last = new Item { Type = type, Nodes = new List<InspectNode>(), Tree = kind == ViewKind.Array };
                    result.Items.Add(last);
                }
                last.Count++;
                if (result.Budget > 0)
                {
                    InspectNode node = InspectNode.Of(root, ref result.Budget);
                    last.Nodes.Add(node);
                }
                if (!_opened)
                {
                    _opened = true;
                    if (Mode == "auto") Open();
                }
            }
        }

        /// <summary>A builtin's text, written to the screen.</summary>
        internal void TakeText(string text)
        {
            lock (_lock)
            {
                _dirty = true;
                AddText(Pending(), text, false);
            }
        }

        private static void AddText(Result result, string text, bool line)
        {
            Item last = result.Items.Count > 0 ? result.Items[result.Items.Count - 1] : null;
            if (last == null || last.Text == null)
            {
                last = new Item { Text = new StringBuilder() };
                result.Items.Add(last);
            }
            if (last.Text.Length + text.Length > TextChars)
            {
                last.TextLeft += text.Length;
                return;
            }
            last.Text.Append(text);
            if (line) last.Text.Append('\n');
        }

        private static unsafe void AddBytes(Result result, byte* bytes, int length)
        {
            Item last = result.Items.Count > 0 ? result.Items[result.Items.Count - 1] : null;
            if (last == null || last.Bytes == null)
            {
                last = new Item { Bytes = new byte[4096] };
                result.Items.Add(last);
            }
            int take = length;
            if (last.ByteLength + take > ByteCount) take = ByteCount - last.ByteLength;
            last.BytesLeft += length - take;
            if (take <= 0) return;
            if (last.ByteLength + take > last.Bytes.Length)
            {
                int size = last.Bytes.Length;
                while (size < last.ByteLength + take) size *= 2;
                var grown = new byte[size];
                System.Array.Copy(last.Bytes, grown, last.ByteLength);
                last.Bytes = grown;
            }
            new System.ReadOnlySpan<byte>(bytes, take).CopyTo(new System.Span<byte>(last.Bytes, last.ByteLength, take));
            last.ByteLength += take;
        }

        // ---- the panel ----

        // INSPECTOR_WIDTH of the screen, in columns; 0 when the screen has no width.
        // The console's width is the main panel's: with the side one shown, the
        // screen is both and the rule between them.
        private static int Wanted
        {
            get
            {
                int screen = System.Console.WindowWidth, side = AppHost.Terminal.SidePanelColumns;
                if (side > 0) screen += side + 1;
                return screen * WidthPercent / 100;
            }
        }

        // The width last asked for, in percent: a changed INSPECTOR_WIDTH resizes the panel.
        private static int s_applied;

        // At the right; a panel the person hid stays hidden.
        private static void Open()
        {
            if (AppHost.Terminal.SidePanelColumns > 0 || AppHost.Terminal.SideHidden) return;
            Ask();
        }

        private static void Ask()
        {
            int columns = Wanted;
            if (columns <= 0) return;
            s_applied = WidthPercent;
            AppHost.Terminal.SidePanel(columns);
        }

        /// <summary>Shows the panel with the keys: command <paramref name="number"/>, or the one it shows (0).</summary>
        internal bool Show(int number)
        {
            lock (_lock)
            {
                if (number > 0)
                {
                    Result found = null;
                    foreach (Result r in _results)
                        if (r.Number == number) found = r;
                    if (found == null) return false;
                    _current = found;
                }
                _dirty = true;
            }
            if (AppHost.Terminal.SidePanelColumns <= 0) Ask();
            AppHost.Terminal.ShowSide(keys: true);
            return true;
        }

        /// <summary>Hides the panel, as F12 does.</summary>
        internal void Close() => AppHost.Terminal.HideSide();

        // The keys' reader and the painter: polls the panel's keys and paints
        // what changed, a frame at most every interval.
        private unsafe void Pump()
        {
            byte* keys = stackalloc byte[64];
            bool hadKeys = false;
            int width = 0;
            while (true)
            {
                int n = AppHost.Terminal.ReadSideKeys(keys, 64);
                if (n < 0) return;
                bool focus = AppHost.Terminal.SideHasKeys;
                int columns = AppHost.Terminal.SidePanelColumns;
                if (columns > 0 && WidthPercent != s_applied)
                {
                    Ask();
                    columns = AppHost.Terminal.SidePanelColumns;
                }
                lock (_lock)
                {
                    if (n > 0) HandleKeys(keys, n);
                    if (focus != hadKeys || (columns > 0 && columns != width)) _dirty = true;
                    hadKeys = focus;
                    if (columns > 0) width = columns;
                    if (_dirty)
                    {
                        _dirty = false;
                        Paint(width > 0 ? width : Wanted, focus);
                    }
                }
                AppThreads.Sleep(n > 0 ? 5u : 30u);
            }
        }

        private int Height
        {
            get
            {
                int rows = System.Console.WindowHeight;
                return rows > 2 ? rows : 25;
            }
        }

        // ---- keys ----

        private unsafe void HandleKeys(byte* keys, int n)
        {
            for (int i = 0; i < n; i++)
            {
                byte b = keys[i];
                if (b == 0x1B && i + 2 < n && keys[i + 1] == (byte)'[')
                {
                    byte final = keys[i + 2];
                    if (final >= (byte)'0' && final <= (byte)'9' && i + 3 < n && keys[i + 3] == (byte)'~')
                    {
                        Key(final == (byte)'5' ? "PgUp" : final == (byte)'6' ? "PgDn" : "");
                        i += 3;
                        continue;
                    }
                    Key(final == (byte)'A' ? "Up" : final == (byte)'B' ? "Down" : final == (byte)'C' ? "Right"
                        : final == (byte)'D' ? "Left" : final == (byte)'H' ? "Home" : final == (byte)'F' ? "End" : "");
                    i += 2;
                    continue;
                }
                switch ((char)b)
                {
                    case 'k': Key("Up"); break;
                    case 'j': Key("Down"); break;
                    case 'l': Key("Right"); break;
                    case 'h': Key("Left"); break;
                    case '\r': case ' ': Key("Toggle"); break;
                    case 't': Key("Table"); break;
                    case 'p': Key("Before"); break;
                    case 'n': Key("After"); break;
                    case 'g': Key("Home"); break;
                    case 'G': Key("End"); break;
                    case 'q': AppHost.Terminal.HideSide(); break;
                }
            }
        }

        private void Key(string key)
        {
            if (key.Length == 0) return;
            Result result = _current;
            if (key == "Before" || key == "After")
            {
                int at = _results.IndexOf(result);
                int to = key == "Before" ? at - 1 : at + 1;
                if (at >= 0 && to >= 0 && to < _results.Count) _current = _results[to];
                _dirty = true;
                return;
            }
            if (result == null) return;
            List<Row> rows = Rows(result, _perRow, _fit, out _);
            int page = Height - 1;
            int cursor = Clamp(result.Cursor, rows);
            switch (key)
            {
                case "Up": cursor = Step(rows, cursor, -1); break;
                case "Down": cursor = Step(rows, cursor, 1); break;
                case "PgUp": cursor = Step(rows, cursor, -page); break;
                case "PgDn": cursor = Step(rows, cursor, page); break;
                case "Home": cursor = Step(rows, -1, 1); break;
                case "End": cursor = Step(rows, rows.Count, -1); break;
                case "Right":
                    if (cursor >= 0 && rows[cursor].Node is InspectNode open && open.Children != null)
                    {
                        if (!open.Open) open.Open = true;
                        else cursor = Step(rows, cursor, 1);
                    }
                    break;
                case "Left":
                    if (cursor >= 0 && rows[cursor].Node is InspectNode shut && shut.Children != null && shut.Open)
                        shut.Open = false;
                    else if (cursor >= 0 && rows[cursor].Parent >= 0)
                        cursor = rows[cursor].Parent;
                    break;
                case "Toggle":
                    if (cursor >= 0 && rows[cursor].Node is InspectNode node && node.Children != null)
                        node.Open = !node.Open;
                    break;
                case "Table":
                    if (cursor >= 0 && rows[cursor].Item is Item item && item.Nodes != null && item.Nodes.Count > 1)
                    {
                        item.Tree = !item.Tree;
                        // The cursor stays on the object it was on.
                        InspectNode on = rows[cursor].Node;
                        List<Row> after = Rows(result, _perRow, _fit, out _);
                        cursor = Step(after, -1, 1);
                        for (int r = 0; r < after.Count; r++)
                            if (after[r].Node == on && on != null) { cursor = r; break; }
                        result.Cursor = cursor;
                        _dirty = true;
                        return;
                    }
                    break;
            }
            result.Cursor = cursor;
            _dirty = true;
        }

        // The selectable row `delta` rows from `from`, or the last one in that direction.
        private static int Step(List<Row> rows, int from, int delta)
        {
            int direction = delta < 0 ? -1 : 1;
            int steps = delta < 0 ? -delta : delta;
            int at = from, found = from >= 0 && from < rows.Count && rows[from].Selectable ? from : -1;
            while (steps > 0)
            {
                at += direction;
                if (at < 0 || at >= rows.Count) break;
                if (!rows[at].Selectable) continue;
                found = at;
                steps--;
            }
            if (found < 0)
                for (int r = 0; r < rows.Count; r++)
                    if (rows[r].Selectable) return r;
            return found;
        }

        private static int Clamp(int cursor, List<Row> rows)
        {
            if (cursor >= 0 && cursor < rows.Count && rows[cursor].Selectable) return cursor;
            return Step(rows, cursor < 0 ? -1 : cursor >= rows.Count ? rows.Count : cursor, cursor >= rows.Count ? -1 : 1);
        }

        // ---- painting ----

        private const string Dim = "\u001b[90m", Plain = "\u001b[0m", Type = "\u001b[1;36m";

        private void Paint(int width, bool focus)
        {
            int height = Height;
            _perRow = width >= 78 ? 16 : 8;
            _fit = height - 2;
            var frame = new StringBuilder();
            // From the top, no cursor, no wrapping, no scrollback: the panel is
            // read, not typed in, and scrolls itself.
            frame.Append("\u001b[?25l\u001b[?7l\u001b[3J\u001b[H");
            Result result = _current;
            List<Row> rows;
            int head;
            if (result == null)
            {
                rows = new List<Row>();
                rows.Add(new Row { Text = Dim + "Nothing shown yet. Objects open this panel;" + Plain, Parent = -1 });
                rows.Add(new Row { Text = Dim + "text and bytes wait in it. F12 hides it," + Plain, Parent = -1 });
                rows.Add(new Row { Text = Dim + "Ctrl+Tab gives it the keys, Esc gives them back." + Plain, Parent = -1 });
                head = 0;
            }
            else rows = Rows(result, _perRow, _fit, out head);

            int cursor = result == null ? -1 : Clamp(result.Cursor, rows);
            if (result != null) result.Cursor = cursor;

            // The header stays; the rest scrolls to keep the cursor in sight.
            int body = height - head;
            int top = result == null ? head : result.Top;
            if (cursor >= 0)
            {
                if (cursor < top) top = cursor;
                if (cursor >= top + body) top = cursor - body + 1;
            }
            if (top > rows.Count - body) top = rows.Count - body;
            if (top < head) top = head;
            if (result != null) result.Top = top;

            int written = 0;
            for (int r = 0; r < head && written < height; r++, written++)
                Line(frame, rows[r].Text, width, false, written == height - 1);
            for (int r = top; r < rows.Count && written < height; r++, written++)
                Line(frame, rows[r].Text, width, focus && r == cursor, written == height - 1);
            if (written < height) frame.Append("\u001b[J");
            AppHost.Terminal.WriteSide(frame.ToString());
        }

        private static void Line(StringBuilder frame, string text, int width, bool selected, bool last)
        {
            string shown = InspectNode.Cut(text, width);
            if (selected)
            {
                string plain = InspectNode.Plain(shown);
                frame.Append("\u001b[7m").Append(plain);
                for (int w = InspectNode.Width(plain); w < width; w++) frame.Append(' ');
                frame.Append(Plain);
            }
            else frame.Append(shown).Append(Plain).Append("\u001b[K");
            if (!last) frame.Append("\r\n");
        }

        // The rows of a command: its header, then each item; head is how many
        // rows the header takes.
        private static List<Row> Rows(Result result, int perRow, int fit, out int head)
        {
            var rows = new List<Row>();
            var title = new StringBuilder();
            title.Append(Dim).Append('#').Append(result.Number.ToString()).Append(" $ ").Append(Plain).Append(result.Line);
            rows.Add(new Row { Text = title.ToString(), Parent = -1 });
            rows.Add(new Row { Text = Contents(result), Parent = -1 });
            head = rows.Count;

            foreach (Item item in result.Items)
            {
                if (item.Nodes != null) AddObjects(rows, item, fit);
                else if (item.Text != null) AddLines(rows, item, item.Text.ToString(), item.TextLeft, "characters");
                else AddBytes(rows, item, perRow);
            }
            return rows;
        }

        private static string Contents(Result result)
        {
            var text = new StringBuilder();
            foreach (Item item in result.Items)
            {
                if (text.Length > 0) text.Append(Dim).Append(", ").Append(Plain);
                if (item.Nodes != null)
                {
                    text.Append(item.Count.ToString()).Append(Dim).Append(" × ").Append(Plain);
                    AppendType(text, item.Type);
                }
                else if (item.Text != null)
                {
                    // Strings are objects like the rest: a line each, named by their type.
                    int strings = 0;
                    for (int i = 0; i < item.Text.Length; i++) if (item.Text[i] == '\n') strings++;
                    if (item.Text.Length > 0 && item.Text[item.Text.Length - 1] != '\n') strings++;
                    text.Append(strings.ToString()).Append(Dim).Append(" × ").Append(Plain);
                    AppendType(text, "System.String");
                }
                else
                {
                    // As the feed shows a byte[]: the type and its length.
                    AppendType(text, "System.Byte");
                    text.Append(Dim).Append('[').Append((item.ByteLength + item.BytesLeft).ToString()).Append(']').Append(Plain);
                }
            }
            if (text.Length == 0) text.Append(Dim).Append("nothing").Append(Plain);
            return text.ToString();
        }

        private static void AppendType(StringBuilder text, string type)
        {
            type ??= "";
            if (type == "SharpOS.Std.Pipes.Expando") type = "Expando";
            int dot = type.LastIndexOf('.');
            if (dot > 0) text.Append(Dim).Append(type, 0, dot + 1).Append(Plain);
            text.Append(Type).Append(dot > 0 ? type.Substring(dot + 1) : type).Append(Plain);
        }

        // One object: its tree, open. A run: a table, a row each, or a tree each, closed.
        private static void AddObjects(List<Row> rows, Item item, int fit)
        {
            if (item.Nodes.Count == 1 && item.Count == 1)
            {
                // The first time, as many whole levels as the panel holds (one
                // at least); what the person opens or closes after stays so.
                InspectNode only = item.Nodes[0];
                if (!item.Opened)
                {
                    item.Opened = true;
                    int levels = Depth;
                    if (levels == 0)
                    {
                        levels = 1;
                        while (levels < InspectNode.Depth && Deeper(only, levels) && Visible(only, levels + 1) <= fit) levels++;
                    }
                    OpenDown(only, levels);
                }
                AddTree(rows, item, only, 0, -1);
                return;
            }
            if (item.Tree)
            {
                // Each by its number in the run: the heads alone are one type name over and over.
                for (int i = 0; i < item.Nodes.Count; i++)
                {
                    item.Nodes[i].Name ??= "[" + i.ToString() + "]";
                    AddTree(rows, item, item.Nodes[i], 0, -1);
                }
            }
            else AddTable(rows, item);
            if (item.Count > item.Nodes.Count)
                rows.Add(new Row { Text = Dim + "… " + (item.Count - item.Nodes.Count).ToString() + " more, not kept" + Plain, Parent = -1 });
        }

        // The rows a node takes with its first `levels` levels open.
        private static int Visible(InspectNode node, int levels)
        {
            int rows = 1;
            if (levels <= 0 || node.Children == null) return rows;
            foreach (InspectNode child in node.Children) rows += Visible(child, levels - 1);
            return node.Left > 0 ? rows + 1 : rows;
        }

        // Whether opening `levels` levels leaves something closed below them.
        private static bool Deeper(InspectNode node, int levels)
        {
            if (node.Children == null) return false;
            if (levels <= 0) return true;
            foreach (InspectNode child in node.Children)
                if (Deeper(child, levels - 1)) return true;
            return false;
        }

        private static void OpenDown(InspectNode node, int levels)
        {
            if (levels <= 0 || node.Children == null) return;
            node.Open = true;
            foreach (InspectNode child in node.Children) OpenDown(child, levels - 1);
        }

        private static void AddTree(List<Row> rows, Item item, InspectNode node, int depth, int parent)
        {
            var text = new StringBuilder();
            text.Append(' ', depth * 2);
            text.Append(Dim).Append(node.Children == null ? "  " : node.Open ? "▾ " : "▸ ").Append(Plain);
            if (node.Name != null) text.Append(node.Name).Append(Dim).Append(" = ").Append(Plain);
            text.Append(node.Head);
            int self = rows.Count;
            rows.Add(new Row { Text = text.ToString(), Node = node, Item = item, Parent = parent, Selectable = true });
            if (!node.Open || node.Children == null) return;
            foreach (InspectNode child in node.Children) AddTree(rows, item, child, depth + 1, self);
            if (node.Left > 0)
                rows.Add(new Row
                {
                    Text = new string(' ', (depth + 1) * 2) + Dim + "  … " + node.Left.ToString() + " more" + Plain,
                    Parent = self,
                });
        }

        // The objects' fields as columns: the names from the first objects, a
        // cell each, as wide as the widest of it up to CellWidth.
        private static void AddTable(List<Row> rows, Item item)
        {
            var columns = new List<string>();
            foreach (InspectNode node in item.Nodes)
            {
                if (node.Children == null) continue;
                foreach (InspectNode field in node.Children)
                    if (columns.Count < MaxColumns && !columns.Contains(field.Name)) columns.Add(field.Name);
            }
            var widths = new int[columns.Count];
            for (int c = 0; c < columns.Count; c++) widths[c] = InspectNode.Width(columns[c]);
            foreach (InspectNode node in item.Nodes)
                for (int c = 0; c < columns.Count; c++)
                {
                    InspectNode field = node.Field(columns[c]);
                    int w = field == null ? 0 : InspectNode.Width(field.Head);
                    if (w > widths[c]) widths[c] = w > CellWidth ? CellWidth : w;
                }

            var header = new StringBuilder();
            header.Append("  ");
            for (int c = 0; c < columns.Count; c++)
                Cell(header, Dim + columns[c] + Plain, widths[c], c == columns.Count - 1);
            rows.Add(new Row { Text = header.ToString(), Parent = -1 });

            foreach (InspectNode node in item.Nodes)
            {
                var line = new StringBuilder();
                line.Append(Dim).Append(node.Open ? "▾ " : "▸ ").Append(Plain);
                for (int c = 0; c < columns.Count; c++)
                {
                    InspectNode field = node.Field(columns[c]);
                    Cell(line, field == null ? "" : field.Head, widths[c], c == columns.Count - 1);
                }
                int self = rows.Count;
                rows.Add(new Row { Text = line.ToString(), Node = node, Item = item, Parent = -1, Selectable = true });
                // An opened row: the object's tree under it.
                if (node.Open && node.Children != null)
                {
                    foreach (InspectNode child in node.Children) AddTree(rows, item, child, 1, self);
                    if (node.Left > 0)
                        rows.Add(new Row { Text = "    " + Dim + "… " + node.Left.ToString() + " more" + Plain, Parent = self });
                }
            }
        }

        private static void Cell(StringBuilder line, string text, int width, bool last)
        {
            string cut = InspectNode.Cut(text, width);
            line.Append(cut).Append(Plain);
            if (last) return;
            for (int w = InspectNode.Width(cut); w < width; w++) line.Append(' ');
            line.Append(Dim).Append(" │ ").Append(Plain);
        }

        private const int MaxLines = 20000;

        private static void AddLines(List<Row> rows, Item item, string text, int left, string unit)
        {
            int start = 0, count = 0;
            while (start < text.Length && count < MaxLines)
            {
                int end = text.IndexOf('\n', start);
                if (end < 0) end = text.Length;
                string line = InspectNode.Plain(text.Substring(start, end - start).TrimEnd('\r')).Replace("\t", "    ");
                rows.Add(new Row { Text = line, Item = item, Parent = -1, Selectable = true });
                count++;
                start = end + 1;
            }
            if (start < text.Length)
                rows.Add(new Row { Text = Dim + "… more lines, not shown" + Plain, Parent = -1 });
            if (left > 0)
                rows.Add(new Row { Text = Dim + "… " + left.ToString() + " " + unit + " more, not kept" + Plain, Parent = -1 });
        }

        // Bytes as text when they are UTF-8 without control characters, else
        // as xxd shows them: an offset, sixteen in hex (eight in a narrow
        // panel), the same as text.
        private static void AddBytes(List<Row> rows, Item item, int perRow)
        {
            string text = AsText(item.Bytes, item.ByteLength);
            if (text != null)
            {
                AddLines(rows, item, text, item.BytesLeft, "bytes");
                return;
            }
            var line = new StringBuilder();
            int at = 0;
            for (; at < item.ByteLength && rows.Count < MaxLines; at += perRow)
            {
                line.Clear();
                line.Append(Dim).Append(at.ToString("x8")).Append(Plain).Append(' ');
                for (int i = 0; i < perRow; i++)
                {
                    if (i == 8 && perRow == 16) line.Append(' ');
                    if (at + i < item.ByteLength) line.Append(' ').Append(item.Bytes[at + i].ToString("x2"));
                    else line.Append("   ");
                }
                line.Append("  ").Append(Dim);
                for (int i = 0; i < perRow && at + i < item.ByteLength; i++)
                {
                    byte b = item.Bytes[at + i];
                    line.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
                }
                line.Append(Plain);
                rows.Add(new Row { Text = line.ToString(), Item = item, Parent = -1, Selectable = true });
            }
            if (at < item.ByteLength)
                rows.Add(new Row { Text = Dim + "… " + (item.ByteLength - at).ToString() + " bytes more, not shown" + Plain, Parent = -1 });
            if (item.BytesLeft > 0)
                rows.Add(new Row { Text = Dim + "… " + item.BytesLeft.ToString() + " bytes more, not kept" + Plain, Parent = -1 });
        }

        private static string AsText(byte[] bytes, int length)
        {
            for (int i = 0; i < length; i++)
            {
                byte b = bytes[i];
                if ((b < 0x20 && b != (byte)'\t' && b != (byte)'\n' && b != (byte)'\r') || b == 0x7F) return null;
            }
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes, 0, length);
            }
            catch (System.ArgumentException)
            {
                return null;
            }
        }
    }
}
