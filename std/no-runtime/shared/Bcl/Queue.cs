// System.Collections.Generic.Queue<T> — API-compatible with BCL.
//
// Circular buffer: T[] _array, int _head (dequeue point), int _tail
// (next enqueue slot), int _size. Grows x2 with DefaultCapacity=4,
// straightening the wrap-around into the new array. Enqueue/Dequeue/
// Peek are O(1).
//
// Indexing rule: physical[logical] = _array[(_head + logical) % cap].
// Empty: _size == 0 (with _head possibly anywhere). Full-needs-grow:
// _size == _array.Length.
//
// Cut from real Queue<T>:
//  - `_version` bump / enumerator invalidation.
//  - Argument* / InvalidOperation throws → Halt() (no exception engine).
//  - ctor(IEnumerable<T>) — add when caller needs it.

namespace System.Collections.Generic
{
    public class Queue<T> : IEnumerable<T>, IReadOnlyCollection<T>
    {
        private const int DefaultCapacity = 4;

        private T[] _array;
        private int _head;
        private int _tail;
        private int _size;

        public Queue()
        {
            _array = new T[0];
        }

        public Queue(int capacity)
        {
            _array = capacity <= 0 ? new T[0] : new T[capacity];
        }

        public int Count => _size;

        public void Clear()
        {
            if (_size != 0)
            {
                if (_head < _tail)
                {
                    for (int i = _head; i < _tail; i++) _array[i] = default;
                }
                else
                {
                    for (int i = _head; i < _array.Length; i++) _array[i] = default;
                    for (int i = 0; i < _tail; i++) _array[i] = default;
                }
                _size = 0;
            }
            _head = 0;
            _tail = 0;
        }

        public bool Contains(T item)
        {
            IEqualityComparer<T> cmp = EqualityComparer<T>.Default;
            int cap = _array.Length;
            int idx = _head;
            for (int i = 0; i < _size; i++)
            {
                if (cmp.Equals(_array[idx], item)) return true;
                idx++;
                if (idx == cap) idx = 0;
            }
            return false;
        }

        public void Enqueue(T item)
        {
            if (_size == _array.Length)
            {
                int newCapacity = _array.Length == 0 ? DefaultCapacity : _array.Length * 2;
                Grow(newCapacity);
            }
            _array[_tail] = item;
            _tail++;
            if (_tail == _array.Length) _tail = 0;
            _size++;
        }

        public T Dequeue()
        {
            if (_size == 0) Halt();
            T removed = _array[_head];
            _array[_head] = default;
            _head++;
            if (_head == _array.Length) _head = 0;
            _size--;
            return removed;
        }

        public bool TryDequeue(out T result)
        {
            if (_size == 0) { result = default; return false; }
            result = _array[_head];
            _array[_head] = default;
            _head++;
            if (_head == _array.Length) _head = 0;
            _size--;
            return true;
        }

        public T Peek()
        {
            if (_size == 0) Halt();
            return _array[_head];
        }

        public bool TryPeek(out T result)
        {
            if (_size == 0) { result = default; return false; }
            result = _array[_head];
            return true;
        }

        // Snapshot in FIFO order (head first).
        public T[] ToArray()
        {
            T[] result = new T[_size];
            if (_size == 0) return result;
            int cap = _array.Length;
            int idx = _head;
            for (int i = 0; i < _size; i++)
            {
                result[i] = _array[idx];
                idx++;
                if (idx == cap) idx = 0;
            }
            return result;
        }

        // Enlarge and unwrap into a contiguous layout starting at index 0.
        private void Grow(int newCapacity)
        {
            T[] newArray = new T[newCapacity];
            if (_size > 0)
            {
                if (_head < _tail)
                {
                    for (int i = 0; i < _size; i++) newArray[i] = _array[_head + i];
                }
                else
                {
                    int firstRun = _array.Length - _head;
                    for (int i = 0; i < firstRun; i++) newArray[i] = _array[_head + i];
                    for (int i = 0; i < _tail; i++) newArray[firstRun + i] = _array[i];
                }
            }
            _array = newArray;
            _head = 0;
            _tail = _size == newCapacity ? 0 : _size;
        }

        public Enumerator GetEnumerator() => new Enumerator(this);
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => new Enumerator(this);
        IEnumerator IEnumerable.GetEnumerator() => new Enumerator(this);

        private static void Halt() { while (true) ; }

        public struct Enumerator : IEnumerator<T>
        {
            private readonly Queue<T> _q;
            private int _index;   // -1 = before start, otherwise 0..(_size-1)
            private T _current;

            internal Enumerator(Queue<T> q)
            {
                _q = q;
                _index = -1;
                _current = default;
            }

            public T Current => _current;
            object IEnumerator.Current => Current;

            public bool MoveNext()
            {
                _index++;
                if (_index >= _q._size)
                {
                    _current = default;
                    return false;
                }
                int cap = _q._array.Length;
                int physIdx = _q._head + _index;
                if (physIdx >= cap) physIdx -= cap;
                _current = _q._array[physIdx];
                return true;
            }

            public void Reset()
            {
                _index = -1;
                _current = default;
            }

            public void Dispose() { }
        }
    }
}
