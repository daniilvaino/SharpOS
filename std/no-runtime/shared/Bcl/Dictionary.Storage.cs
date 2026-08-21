// The storage half of Dictionary<TKey, TValue>: buckets of indices over a
// dense array of entries.
//
// Ported in structure from dotnet/runtime v8.0 (MIT),
//   src/libraries/System.Private.CoreLib/src/System/Collections/Generic/Dictionary.cs
// keeping its layout, its free-list encoding and its resize, and dropping what
// this environment has no use for: serialization, the non-generic IDictionary
// surface, randomized string hashing, and the ref-local fast paths.
//
// Why the layout matters — it is not an optimisation. The previous port was
// dotnet/runtime's LowLevelDictionary, a chain-per-bucket design whose
// enumeration comes out in HASH order. The real Dictionary walks its entries
// array, so enumeration follows insertion, and a great deal of code written
// against the BCL depends on that without ever saying so. Terminal.Gui does:
// its TreeView keeps root nodes in a Dictionary and renders them in whatever
// order the enumerator gives, so on the old storage a carefully sorted list of
// folders came out shuffled and the "up one level" row landed in the middle.
//
// That is exactly the kind of divergence the naming rule exists to prevent: a
// type under the canonical name has to behave like the canonical type, and
// "same API, different order" is not a detail — it is a bug in whatever
// trusted the name.

namespace System.Collections.Generic
{
    public partial class Dictionary<TKey, TValue>
    {
        private struct Entry
        {
            public uint hashCode;

            /// <summary>
            /// Index of the next entry in the chain, -1 at the end.
            /// </summary>
            /// <remarks>
            /// Also marks free entries, by sign: -2 is the end of the free
            /// list, -3 means index 0 is free, -4 index 1, and so on. Verbatim
            /// from the BCL, because the encoding is what lets one field serve
            /// both lists.
            /// </remarks>
            public int next;

            public TKey key;
            public TValue value;
        }

        private int[]? _buckets;
        private Entry[]? _entries;

        /// <summary>One past the highest entry ever used, free slots included.</summary>
        private int _count;

        private int _freeList;
        private int _freeCount;

        private const int StartOfFreeList = -3;

        private int Initialize(int capacity)
        {
            int size = HashHelpers.GetPrime(capacity);

            _freeList = -1;
            _buckets = new int[size];
            _entries = new Entry[size];

            return size;
        }

        /// <summary>The bucket slot for a hash. Buckets hold index+1, so 0 means empty.</summary>
        private ref int GetBucket(uint hashCode)
        {
            int[] buckets = _buckets!;
            return ref buckets[(int)(hashCode % (uint)buckets.Length)];
        }

        private int FindEntry(TKey key)
        {
            if (key == null) ThrowKeyNull();
            if (_buckets == null) return -1;

            Entry[] entries = _entries!;
            uint hashCode = (uint)_comparer.GetHashCode(key);
            int i = GetBucket(hashCode) - 1;

            // Bounded by the entry count: a corrupted chain would otherwise
            // loop forever rather than fail.
            int remaining = entries.Length;
            while ((uint)i < (uint)entries.Length)
            {
                if (entries[i].hashCode == hashCode && _comparer.Equals(entries[i].key, key))
                    return i;

                i = entries[i].next;
                if (--remaining < 0) break;
            }

            return -1;
        }

        private bool TryInsert(TKey key, TValue value, bool overwrite)
        {
            if (key == null) ThrowKeyNull();
            if (_buckets == null) Initialize(0);

            Entry[] entries = _entries!;
            uint hashCode = (uint)_comparer.GetHashCode(key);

            int existing = FindEntry(key);
            if (existing >= 0)
            {
                if (!overwrite) return false;

                entries[existing].value = value;
                return true;
            }

            int index;
            if (_freeCount > 0)
            {
                index = _freeList;
                _freeList = StartOfFreeList - entries[_freeList].next;
                _freeCount--;
            }
            else
            {
                if (_count == entries.Length)
                {
                    Resize();
                    entries = _entries!;
                }
                index = _count;
                _count++;
            }

            ref int bucket = ref GetBucket(hashCode);
            entries[index].hashCode = hashCode;
            entries[index].next = bucket - 1;
            entries[index].key = key;
            entries[index].value = value;
            bucket = index + 1;

            _version++;
            return true;
        }

        private void Resize()
        {
            int newSize = HashHelpers.ExpandPrime(_count);

            var entries = new Entry[newSize];
            Array.Copy(_entries!, entries, _count);

            _buckets = new int[newSize];
            _entries = entries;

            // Chains are rebuilt rather than moved: bucket membership depends
            // on the new size.
            for (int i = 0; i < _count; i++)
            {
                if (entries[i].next < -1) continue;   // on the free list

                ref int bucket = ref GetBucket(entries[i].hashCode);
                entries[i].next = bucket - 1;
                bucket = i + 1;
            }
        }

        private bool RemoveEntry(TKey key)
        {
            if (key == null) ThrowKeyNull();
            if (_buckets == null) return false;

            Entry[] entries = _entries!;
            uint hashCode = (uint)_comparer.GetHashCode(key);

            ref int bucket = ref GetBucket(hashCode);
            int last = -1;
            int i = bucket - 1;

            while (i >= 0)
            {
                if (entries[i].hashCode == hashCode && _comparer.Equals(entries[i].key, key))
                {
                    if (last < 0) bucket = entries[i].next + 1;
                    else entries[last].next = entries[i].next;

                    // Marked free rather than compacted: moving entries would
                    // change everyone else's index, and the index is what the
                    // buckets point at.
                    entries[i].next = StartOfFreeList - _freeList;
                    entries[i].key = default!;
                    entries[i].value = default!;

                    _freeList = i;
                    _freeCount++;
                    _version++;
                    return true;
                }

                last = i;
                i = entries[i].next;
            }

            return false;
        }
    }

    /// <summary>
    /// Prime sizes for the table. From dotnet/runtime's HashHelpers (MIT), cut
    /// to the table and the two lookups.
    /// </summary>
    internal static class HashHelpers
    {
        public const int MaxPrimeArrayLength = 0x7FFFFFC3;
        private const int HashPrime = 101;

        public static bool IsPrime(int candidate)
        {
            if ((candidate & 1) == 0) return candidate == 2;

            // divisor <= candidate/divisor instead of a square root: Math.Sqrt
            // is a floating-point intrinsic, and sizing a hash table wants
            // integers.
            for (int divisor = 3; divisor <= candidate / divisor; divisor += 2)
                if ((candidate % divisor) == 0) return false;

            return true;
        }

        public static int GetPrime(int min)
        {
            // The published table, then a search. Primes keep the modulo from
            // clustering when hashes share a factor with the table size.
            int[] primes = Primes;
            for (int i = 0; i < primes.Length; i++)
                if (primes[i] >= min) return primes[i];

            for (int i = min | 1; i < int.MaxValue; i += 2)
                if (IsPrime(i) && ((i - 1) % HashPrime != 0)) return i;

            return min;
        }

        public static int ExpandPrime(int oldSize)
        {
            int newSize = 2 * oldSize;
            if ((uint)newSize > MaxPrimeArrayLength && MaxPrimeArrayLength > oldSize)
                return MaxPrimeArrayLength;

            return GetPrime(newSize);
        }

        // A property rather than a static field: a static array field with an
        // initialiser gives this type a class constructor, and the check ILC
        // emits for one does not work here (limits §1).
        private static int[] Primes => new int[]
        {
            3, 7, 11, 17, 23, 29, 37, 47, 59, 71, 89, 107, 131, 163, 197, 239, 293, 353, 431, 521, 631, 761, 919,
            1103, 1327, 1597, 1931, 2333, 2801, 3371, 4049, 4861, 5839, 7013, 8419, 10103, 12143, 14591,
            17519, 21023, 25229, 30293, 36353, 43627, 52361, 62851, 75431, 90523, 108631, 130363, 156437,
            187751, 225307, 270371, 324449, 389357, 467237, 560689, 672827, 807403, 968897, 1162687, 1395263,
            1674319, 2009191, 2411033, 2893249, 3471899, 4166287, 4999559, 5999471, 7199369,
        };
    }
}
