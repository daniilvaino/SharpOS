// Ported from dotnet/runtime
// src/libraries/System.Private.CoreLib/src/System/String.Manipulation.cs
// (FormatHelper) — matches the canonical {index[,alignment][:formatString]}
// parser shape byte-for-byte. Differences from upstream:
//   - IFormatProvider parameter dropped (we always pass null — no cultures,
//     no ICustomFormatter resolution path).
//   - ICustomFormatter not consulted (we never have one).
//   - FormatException carries a plain canned message instead of SR.* keys.
// IFormattable consumers (Int32.ToString("X"), etc.) still get spec strings
// honoured exactly as upstream when the arg type implements IFormattable.

using System.Text;

namespace System
{
    public sealed unsafe partial class String
    {
        public static string Format(string format, object arg0)
            => FormatHelper(format, new ParamsArray(arg0));

        public static string Format(string format, object arg0, object arg1)
            => FormatHelper(format, new ParamsArray(arg0, arg1));

        public static string Format(string format, object arg0, object arg1, object arg2)
            => FormatHelper(format, new ParamsArray(arg0, arg1, arg2));

        public static string Format(string format, params object[] args)
        {
            if ((object)format == null)
                ThrowFormatNull(nameof(format));
            if (args == null)
                ThrowFormatNull(nameof(args));
            return FormatHelper(format, new ParamsArray(args));
        }

        private static string FormatHelper(string format, ParamsArray args)
        {
            if ((object)format == null)
                ThrowFormatNull(nameof(format));

            var sb = new StringBuilder(format.Length + args.Length * 8);
            AppendFormatHelper(sb, format, args);
            return sb.ToString();
        }

        // Canonical BCL parser — same loop shape as upstream AppendFormatHelper.
        // Reads {index[,width][:spec]} sequences; doubled '{{' / '}}' become
        // literal braces. Width is signed (negative = left-justified).
        internal static void AppendFormatHelper(StringBuilder result, string format, ParamsArray args)
        {
            int pos = 0;
            int len = format.Length;
            char ch = '\0';

            while (true)
            {
                while (pos < len)
                {
                    ch = format[pos];
                    pos++;
                    if (ch == '}')
                    {
                        if (pos < len && format[pos] == '}')
                            pos++;
                        else
                            ThrowFormatInvalid();
                    }
                    if (ch == '{')
                    {
                        if (pos < len && format[pos] == '{')
                            pos++;
                        else
                        {
                            pos--;
                            break;
                        }
                    }
                    result.Append(ch);
                }

                if (pos == len) break;
                pos++;
                if (pos == len || (ch = format[pos]) < '0' || ch > '9')
                    ThrowFormatInvalid();

                // index
                int index = 0;
                do
                {
                    index = index * 10 + ch - '0';
                    pos++;
                    if (pos == len) ThrowFormatInvalid();
                    ch = format[pos];
                } while (ch >= '0' && ch <= '9' && index < 1000000);

                if (index >= args.Length) ThrowFormatIndexOutOfRange();

                while (pos < len && (ch = format[pos]) == ' ') pos++;

                // alignment
                bool leftJustify = false;
                int width = 0;
                if (ch == ',')
                {
                    pos++;
                    while (pos < len && format[pos] == ' ') pos++;
                    if (pos == len) ThrowFormatInvalid();
                    ch = format[pos];
                    if (ch == '-')
                    {
                        leftJustify = true;
                        pos++;
                        if (pos == len) ThrowFormatInvalid();
                        ch = format[pos];
                    }
                    if (ch < '0' || ch > '9') ThrowFormatInvalid();
                    do
                    {
                        width = width * 10 + ch - '0';
                        pos++;
                        if (pos == len) ThrowFormatInvalid();
                        ch = format[pos];
                    } while (ch >= '0' && ch <= '9' && width < 1000000);
                }

                while (pos < len && (ch = format[pos]) == ' ') pos++;

                // format spec
                object arg = args[index];
                string itemFormat = null;
                if (ch == ':')
                {
                    pos++;
                    int start = pos;
                    while (true)
                    {
                        if (pos == len) ThrowFormatInvalid();
                        ch = format[pos];
                        if (ch == '}') break;
                        if (ch == '{') ThrowFormatInvalid();
                        pos++;
                    }
                    if (pos > start)
                        itemFormat = format.Substring(start, pos - start);
                }
                if (ch != '}') ThrowFormatInvalid();
                pos++;

                // resolve arg → string
                // Plain if-cascade instead of `s ??= string.Empty;` —
                // null-coalescing assignment lowering can crash Roslyn on
                // our minimal Object/String stubs (same family as the
                // `?.` + `??` chain in DebuggerTypeProxyAttribute).
                string s = null;
                if (arg is IFormattable formattable)
                    s = formattable.ToString(itemFormat, null);
                else if (arg != null)
                    s = arg.ToString();
                if (s == null) s = string.Empty;

                // pad
                int pad = width - s.Length;
                if (!leftJustify && pad > 0) result.Append(' ', pad);
                result.Append(s);
                if (leftJustify && pad > 0) result.Append(' ', pad);
            }
        }

        private static void ThrowFormatNull(string argName)
        {
            throw new ArgumentNullException(argName);
        }

        private static void ThrowFormatInvalid()
        {
            throw new FormatException("Input string was not in a correct format.");
        }

        private static void ThrowFormatIndexOutOfRange()
        {
            throw new FormatException("Index (zero based) must be greater than or equal to zero and less than the size of the argument list.");
        }
    }

    // Mirrors BCL's internal ParamsArray (System.ParamsArray). 0..3 args
    // stored inline; 4+ via a real object[]. Differs from upstream in
    // avoiding the `s_oneArgArray`/`s_twoArgArray`/`s_threeArgArray`
    // sentinel statics — those would trip our ClassConstructorRunner trap
    // (static reference-field init). Length carried explicitly; `_args` is
    // null in inline-mode and the dispatcher uses index ranges instead of
    // reference identity to pick the slot.
    internal readonly struct ParamsArray
    {
        private readonly object _arg0;
        private readonly object _arg1;
        private readonly object _arg2;
        private readonly object[] _args;     // null when inline (Length <= 3)
        private readonly int _length;

        public ParamsArray(object arg0)
        {
            _arg0 = arg0; _arg1 = null; _arg2 = null;
            _args = null; _length = 1;
        }

        public ParamsArray(object arg0, object arg1)
        {
            _arg0 = arg0; _arg1 = arg1; _arg2 = null;
            _args = null; _length = 2;
        }

        public ParamsArray(object arg0, object arg1, object arg2)
        {
            _arg0 = arg0; _arg1 = arg1; _arg2 = arg2;
            _args = null; _length = 3;
        }

        public ParamsArray(object[] args)
        {
            int l = args.Length;
            _arg0 = l > 0 ? args[0] : null;
            _arg1 = l > 1 ? args[1] : null;
            _arg2 = l > 2 ? args[2] : null;
            _args = args;
            _length = l;
        }

        public int Length => _length;

        public object this[int index] => index switch
        {
            0 => _arg0,
            1 => _arg1,
            2 => _arg2,
            _ => _args[index],
        };
    }
}
