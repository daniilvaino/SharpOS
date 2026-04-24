// System.Collections.Generic.Stack<T> — API-compatible with BCL.
//
// Backing array + top index, grows x2 with DefaultCapacity=4. Push/Pop/
// Peek are O(1). Boxing-less foreach via public struct Enumerator (LIFO
// order — same as BCL). IEnumerable<T>/ICollection surface implemented
// so Stack<T> plays with the rest of the collection types.
//
// Cut from real Stack<T>:
//  - `_version` bump / enumerator invalidation.
//  - Argument* / InvalidOperation exceptions → Halt() (kernel has no
//    exception engine).
//  - ctor(IEnumerable<T>) — add when a caller needs it.
//  - Sync object / SyncRoot ICollection member — deprecated even in BCL.

namespace System.Collections.Generic
{
    public class Stack<T> : IEnumerable<T>, IReadOnlyCollection<T>
    {
        private const int DefaultCapacity = 4;

        private T[] _array;
        private int _size;

        public Stack()
        {
            _array = new T[0];
        }

        public Stack(int capacity)
        {
            _array = capacity <= 0 ? new T[0] : new T[capacity];
        }

        public int Count => _size;

        public void Clear()
        {
            for (int i = 0; i < _size; i++) _array[i] = default;
            _size = 0;
        }

        public bool Contains(T item)
        {
            IEqualityComparer<T> cmp = EqualityComparer<T>.Default;
            for (int i = 0; i < _size; i++)
            {
                if (cmp.Equals(_array[i], item)) return true;
            }
            return false;
        }

        public T Peek()
        {
            if (_size == 0) Halt();
            return _array[_size - 1];
        }

        public bool TryPeek(out T result)
        {
            if (_size == 0) { result = default; return false; }
            result = _array[_size - 1];
            return true;
        }

        public T Pop()
        {
            if (_size == 0) Halt();
            _size--;
            T value = _array[_size];
            _array[_size] = default;   // free reference for GC
            return value;
        }

        public bool TryPop(out T result)
        {
            if (_size == 0) { result = default; return false; }
            _size--;
            result = _array[_size];
            _array[_size] = default;
            return true;
        }

        public void Push(T item)
        {
            if (_size == _array.Length)
            {
                int newCapacity = _array.Length == 0 ? DefaultCapacity : _array.Length * 2;
                T[] newArray = new T[newCapacity];
                for (int i = 0; i < _size; i++) newArray[i] = _array[i];
                _array = newArray;
            }
            _array[_size] = item;
            _size++;
        }

        // Snapshot in LIFO order — oldest push last.
        public T[] ToArray()
        {
            T[] result = new T[_size];
            for (int i = 0; i < _size; i++)
                result[i] = _array[_size - 1 - i];
            return result;
        }

        // LIFO — newest first, matching BCL's Stack<T> enumerator contract.
        public Enumerator GetEnumerator() => new Enumerator(this);
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => new Enumerator(this);
        IEnumerator IEnumerable.GetEnumerator() => new Enumerator(this);

        private static void Halt() { while (true) ; }

        public struct Enumerator : IEnumerator<T>
        {
            private readonly Stack<T> _stack;
            private int _index;   // counts down from _size - 1; -2 = before-start, -1 = after-end
            private T _current;

            internal Enumerator(Stack<T> stack)
            {
                _stack = stack;
                _index = -2;
                _current = default;
            }

            public T Current => _current;
            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                if (_index == -2)
                    _index = _stack._size - 1;
                else
                    _index--;

                if (_index < 0)
                {
                    _current = default;
                    return false;
                }

                _current = _stack._array[_index];
                return true;
            }

            public void Reset()
            {
                _index = -2;
                _current = default;
            }

            public void Dispose() { }
        }
    }
}
