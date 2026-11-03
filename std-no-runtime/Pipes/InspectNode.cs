using System.Collections.Generic;
using System.Text;

namespace SharpOS.Std.Pipes
{
    /// <summary>
    /// What the screen hands the inspector beside it (step237): every message
    /// it shows, while the message is there — objects, strings, bytes.
    /// </summary>
    public interface IScreenInspector
    {
        /// <summary>
        /// A message of a stream the screen shows, called with the screen's
        /// lock held; <paramref name="terminalText"/>: a string as the writer
        /// wrote it to a terminal — a piece, escapes kept — not a line.
        /// </summary>
        void Take(View root, bool terminalText);

        /// <summary>
        /// The feed's mark for what the inspector holds of the command now
        /// shown, after a run of objects' summary line (<c>▸ 1000 × T</c>).
        /// </summary>
        string Mark { get; }

        /// <summary>Whether a run of objects of one type is its first line and a summary line, not a line each.</summary>
        bool Summary { get; }
    }

    /// <summary>
    /// A value as the inspector shows it (step237): its own line and the
    /// values under it, copied out of the message while it is there, so that
    /// it can be opened and closed after the message is gone.
    /// </summary>
    public sealed class InspectNode
    {
        /// <summary>The field's name, <c>[i]</c> for an element; null at a root.</summary>
        public string Name;

        /// <summary>The value's own line in the screen's colours: a scalar itself, an object or an array its type.</summary>
        public string Head;

        /// <summary>The type's full name of an object or an array; null for a scalar.</summary>
        public string Type;

        /// <summary>The fields or elements kept; null when there is nothing under it.</summary>
        public List<InspectNode> Children;

        /// <summary>Elements or levels not kept (limits below): shown as "… N more".</summary>
        public int Left;

        /// <summary>Whether the inspector shows the values under it.</summary>
        public bool Open;

        /// <summary>Elements of an array kept, levels kept, and nodes kept per value at most.</summary>
        public const int Elements = 256, Depth = 8;

        /// <summary>
        /// A copy of <paramref name="value"/> down to <see cref="Depth"/> levels;
        /// <paramref name="budget"/> nodes at most, counted down across calls.
        /// </summary>
        public static InspectNode Of(View value, ref int budget) => Build(null, value, Depth, ref budget);

        private static InspectNode Build(string name, View value, int depth, ref int budget)
        {
            budget--;
            var node = new InspectNode { Name = name, Head = value.ToScreenHead() };
            ViewKind kind = value.Kind;
            if (kind == ViewKind.Object || kind == ViewKind.Array) node.Type = value.TypeName;
            if (!value.HasScreenBody) return node;

            node.Children = new List<InspectNode>();
            if (kind == ViewKind.Array)
            {
                int length = value.Length;
                int i = 0;
                for (; i < length && i < Elements && budget > 0 && depth > 0; i++)
                    node.Children.Add(Build("[" + i.ToString() + "]", value[i], depth - 1, ref budget));
                node.Left = length - i;
                return node;
            }
            int all = 0;
            foreach (ViewField f in value.Fields)
            {
                all++;
                if (budget > 0 && depth > 0) node.Children.Add(Build(f.Name, f.Value, depth - 1, ref budget));
            }
            node.Left = all - node.Children.Count;
            return node;
        }

        /// <summary>The field of this name among the children; null when there is none.</summary>
        public InspectNode Field(string name)
        {
            if (Children == null) return null;
            foreach (InspectNode c in Children)
                if (c.Name == name) return c;
            return null;
        }

        /// <summary>The number of visible columns of a line in the screen's colours: its escapes take none.</summary>
        public static int Width(string painted)
        {
            int width = 0;
            for (int i = 0; i < painted.Length; i++)
            {
                if (painted[i] == '\u001b') { i = SkipEscape(painted, i); continue; }
                if (!char.IsLowSurrogate(painted[i])) width++;
            }
            return width;
        }

        /// <summary>The line cut to <paramref name="columns"/> visible columns, its escapes kept, "…" where it was cut.</summary>
        public static string Cut(string painted, int columns)
        {
            if (columns <= 0) return "";
            if (Width(painted) <= columns) return painted;
            var text = new StringBuilder();
            int width = 0;
            for (int i = 0; i < painted.Length; i++)
            {
                if (painted[i] == '\u001b')
                {
                    int end = SkipEscape(painted, i);
                    text.Append(painted, i, end - i + 1);
                    i = end;
                    continue;
                }
                if (width == columns - 1 && !char.IsLowSurrogate(painted[i])) break;
                text.Append(painted[i]);
                if (!char.IsLowSurrogate(painted[i])) width++;
            }
            return text.Append("\u001b[0m…").ToString();
        }

        /// <summary>The line without its escapes.</summary>
        public static string Plain(string painted)
        {
            if (painted.IndexOf('\u001b') < 0) return painted;
            var text = new StringBuilder();
            for (int i = 0; i < painted.Length; i++)
            {
                if (painted[i] == '\u001b') { i = SkipEscape(painted, i); continue; }
                text.Append(painted[i]);
            }
            return text.ToString();
        }

        // The last character of the CSI sequence at i.
        private static int SkipEscape(string s, int i)
        {
            int j = i + 1;
            if (j < s.Length && s[j] == '[')
            {
                j++;
                while (j < s.Length && (s[j] < '@' || s[j] > '~')) j++;
            }
            return j < s.Length ? j : s.Length - 1;
        }
    }

    public readonly unsafe partial struct View
    {
        /// <summary>The value's own line in the inspector, in the screen's colours.</summary>
        internal string ToScreenHead()
        {
            Check();
            var text = new StringBuilder();
            AppendTreeHead(text);
            return text.ToString();
        }

        /// <summary>Whether the inspector has lines under the value's own: fields or elements.</summary>
        internal bool HasScreenBody
        {
            get
            {
                Check();
                return HasTreeBody;
            }
        }
    }
}
