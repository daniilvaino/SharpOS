// StringComparer — comparison as an object you can pass around.
//
// Shaped after dotnet/runtime v8.0 (MIT), with the comparisons themselves done
// ordinally. That is not a shortcut hidden in an implementation: with one
// culture in the system (see Globalization.cs) culture-sensitive ordering has
// nothing to be sensitive to, so InvariantCulture and Ordinal genuinely
// coincide here. Where they would differ on a real system — locale collation,
// case folding outside ASCII — this compares by code unit.

using System.Collections.Generic;

namespace System
{
    public abstract class StringComparer : IComparer<string?>, IEqualityComparer<string?>
    {
        // Factory properties, not cached statics: lazily initialised static
        // reference fields do not work in this environment (limits §1). The
        // comparers hold no state, so each call handing back a new one is
        // equivalent — only reference identity differs, which nothing here uses.
        public static StringComparer Ordinal => new OrdinalComparer(ignoreCase: false);
        public static StringComparer OrdinalIgnoreCase => new OrdinalComparer(ignoreCase: true);
        public static StringComparer InvariantCulture => new OrdinalComparer(ignoreCase: false);
        public static StringComparer InvariantCultureIgnoreCase => new OrdinalComparer(ignoreCase: true);
        public static StringComparer CurrentCulture => new OrdinalComparer(ignoreCase: false);
        public static StringComparer CurrentCultureIgnoreCase => new OrdinalComparer(ignoreCase: true);

        public abstract int Compare(string? x, string? y);
        public abstract bool Equals(string? x, string? y);
        public abstract int GetHashCode(string? obj);

        private sealed class OrdinalComparer : StringComparer
        {
            private readonly bool _ignoreCase;

            internal OrdinalComparer(bool ignoreCase) { _ignoreCase = ignoreCase; }

            public override int Compare(string? x, string? y)
            {
                if ((object?)x == (object?)y) return 0;
                if (x == null) return -1;
                if (y == null) return 1;

                int shared = x.Length < y.Length ? x.Length : y.Length;
                for (int i = 0; i < shared; i++)
                {
                    char a = Fold(x[i]);
                    char b = Fold(y[i]);
                    if (a != b) return a < b ? -1 : 1;
                }
                return x.Length - y.Length;
            }

            public override bool Equals(string? x, string? y)
            {
                if ((object?)x == (object?)y) return true;
                if (x == null || y == null || x.Length != y.Length) return false;

                for (int i = 0; i < x.Length; i++)
                    if (Fold(x[i]) != Fold(y[i])) return false;
                return true;
            }

            public override int GetHashCode(string? obj)
            {
                if (obj == null) return 0;

                int hash = 5381;
                for (int i = 0; i < obj.Length; i++)
                    hash = ((hash << 5) + hash) ^ Fold(obj[i]);
                return hash;
            }

            private char Fold(char c)
            {
                if (!_ignoreCase) return c;
                return c >= 'a' && c <= 'z' ? (char)(c - ('a' - 'A')) : c;
            }
        }
    }
}
