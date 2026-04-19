// Minimal List<T> — subset of System.Collections.Generic.List<T> from
// dotnet/runtime's LowLevelList.cs, stripped to what compiles against
// our NoStdLib surface (no IEnumerable/ICollection interfaces — our
// System.Collections.Generic is empty — and no exceptions — we halt on
// out-of-range via ThrowHelpers.ThrowIndexOutOfRangeException).
//
// Storage: T[] backing array that grows x2 (first grow from 0 -> 4).
// foreach works through a struct Enumerator with MoveNext/Current —
// C# recognizes it by duck-typing, no IEnumerator interface needed.
//
// Not here yet (add when needed): IndexOf/Contains/Remove (need equality
// comparer — either IEquatable<T> + constraint, or boxing-based fallback),
// InsertAt, AddRange, conversion ctors.

namespace SharpOS.Std.Collections
{
    public sealed class List<T>
    {
        private const int DefaultCapacity = 4;

        private T[] _items;
        private int _count;

        public List()
        {
            _items = new T[0];
        }

        public List(int capacity)
        {
            _items = capacity <= 0 ? new T[0] : new T[capacity];
        }

        public int Count => _count;
        public int Capacity => _items.Length;

        public T this[int index]
        {
            get => _items[index];
            set => _items[index] = value;
        }

        public void Add(T item)
        {
            if (_count == _items.Length)
                Grow();
            _items[_count] = item;
            _count++;
        }

        public void Clear()
        {
            for (int i = 0; i < _count; i++)
                _items[i] = default;
            _count = 0;
        }

        public void RemoveAt(int index)
        {
            _count--;
            for (int i = index; i < _count; i++)
                _items[i] = _items[i + 1];
            _items[_count] = default;
        }

        private void Grow()
        {
            int newCap = _items.Length == 0 ? DefaultCapacity : _items.Length * 2;
            T[] larger = new T[newCap];
            for (int i = 0; i < _count; i++)
                larger[i] = _items[i];
            _items = larger;
        }

        // Struct enumerator for foreach (duck-typed, no IEnumerator<T> needed).
        public Enumerator GetEnumerator() => new Enumerator(this);

        public struct Enumerator
        {
            private readonly List<T> _list;
            private int _index;

            internal Enumerator(List<T> list)
            {
                _list = list;
                _index = -1;
            }

            public T Current => _list._items[_index];

            public bool MoveNext()
            {
                _index++;
                return _index < _list._count;
            }
        }
    }
}
