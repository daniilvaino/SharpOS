// System.Array<T> — the NativeAOT mechanism behind "T[] implements
// IEnumerable<T>/ICollection<T>/IList<T>/IReadOnlyList<T>". Ported from
// dotnet/runtime v8.0.27:
//   src/coreclr/nativeaot/System.Private.CoreLib/src/System/Array.NativeAot.cs
//   (class Array<T> + ArrayEnumerator)  (MIT)
//
// ILC treats System.Array<T> as the interface template for SZ arrays: the
// array MethodTable gets THIS class's interface list and dispatch map, and
// `this` inside these methods is the actual T[] instance (reinterpreted via
// Unsafe.As). Before step142 our Array<T> was an empty placeholder — array
// MTs carried NumInterfaces=0 and any interface use of an array died at
// dispatch (limits doc §4). With this port arrays are honest IEnumerable<T>
// on both tiers.
//
// Cuts vs original:
//   - ArrayEnumerator.Empty cached singleton — allocate per call (static
//     lazy field would need the cctor pathway for no real win here).
//   - ThrowHelper indirection — throw directly.
//   - Contains via Array.IndexOf — inline EqualityComparer loop (our std
//     Array has no IndexOf yet).
//   - IndexOf(T)/Insert/RemoveAt keep the BCL fixed-size-collection throw
//     behaviour.
//
// SharpOS: ICloneable and the non-generic ICollection and IList are implemented again here,
// with System.Array's semantics (step230). Upstream they live on System.Array
// alone, and the runtime reaches them by walking from the array's type to its
// base; our resolver does not walk an array's chain (its related type is the
// element, not the base), so `string[]` as an IList — Terminal.Gui's ListView
// and ComboBox take their source so — panicked "iface-resolve: no impl slot".
// Listed on Array<T>, they land in every array type's own dispatch map.

using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace System
{
    public class Array<T> : Array, IEnumerable<T>, ICollection<T>, IList<T>, IReadOnlyList<T>, ICloneable, ICollection, IList
    {
        // Prevent the C# compiler from generating a public default constructor.
        private Array() { }

        public new IEnumerator<T> GetEnumerator()
        {
            return new ArrayEnumerator(Unsafe.As<T[]>(this), this.Length);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public int Count => this.Length;

        //
        // Fun fact (BCL contract):
        //
        //  ((int[])a).IsReadOnly returns false.
        //  ((IList<int>)a).IsReadOnly returns true.
        //
        public new bool IsReadOnly => true;

        public void Add(T item)
        {
            throw new NotSupportedException("Collection was of a fixed size.");
        }

        public void Clear()
        {
            throw new NotSupportedException("Collection was of a fixed size.");
        }

        public bool Contains(T item)
        {
            T[] array = Unsafe.As<T[]>(this);
            var comparer = EqualityComparer<T>.Default;
            for (int i = 0; i < array.Length; i++)
            {
                if (comparer.Equals(array[i], item))
                    return true;
            }
            return false;
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            Array.Copy(Unsafe.As<T[]>(this), 0, array, arrayIndex, this.Length);
        }

        public bool Remove(T item)
        {
            throw new NotSupportedException("Collection was of a fixed size.");
        }

        public T this[int index]
        {
            get
            {
                T[] array = Unsafe.As<T[]>(this);
                if ((uint)index >= (uint)array.Length)
                    throw new ArgumentOutOfRangeException(nameof(index));
                return array[index];
            }
            set
            {
                T[] array = Unsafe.As<T[]>(this);
                if ((uint)index >= (uint)array.Length)
                    throw new ArgumentOutOfRangeException(nameof(index));
                array[index] = value;
            }
        }

        public int IndexOf(T item)
        {
            T[] array = Unsafe.As<T[]>(this);
            var comparer = EqualityComparer<T>.Default;
            for (int i = 0; i < array.Length; i++)
            {
                if (comparer.Equals(array[i], item))
                    return i;
            }
            return -1;
        }

        public void Insert(int index, T item)
        {
            throw new NotSupportedException("Collection was of a fixed size.");
        }

        public void RemoveAt(int index)
        {
            throw new NotSupportedException("Collection was of a fixed size.");
        }

        // ---- ICloneable and the non-generic ICollection and IList, as System.Array has them ----

        object ICloneable.Clone() => Clone();

        int ICollection.Count => this.Length;
        object ICollection.SyncRoot => this;
        bool ICollection.IsSynchronized => false;
        void ICollection.CopyTo(Array array, int index) => Array.Copy(this, 0, array, index, this.Length);

        bool IList.IsReadOnly => false;
        bool IList.IsFixedSize => true;

        object? IList.this[int index]
        {
            get
            {
                T[] array = Unsafe.As<T[]>(this);
                if ((uint)index >= (uint)array.Length)
                    throw new IndexOutOfRangeException();
                return array[index];
            }
            set => SetValue(value, index);
        }

        int IList.Add(object? value) => throw new NotSupportedException("Collection was of a fixed size.");
        bool IList.Contains(object? value) => Array.IndexOf(this, value) >= 0;
        void IList.Clear() => Array.Clear(Unsafe.As<T[]>(this), 0, this.Length);
        int IList.IndexOf(object? value) => Array.IndexOf(this, value);
        void IList.Insert(int index, object? value) => throw new NotSupportedException("Collection was of a fixed size.");
        void IList.Remove(object? value) => throw new NotSupportedException("Collection was of a fixed size.");
        void IList.RemoveAt(int index) => throw new NotSupportedException("Collection was of a fixed size.");

        private sealed class ArrayEnumerator : IEnumerator<T>
        {
            private readonly T[] _array;
            private readonly int _endIndex;
            private int _index;

            internal ArrayEnumerator(T[] array, int endIndex)
            {
                _array = array;
                _endIndex = endIndex;
                _index = -1;
            }

            public bool MoveNext()
            {
                if (_index < _endIndex)
                {
                    _index++;
                    return _index < _endIndex;
                }
                return false;
            }

            public T Current
            {
                get
                {
                    if ((uint)_index >= (uint)_endIndex)
                        throw new InvalidOperationException("Enumeration has either not started or has already finished.");
                    return _array[_index];
                }
            }

            object IEnumerator.Current => Current;

            public void Reset() => _index = -1;

            public void Dispose() { }
        }
    }
}
