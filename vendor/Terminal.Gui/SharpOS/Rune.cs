// NStack replacement for the SharpOS port of Terminal.Gui.
//
// Terminal.Gui v1 is written against NStack's ustring and Rune, which existed
// because .NET had no rune type when the library was written: it targets
// netstandard2.0 and net472, and System.Text.Rune arrived later. We do have
// codepoints and strings, so the dependency buys nothing here — and NStack's
// Rune.ColumnWidth is broken on CJK (it reads table[max] after being handed the
// row COUNT), which we already hit and fixed once in XtermSharp (step144).
//
// What is left here is Rune alone: ustring is gone from the port entirely.
// The library was rewritten onto plain strings (494 occurrences across 34
// files), with the two things it actually needed NStack for — codepoint count
// and terminal width — moved to extensions in SharpOS/TextExtensions.cs.
//
// Rune stays because a character is still not a cell: wide characters take two
// columns, combining marks take none, and every truncation and padding
// decision depends on telling them apart.

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

        // A copy constructor looks redundant until you meet the callers:
        //   new Rune (Driver != null ? Driver.LeftBracket : '[')
        // The ternary settles on Rune (char converts to it), so the argument is
        // already a Rune and every other overload is the wrong one.
        public Rune(Rune other) { Value = other.Value; }

        public static implicit operator Rune(int value) => new Rune((uint)value);
        public static implicit operator Rune(byte b) => new Rune(b);
        public static implicit operator Rune(char ch) => new Rune(ch);
        public static implicit operator Rune(uint value) => new Rune(value);

        public static explicit operator uint(Rune r) => r.Value;
        public static explicit operator int(Rune r) => (int)r.Value;
        public static explicit operator char(Rune r) => (char)r.Value;

        // Ordering and arithmetic against plain integers. The library treats a
        // rune as a codepoint number in places — range checks against 0x7F, an
        // offset added to reach a box-drawing glyph — and NStack's Rune allowed
        // that directly.
        public static bool operator <(Rune a, int b) => a.Value < (uint)b;
        public static bool operator >(Rune a, int b) => a.Value > (uint)b;
        public static bool operator <=(Rune a, int b) => a.Value <= (uint)b;
        public static bool operator >=(Rune a, int b) => a.Value >= (uint)b;

        public static bool operator <(int a, Rune b) => (uint)a < b.Value;
        public static bool operator >(int a, Rune b) => (uint)a > b.Value;
        public static bool operator <=(int a, Rune b) => (uint)a <= b.Value;
        public static bool operator >=(int a, Rune b) => (uint)a >= b.Value;

        public static bool operator <(Rune a, Rune b) => a.Value < b.Value;
        public static bool operator >(Rune a, Rune b) => a.Value > b.Value;
        public static bool operator <=(Rune a, Rune b) => a.Value <= b.Value;
        public static bool operator >=(Rune a, Rune b) => a.Value >= b.Value;

        public static Rune operator +(Rune a, int b) => new Rune((uint)(a.Value + b));
        public static Rune operator -(Rune a, int b) => new Rune((uint)(a.Value - b));

        public static bool operator ==(Rune a, int b) => a.Value == (uint)b;
        public static bool operator !=(Rune a, int b) => a.Value != (uint)b;

        /// <summary>
        /// A codepoint that can actually be encoded: inside the Unicode range
        /// and not one half of a surrogate pair.
        /// </summary>
        public bool IsValid => Value <= 0x10FFFF && !(Value >= 0xD800 && Value <= 0xDFFF);

        /// <summary>This rune's width in terminal columns.</summary>
        public int ColumnWidth() => XtermSharp.RuneHelper.ConsoleWidth(Value);

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

        /// <summary>
        /// How many units this rune occupies in a string.
        /// </summary>
        /// <remarks>
        /// NStack returned the UTF-8 BYTE length here, because its ustring was
        /// a byte buffer and this number was used to index it. Ours counts
        /// UTF-16 units instead — one for most characters, two for a surrogate
        /// pair — because the strings it indexes are .NET strings.
        ///
        /// The change is the point, not a compromise: the three callers all add
        /// this up to produce an offset that is then handed straight to a slice.
        /// Byte lengths against a UTF-16 string would be silently wrong for
        /// every non-ASCII character, which is the sort of defect that survives
        /// English testing untouched.
        /// </remarks>
        public static int RuneLen(Rune rune) => rune.Value > 0xFFFF ? 2 : 1;

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
