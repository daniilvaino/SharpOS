// NStack replacement for the SharpOS port of Terminal.Gui.
//
// Terminal.Gui v1 is written against NStack's ustring and Rune, which existed
// because .NET had no rune type when the library was written: it targets
// netstandard2.0 and net472, and System.Text.Rune arrived later. We do have
// codepoints and strings, so the dependency buys nothing here — and NStack's
// Rune.ColumnWidth is broken on CJK (it reads table[max] after being handed the
// row COUNT), which we already hit and fixed once in XtermSharp (step144).
//
// Written under the same namespace and type names, so the library's own sources
// stay untouched: "using NStack;" resolves here instead.
//
// Deliberately missing: ustring.Length. In NStack that is the UTF-8 BYTE count
// and the indexer returns a byte, while a .NET string counts UTF-16 units. The
// two agree for ASCII and diverge for everything else — exactly the kind of
// silent change that surfaces much later as a layout drifting on Cyrillic.
// Leaving it out turns a quiet semantic swap into compile errors at the ~13 call
// sites that read it, each of which then gets looked at on its own.

using System;
using System.Collections.Generic;
using System.Text;

// Rune sits in System, not in NStack: that is where NStack itself declares it,
// which is why the library writes "using NStack;" for ustring and reaches Rune
// with no using at all. Put it anywhere else and 46 references stop resolving.
namespace System
{
    /// <summary>One Unicode codepoint. A wrapper over uint, as in NStack.</summary>
    public readonly struct Rune : IEquatable<Rune>, IComparable<Rune>
    {
        public readonly uint Value;

        public Rune(uint value) { Value = value; }
        public Rune(char ch) { Value = ch; }

        public static implicit operator Rune(int value) => new Rune((uint)value);
        public static implicit operator Rune(byte b) => new Rune(b);
        public static implicit operator Rune(char ch) => new Rune(ch);
        public static implicit operator Rune(uint value) => new Rune(value);

        public static explicit operator uint(Rune r) => r.Value;
        public static explicit operator int(Rune r) => (int)r.Value;
        public static explicit operator char(Rune r) => (char)r.Value;

        public static bool operator ==(Rune a, Rune b) => a.Value == b.Value;
        public static bool operator !=(Rune a, Rune b) => a.Value != b.Value;

        public bool Equals(Rune other) => Value == other.Value;
        public override bool Equals(object obj) => obj is Rune r && r.Value == Value;
        public override int GetHashCode() => (int)Value;
        public int CompareTo(Rune other) => Value.CompareTo(other.Value);

        public override string ToString() =>
            Value <= 0xFFFF ? ((char)Value).ToString() : char.ConvertFromUtf32((int)Value);

        // Why the layout needs a rune type at all: a character is not a cell.
        // Wide characters take two columns, combining marks take none, and every
        // padding and truncation decision depends on knowing which.
        //
        // Delegates to the port we already fixed rather than carrying a third
        // copy of the same wcwidth table.
        public static int ColumnWidth(Rune rune) => XtermSharp.RuneHelper.ConsoleWidth(rune.Value);

        public static int RuneLen(Rune rune) =>
            rune.Value < 0x80 ? 1 : rune.Value < 0x800 ? 2 : rune.Value < 0x10000 ? 3 : 4;

        public static bool IsWhiteSpace(Rune r) => r.Value <= 0xFFFF && char.IsWhiteSpace((char)r.Value);
        public static bool IsLetterOrDigit(Rune r) => r.Value <= 0xFFFF && char.IsLetterOrDigit((char)r.Value);
        public static bool IsLetterOrNumber(Rune r) => IsLetterOrDigit(r);
        public static bool IsPunctuation(Rune r) => r.Value <= 0xFFFF && char.IsPunctuation((char)r.Value);
        public static bool IsSymbol(Rune r) => r.Value <= 0xFFFF && char.IsSymbol((char)r.Value);
        public static bool IsUpper(Rune r) => r.Value <= 0xFFFF && char.IsUpper((char)r.Value);
        public static bool IsLower(Rune r) => r.Value <= 0xFFFF && char.IsLower((char)r.Value);
        public static bool IsDigit(Rune r) => r.Value <= 0xFFFF && char.IsDigit((char)r.Value);
        public static bool IsLetter(Rune r) => r.Value <= 0xFFFF && char.IsLetter((char)r.Value);
        public static Rune ToUpper(Rune r) => r.Value <= 0xFFFF ? new Rune(char.ToUpperInvariant((char)r.Value)) : r;
        public static Rune ToLower(Rune r) => r.Value <= 0xFFFF ? new Rune(char.ToLowerInvariant((char)r.Value)) : r;

        public static bool DecodeSurrogatePair(char high, char low, out Rune rune)
        {
            if (char.IsHighSurrogate(high) && char.IsLowSurrogate(low))
            {
                rune = new Rune((uint)char.ConvertToUtf32(high, low));
                return true;
            }
            rune = default;
            return false;
        }
    }

}

namespace NStack
{
    /// <summary>
    /// Terminal.Gui's string type, backed by a plain string: everything the
    /// library asks of it beyond rune counting is what System.String already does.
    /// </summary>
    public sealed class ustring : IEquatable<ustring>, IComparable<ustring>
    {
        private readonly string _s;

        private ustring(string s) { _s = s ?? string.Empty; }

        public static readonly ustring Empty = new ustring(string.Empty);

        public static ustring Make(string s) => new ustring(s ?? string.Empty);
        public static ustring Make(char ch) => new ustring(ch.ToString());
        public static ustring Make(Rune rune) => new ustring(rune.ToString());
        public static ustring Make(int rune) => new ustring(new Rune((uint)rune).ToString());
        public static ustring Make(byte[] utf8) => new ustring(Encoding.UTF8.GetString(utf8));

        public static ustring Make(IEnumerable<Rune> runes)
        {
            var sb = new StringBuilder();
            foreach (var r in runes) sb.Append(r.ToString());
            return new ustring(sb.ToString());
        }

        public static implicit operator ustring(string s) => new ustring(s);
        public static implicit operator string(ustring u) => u == null ? string.Empty : u._s;

        public static bool operator ==(ustring a, ustring b) =>
            ReferenceEquals(a, b) || (a is object && b is object && a._s == b._s);
        public static bool operator !=(ustring a, ustring b) => !(a == b);

        public static ustring operator +(ustring a, ustring b) =>
            new ustring((a == null ? string.Empty : a._s) + (b == null ? string.Empty : b._s));

        public bool IsEmpty => _s.Length == 0;
        public static bool IsNullOrEmpty(ustring u) => u == null || u._s.Length == 0;

        /// <summary>Codepoints, not UTF-16 units — a surrogate pair counts once.</summary>
        public int RuneCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _s.Length; i++)
                {
                    if (char.IsHighSurrogate(_s[i]) && i + 1 < _s.Length && char.IsLowSurrogate(_s[i + 1])) i++;
                    n++;
                }
                return n;
            }
        }

        public List<Rune> ToRuneList()
        {
            var list = new List<Rune>(_s.Length);
            for (int i = 0; i < _s.Length; i++)
            {
                if (char.IsHighSurrogate(_s[i]) && i + 1 < _s.Length && char.IsLowSurrogate(_s[i + 1]))
                {
                    list.Add(new Rune((uint)char.ConvertToUtf32(_s[i], _s[i + 1])));
                    i++;
                }
                else list.Add(new Rune(_s[i]));
            }
            return list;
        }

        public Rune[] ToRunes() => ToRuneList().ToArray();

        public int ConsoleWidth
        {
            get
            {
                int w = 0;
                foreach (var r in ToRuneList())
                {
                    int cw = Rune.ColumnWidth(r);
                    if (cw > 0) w += cw;
                }
                return w;
            }
        }

        public bool Contains(ustring value) => _s.Contains(value._s);
        public bool Contains(string value) => _s.Contains(value);
        public bool StartsWith(ustring value) => _s.StartsWith(value._s);
        public bool EndsWith(ustring value) => _s.EndsWith(value._s);
        public int IndexOf(ustring value) => _s.IndexOf(value._s);
        public int IndexOf(Rune value) => _s.IndexOf(value.ToString());
        public int IndexOf(char value) => _s.IndexOf(value);
        public ustring Substring(int start) => new ustring(_s.Substring(start));
        public ustring Substring(int start, int length) => new ustring(_s.Substring(start, length));
        public ustring Replace(ustring oldValue, ustring newValue) => new ustring(_s.Replace(oldValue._s, newValue._s));

        public ustring[] Split(ustring separator)
        {
            string[] parts = _s.Split(separator._s);
            var result = new ustring[parts.Length];
            for (int i = 0; i < parts.Length; i++) result[i] = new ustring(parts[i]);
            return result;
        }

        public bool Equals(ustring other) => other is object && _s == other._s;
        public override bool Equals(object obj) => obj is ustring u && u._s == _s;
        public override int GetHashCode() => _s.GetHashCode();
        public int CompareTo(ustring other) => string.CompareOrdinal(_s, other == null ? null : other._s);
        public override string ToString() => _s;
    }
}
