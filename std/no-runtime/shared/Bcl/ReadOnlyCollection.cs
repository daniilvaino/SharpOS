// System.Collections.ObjectModel.ReadOnlyCollection<T> — ported from
// dotnet/runtime:
//   src/libraries/System.Private.CoreLib/src/System/Collections/ObjectModel/ReadOnlyCollection.cs
//
// Cuts:
//  - IList non-generic implementation (IList, ICollection) — we don't
//    ship System.Collections.IList in our stub layer; add if LINQ /
//    other code demands it.
//  - Serialization attrs / DebuggerTypeProxy.
//  - Throws become Halt() (no exception engine).

namespace System.Collections.ObjectModel
{
    public class ReadOnlyCollection<T> : System.Collections.Generic.IList<T>,
                                          System.Collections.Generic.IReadOnlyList<T>
    {
        private readonly System.Collections.Generic.IList<T> list;

        public ReadOnlyCollection(System.Collections.Generic.IList<T> list)
        {
            if (list == null) Halt();
            this.list = list;
        }

        public int Count => list.Count;

        public T this[int index] => list[index];

        public bool Contains(T value) => list.Contains(value);

        public void CopyTo(T[] array, int index) => list.CopyTo(array, index);

        public System.Collections.Generic.IEnumerator<T> GetEnumerator() => list.GetEnumerator();

        public int IndexOf(T value) => list.IndexOf(value);

        protected System.Collections.Generic.IList<T> Items => list;

        bool System.Collections.Generic.ICollection<T>.IsReadOnly => true;

        T System.Collections.Generic.IList<T>.this[int index]
        {
            get => list[index];
            set => Halt();
        }

        void System.Collections.Generic.ICollection<T>.Add(T value) => Halt();
        void System.Collections.Generic.ICollection<T>.Clear() => Halt();
        void System.Collections.Generic.IList<T>.Insert(int index, T value) => Halt();
        bool System.Collections.Generic.ICollection<T>.Remove(T value) { Halt(); return false; }
        void System.Collections.Generic.IList<T>.RemoveAt(int index) => Halt();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => ((System.Collections.IEnumerable)list).GetEnumerator();

        private static void Halt() { while (true) ; }
    }
}
