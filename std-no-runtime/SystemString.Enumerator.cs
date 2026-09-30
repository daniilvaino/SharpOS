// System.String : IEnumerable<char> + System.CharEnumerator.
//
// Makes LINQ-over-string compile AND run: `s.Last()`, `s.FirstOrDefault()`,
// `s.All(char-predicate)` bind through the IEnumerable<char> conversion, and
// unlike arrays (no SZArrayHelper — see docs/nativeaot-nostd-kernel-limits.md)
// the interface lands in String's DispatchMap, so runtime interface dispatch
// resolves normally.
//
// CharEnumerator shape from dotnet/runtime v8.0.27
//   src/libraries/System.Private.CoreLib/src/System/CharEnumerator.cs (MIT)
// Cuts: ICloneable, the _currentElement caching nuance kept as plain field.

using System.Collections;
using System.Collections.Generic;

namespace System
{
    public sealed unsafe partial class String : IEnumerable<char>
    {
        public CharEnumerator GetEnumerator() => new CharEnumerator(this);

        IEnumerator<char> IEnumerable<char>.GetEnumerator() => new CharEnumerator(this);

        IEnumerator IEnumerable.GetEnumerator() => new CharEnumerator(this);
    }

    public sealed class CharEnumerator : IEnumerator<char>
    {
        private readonly string _str;
        private int _index;

        internal CharEnumerator(string str)
        {
            _str = str;
            _index = -1;
        }

        public bool MoveNext()
        {
            if (_index < _str.Length - 1)
            {
                _index++;
                return true;
            }
            _index = _str.Length;
            return false;
        }

        public char Current
        {
            get
            {
                if (_index < 0 || _index >= _str.Length)
                    throw new InvalidOperationException("Enumeration has either not started or has already finished.");
                return _str[_index];
            }
        }

        object IEnumerator.Current => Current;

        public void Reset() => _index = -1;

        public void Dispose() { }
    }
}
