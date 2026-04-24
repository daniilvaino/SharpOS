// System.Collections.Generic.LinkedList<T> — ported verbatim from
// dotnet/runtime:
//   src/libraries/System.Collections/src/System/Collections/Generic/LinkedList.cs
//
// Cuts:
//  - Serialization attrs (ISerializable / IDeserializationCallback,
//    [Serializable], SerializationInfo ctor, GetObjectData,
//    OnDeserialization, _siInfo) — no formatter runtime in our env.
//  - Debug attrs (DebuggerTypeProxy/Display, TypeForwardedFrom) — no
//    debug support surface.
//  - ctor(IEnumerable<T>) — callers don't use it yet.
//  - ICollection non-generic CopyTo(Array, int) — requires reflection
//    (ArrayTypeMismatchException catch) we don't support; re-add if a
//    consumer actually needs it.
//  - Throw-based parameter validation replaced with Halt() (no
//    exception engine in the kernel — same end result, just honest).
//  - Debug.Assert calls — silent drops.

using System.Runtime.CompilerServices;

namespace System.Collections.Generic
{
    public class LinkedList<T> : ICollection<T>, IReadOnlyCollection<T>
    {
        // This LinkedList is a doubly-Linked circular list.
        internal LinkedListNode<T> head;
        internal int count;
        internal int version;

        public LinkedList() { }

        public int Count => count;

        public LinkedListNode<T> First => head;

        public LinkedListNode<T> Last => head?.prev;

        bool ICollection<T>.IsReadOnly => false;

        void ICollection<T>.Add(T value) => AddLast(value);

        public LinkedListNode<T> AddAfter(LinkedListNode<T> node, T value)
        {
            ValidateNode(node);
            LinkedListNode<T> result = new LinkedListNode<T>(node.list, value);
            InternalInsertNodeBefore(node.next, result);
            return result;
        }

        public void AddAfter(LinkedListNode<T> node, LinkedListNode<T> newNode)
        {
            ValidateNode(node);
            ValidateNewNode(newNode);
            InternalInsertNodeBefore(node.next, newNode);
            newNode.list = this;
        }

        public LinkedListNode<T> AddBefore(LinkedListNode<T> node, T value)
        {
            ValidateNode(node);
            LinkedListNode<T> result = new LinkedListNode<T>(node.list, value);
            InternalInsertNodeBefore(node, result);
            if (node == head) head = result;
            return result;
        }

        public void AddBefore(LinkedListNode<T> node, LinkedListNode<T> newNode)
        {
            ValidateNode(node);
            ValidateNewNode(newNode);
            InternalInsertNodeBefore(node, newNode);
            newNode.list = this;
            if (node == head) head = newNode;
        }

        public LinkedListNode<T> AddFirst(T value)
        {
            LinkedListNode<T> result = new LinkedListNode<T>(this, value);
            if (head == null)
            {
                InternalInsertNodeToEmptyList(result);
            }
            else
            {
                InternalInsertNodeBefore(head, result);
                head = result;
            }
            return result;
        }

        public void AddFirst(LinkedListNode<T> node)
        {
            ValidateNewNode(node);
            if (head == null)
            {
                InternalInsertNodeToEmptyList(node);
            }
            else
            {
                InternalInsertNodeBefore(head, node);
                head = node;
            }
            node.list = this;
        }

        public LinkedListNode<T> AddLast(T value)
        {
            LinkedListNode<T> result = new LinkedListNode<T>(this, value);
            if (head == null) InternalInsertNodeToEmptyList(result);
            else InternalInsertNodeBefore(head, result);
            return result;
        }

        public void AddLast(LinkedListNode<T> node)
        {
            ValidateNewNode(node);
            if (head == null) InternalInsertNodeToEmptyList(node);
            else InternalInsertNodeBefore(head, node);
            node.list = this;
        }

        public void Clear()
        {
            LinkedListNode<T> current = head;
            while (current != null)
            {
                LinkedListNode<T> temp = current;
                current = current.Next;
                temp.Invalidate();
            }

            head = null;
            count = 0;
            version++;
        }

        public bool Contains(T value) => Find(value) != null;

        public void CopyTo(T[] array, int index)
        {
            if (array == null) Halt();
            if (index < 0) Halt();
            if (index > array.Length) Halt();
            if (array.Length - index < Count) Halt();

            LinkedListNode<T> node = head;
            if (node != null)
            {
                do
                {
                    array[index++] = node.item;
                    node = node.next;
                } while (node != head);
            }
        }

        public LinkedListNode<T> Find(T value)
        {
            LinkedListNode<T> node = head;
            EqualityComparer<T> c = EqualityComparer<T>.Default;
            if (node != null)
            {
                if (value != null)
                {
                    do
                    {
                        if (c.Equals(node.item, value)) return node;
                        node = node.next;
                    } while (node != head);
                }
                else
                {
                    do
                    {
                        if (node.item == null) return node;
                        node = node.next;
                    } while (node != head);
                }
            }
            return null;
        }

        public LinkedListNode<T> FindLast(T value)
        {
            if (head == null) return null;

            LinkedListNode<T> last = head.prev;
            LinkedListNode<T> node = last;
            EqualityComparer<T> c = EqualityComparer<T>.Default;
            if (node != null)
            {
                if (value != null)
                {
                    do
                    {
                        if (c.Equals(node.item, value)) return node;
                        node = node.prev;
                    } while (node != last);
                }
                else
                {
                    do
                    {
                        if (node.item == null) return node;
                        node = node.prev;
                    } while (node != last);
                }
            }
            return null;
        }

        public Enumerator GetEnumerator() => new Enumerator(this);

        IEnumerator<T> IEnumerable<T>.GetEnumerator() => GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public bool Remove(T value)
        {
            LinkedListNode<T> node = Find(value);
            if (node != null)
            {
                InternalRemoveNode(node);
                return true;
            }
            return false;
        }

        public void Remove(LinkedListNode<T> node)
        {
            ValidateNode(node);
            InternalRemoveNode(node);
        }

        public void RemoveFirst()
        {
            if (head == null) Halt();
            InternalRemoveNode(head);
        }

        public void RemoveLast()
        {
            if (head == null) Halt();
            InternalRemoveNode(head.prev);
        }

        private void InternalInsertNodeBefore(LinkedListNode<T> node, LinkedListNode<T> newNode)
        {
            newNode.next = node;
            newNode.prev = node.prev;
            node.prev.next = newNode;
            node.prev = newNode;
            version++;
            count++;
        }

        private void InternalInsertNodeToEmptyList(LinkedListNode<T> newNode)
        {
            newNode.next = newNode;
            newNode.prev = newNode;
            head = newNode;
            version++;
            count++;
        }

        internal void InternalRemoveNode(LinkedListNode<T> node)
        {
            if (node.next == node)
            {
                head = null;
            }
            else
            {
                node.next.prev = node.prev;
                node.prev.next = node.next;
                if (head == node) head = node.next;
            }
            node.Invalidate();
            count--;
            version++;
        }

        internal static void ValidateNewNode(LinkedListNode<T> node)
        {
            if (node == null) Halt();
            if (node.list != null) Halt();
        }

        internal void ValidateNode(LinkedListNode<T> node)
        {
            if (node == null) Halt();
            if (node.list != this) Halt();
        }

        private static void Halt() { while (true) ; }

        // Struct enumerator (BCL shape). Unlike Dictionary/HashSet we haven't
        // hit the ILC 7.0.20 boxed-struct-enumerator issue with LinkedList's
        // shape, and keeping struct here matches BCL byte-for-byte.
        public struct Enumerator : IEnumerator<T>, IEnumerator
        {
            private readonly LinkedList<T> _list;
            private LinkedListNode<T> _node;
            private readonly int _version;
            private T _current;
            private int _index;

            internal Enumerator(LinkedList<T> list)
            {
                _list = list;
                _version = list.version;
                _node = list.head;
                _current = default;
                _index = 0;
            }

            public T Current => _current;

            object IEnumerator.Current
            {
                get
                {
                    if (_index == 0 || (_index == _list.Count + 1)) Halt();
                    return Current;
                }
            }

            public bool MoveNext()
            {
                if (_version != _list.version) Halt();

                if (_node == null)
                {
                    _index = _list.Count + 1;
                    return false;
                }

                ++_index;
                _current = _node.item;
                _node = _node.next;
                if (_node == _list.head) _node = null;
                return true;
            }

            void IEnumerator.Reset()
            {
                if (_version != _list.version) Halt();

                _current = default;
                _node = _list.head;
                _index = 0;
            }

            public void Dispose() { }
        }
    }

    public sealed class LinkedListNode<T>
    {
        internal LinkedList<T> list;
        internal LinkedListNode<T> next;
        internal LinkedListNode<T> prev;
        internal T item;

        public LinkedListNode(T value) { item = value; }

        internal LinkedListNode(LinkedList<T> list, T value)
        {
            this.list = list;
            item = value;
        }

        public LinkedList<T> List => list;

        public LinkedListNode<T> Next => next == null || next == list.head ? null : next;

        public LinkedListNode<T> Previous => prev == null || this == list.head ? null : prev;

        public T Value { get => item; set => item = value; }

        public ref T ValueRef => ref item;

        internal void Invalidate()
        {
            list = null;
            next = null;
            prev = null;
        }
    }
}
