using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using SharpOS.Std.Exchange;

namespace SharpOS.Std.Pipes
{
    /// <summary>
    /// A set of named values with no class of its own (pipe spec Р31, Р32): a
    /// filter builds one from the fields of another object, a parser emits
    /// them. Access is by name through the indexer, or as `dynamic`.
    /// </summary>
    /// <remarks>
    /// It is in std, so every image has the same type under the same key: it
    /// travels through a native pipe like any [Message] type, and every reader
    /// gets a real Expando. Names keep the order they were added in and are
    /// compared ordinally.
    ///
    /// A value is null or of a type in the pipe catalog — a string, a number,
    /// an array of those, a std structure (DateTime, TimeSpan, Guid,
    /// ConsoleKeyInfo, Vector3), another Expando, any [Message] type. Anything
    /// else is refused when it is set, where the mistake is, not later when
    /// the Expando is sent.
    ///
    /// Layout (step201, pipe_plan.md «Умный Expando»): a table of entries in
    /// the order they were added and buckets of a hash of the names — the
    /// shape of OrderedDictionary, and the same in the heap and in a region.
    /// An entry keeps the name's hash, the next entry of its chain, the
    /// value's kind, a scalar and a reference in separate fields: a number,
    /// bool or char is not boxed, and the collector sees the reference as an
    /// ordinary field of a struct. A removed entry is unlinked from its chain
    /// and marked dead; its cell is not reused (that would break the order),
    /// the table is rebuilt from the live entries when it is full. The hash
    /// is part of the format (ExpandoHash): a reader finds a name in a region
    /// without copying anything.
    ///
    /// Read from a region it answers in place. Writing into it there follows
    /// the region's rule: a scalar, or a value already in the region, or
    /// null; anything new is refused by the write barrier
    /// (RegionReferenceException): take ToHeap, change, Copy.
    ///
    /// Not System.Dynamic.ExpandoObject: no DLR here (`dynamic` binds through
    /// SharpOS.Std.Dynamic), and the name stays out of System because the
    /// behaviour is not that type's.
    /// </remarks>
    [Message]
    public sealed partial class Expando : IDictionary<string, object>
    {
        private ExpandoEntry[] _entries;
        private int[] _buckets;     // 1-based index of a chain's first entry, 0 for none
        private int _count;         // entries used, live and dead, in order
        private int _live;

        public Expando()
        {
        }

        // Room for this many entries (a copy out of a region: ViewCopy).
        internal Expando(int capacity)
        {
            if (capacity > 0) Rebuild(capacity);
        }

        // An entry whose name is new and whose value is known to travel: a
        // copy out of a region (ViewCopy) — names of a type's fields are
        // distinct, and every value came out of a pipe.
        internal void AppendFresh(string name, object value) => Append(name, ExpandoHash.Of(name), value);

        /// <summary>Fields in the set.</summary>
        public int Count => _live;

        /// <summary>The value under <paramref name="name"/>; KeyNotFoundException when there is none.</summary>
        public object this[string name]
        {
            get
            {
                int i = IndexOf(name);
                if (i < 0) throw new KeyNotFoundException("no field '" + name + "' in the Expando");
                return ValueAt(i);
            }
            set => Set(name, value);
        }

        /// <summary>The names, in the order they were added. A copy.</summary>
        public ICollection<string> Keys
        {
            get
            {
                var names = new List<string>(_live);
                for (int i = 0; i < _count; i++)
                    if (_entries[i].Dead == 0) names.Add(_entries[i].Name);
                return names;
            }
        }

        /// <summary>The values, in the order of their names. A copy.</summary>
        public ICollection<object> Values
        {
            get
            {
                var values = new List<object>(_live);
                for (int i = 0; i < _count; i++)
                    if (_entries[i].Dead == 0) values.Add(ValueAt(i));
                return values;
            }
        }

        public bool IsReadOnly => false;

        public bool ContainsKey(string name) => IndexOf(name) >= 0;

        public bool TryGetValue(string name, out object value)
        {
            int i = IndexOf(name);
            value = i >= 0 ? ValueAt(i) : null;
            return i >= 0;
        }

        /// <summary>Adds a field; ArgumentException when the name is taken (the indexer replaces instead).</summary>
        public void Add(string name, object value)
        {
            if (IndexOf(name) >= 0) throw new ArgumentException("a field '" + name + "' is already in the Expando", nameof(name));
            Set(name, value);
        }

        public bool Remove(string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (_live == 0) return false;
            int hash = ExpandoHash.Of(name);
            int bucket = ExpandoHash.Bucket(hash, _buckets.Length);
            int previous = 0;
            for (int at = _buckets[bucket]; at != 0; previous = at, at = _entries[at - 1].Next)
            {
                ref ExpandoEntry e = ref _entries[at - 1];
                if (e.Hash != hash || e.Name != name) continue;
                if (previous == 0) _buckets[bucket] = e.Next;
                else _entries[previous - 1].Next = e.Next;
                e.Next = 0;
                e.Dead = 1;
                e.Name = null;
                e.Ref = null;
                e.Bits = 0;
                _live--;
                if (_live == 0) Clear();
                return true;
            }
            return false;
        }

        public void Clear()
        {
            if (_entries != null)
            {
                for (int i = 0; i < _count; i++) _entries[i] = default;
                for (int i = 0; i < _buckets.Length; i++) _buckets[i] = 0;
            }
            _count = 0;
            _live = 0;
        }

        void ICollection<KeyValuePair<string, object>>.Add(KeyValuePair<string, object> item) => Add(item.Key, item.Value);

        bool ICollection<KeyValuePair<string, object>>.Contains(KeyValuePair<string, object> item)
            => TryGetValue(item.Key, out object value) && Equals(value, item.Value);

        bool ICollection<KeyValuePair<string, object>>.Remove(KeyValuePair<string, object> item)
            => TryGetValue(item.Key, out object value) && Equals(value, item.Value) && Remove(item.Key);

        public void CopyTo(KeyValuePair<string, object>[] array, int arrayIndex)
        {
            if (array == null) throw new ArgumentNullException(nameof(array));
            if (arrayIndex < 0 || arrayIndex > array.Length - _live) throw new ArgumentOutOfRangeException(nameof(arrayIndex));
            for (int i = 0; i < _count; i++)
                if (_entries[i].Dead == 0)
                    array[arrayIndex++] = new KeyValuePair<string, object>(_entries[i].Name, ValueAt(i));
        }

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
        {
            for (int i = 0; i < _count; i++)
                if (_entries[i].Dead == 0)
                    yield return new KeyValuePair<string, object>(_entries[i].Name, ValueAt(i));
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary><c>{ Name = value, … }</c>, nested Expandos the same way.</summary>
        public override string ToString()
        {
            var text = new System.Text.StringBuilder();
            text.Append("{ ");
            bool first = true;
            for (int i = 0; i < _count; i++)
            {
                if (_entries[i].Dead != 0) continue;
                if (!first) text.Append(", ");
                first = false;
                text.Append(_entries[i].Name).Append(" = ");
                object value = ValueAt(i);
                if (value == null) text.Append("null");
                else if (value is string s) text.Append('"').Append(s).Append('"');
                else text.Append(value.ToString());
            }
            text.Append(" }");
            return text.ToString();
        }

        private int IndexOf(string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            return IndexOf(name, ExpandoHash.Of(name));
        }

        private int IndexOf(string name, int hash)
        {
            if (_live == 0) return -1;
            for (int at = _buckets[ExpandoHash.Bucket(hash, _buckets.Length)]; at != 0; at = _entries[at - 1].Next)
            {
                ref ExpandoEntry e = ref _entries[at - 1];
                if (e.Hash == hash && e.Name == name) return at - 1;
            }
            return -1;
        }

        private void Set(string name, object value)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            int hash = ExpandoHash.Of(name);
            int i = IndexOf(name, hash);
            if (i >= 0)
            {
                Store(ref _entries[i], value);
                return;
            }
            Append(name, hash, value);
        }

        private void Append(string name, int hash, object value)
        {
            if (_entries == null || _count == _entries.Length)
                Rebuild(_live < 2 ? 4 : _live * 2);
            ref ExpandoEntry e = ref _entries[_count];
            e.Hash = hash;
            e.Name = name;
            e.Dead = 0;
            Store(ref e, value);
            int bucket = ExpandoHash.Bucket(hash, _buckets.Length);
            e.Next = _buckets[bucket];
            _buckets[bucket] = _count + 1;
            _count++;
            _live++;
        }

        // A table of this many cells holding the live entries in their order;
        // the dead ones end here.
        private void Rebuild(int capacity)
        {
            var entries = new ExpandoEntry[capacity];
            var buckets = new int[ExpandoHash.BucketCount(capacity)];
            int n = 0;
            for (int i = 0; i < _count; i++)
            {
                if (_entries[i].Dead != 0) continue;
                entries[n] = _entries[i];
                int bucket = ExpandoHash.Bucket(entries[n].Hash, buckets.Length);
                entries[n].Next = buckets[bucket];
                buckets[bucket] = n + 1;
                n++;
            }
            _entries = entries;
            _buckets = buckets;
            _count = n;
        }

        // The reference first: in a region the write barrier refuses a value
        // from outside the block on that store, and nothing else of the entry
        // has changed by then. A scalar needs no barrier at all — the region
        // takes it in place.
        private void Store(ref ExpandoEntry e, object value)
        {
            if (ExpandoValues.TryScalar(value, out byte kind, out ulong bits))
            {
                e.Bits = bits;
                e.Kind = kind;
                e.Ref = null;
                return;
            }
            CheckTravels(value);
            e.Ref = value;
            e.Bits = 0;
            e.Kind = (byte)FieldKind.Reference;
        }

        private object ValueAt(int i)
        {
            ref ExpandoEntry e = ref _entries[i];
            return e.Kind == (byte)FieldKind.Reference ? e.Ref : ExpandoValues.Box(e.Kind, e.Bits);
        }

        private static unsafe void CheckTravels(object value)
        {
            if (value == null) return;
            ulong table = ObjectLayout.TableOf(Unsafe.As<object, ulong>(ref value));
            if (!MessageCatalog.Contains(table))
                throw new ArgumentException("the value cannot travel through a pipe: its type is not in the catalog", nameof(value));
        }
    }

    /// <summary>
    /// An entry of an Expando's table (step201). Kind is a <see cref="FieldKind"/>:
    /// a scalar kind with its value in Bits, or Reference with it in Ref (null
    /// included). Next is the 1-based index of the next entry of the chain.
    /// </summary>
    [Message]
    internal struct ExpandoEntry
    {
        public int Hash;
        public int Next;
        public byte Kind;
        public byte Dead;
        public ulong Bits;
        public string Name;
        public object Ref;
    }

    /// <summary>
    /// The hash of an Expando's names — part of the format: every image, the
    /// heap and a region compute the same. FNV-1a over the UTF-16 code units,
    /// no seed; a bucket is the hash mixed (murmur's finalizer) and masked.
    /// </summary>
    internal static class ExpandoHash
    {
        public static int Of(string name) => Of(name.AsSpan());

        public static int Of(ReadOnlySpan<char> name)
        {
            uint h = 2166136261;
            for (int i = 0; i < name.Length; i++)
            {
                h ^= name[i];
                h *= 16777619;
            }
            return (int)h;
        }

        /// <summary>A power of two not below <paramref name="capacity"/>.</summary>
        public static int BucketCount(int capacity)
        {
            int n = 4;
            while (n < capacity) n <<= 1;
            return n;
        }

        public static int Bucket(int hash, int count)
        {
            uint h = (uint)hash;
            h ^= h >> 16;
            h *= 0x85EBCA6B;
            h ^= h >> 13;
            return (int)(h & (uint)(count - 1));
        }
    }

    /// <summary>Scalars of an Expando entry: which values are kept unboxed, and their bits.</summary>
    internal static class ExpandoValues
    {
        // Exactly the primitive types: an enum keeps its box (its type travels
        // with it), as does every struct.
        public static bool TryScalar(object value, out byte kind, out ulong bits)
        {
            switch (value)
            {
                case bool v: kind = (byte)FieldKind.Bool; bits = v ? 1UL : 0UL; return true;
                case char v: kind = (byte)FieldKind.Char; bits = v; return true;
                case sbyte v: kind = (byte)FieldKind.SByte; bits = (ulong)(long)v; return true;
                case byte v: kind = (byte)FieldKind.Byte; bits = v; return true;
                case short v: kind = (byte)FieldKind.Int16; bits = (ulong)(long)v; return true;
                case ushort v: kind = (byte)FieldKind.UInt16; bits = v; return true;
                case int v: kind = (byte)FieldKind.Int32; bits = (ulong)(long)v; return true;
                case uint v: kind = (byte)FieldKind.UInt32; bits = v; return true;
                case long v: kind = (byte)FieldKind.Int64; bits = (ulong)v; return true;
                case ulong v: kind = (byte)FieldKind.UInt64; bits = v; return true;
                case float v: kind = (byte)FieldKind.Single; bits = (uint)BitConverter.SingleToInt32Bits(v); return true;
                case double v: kind = (byte)FieldKind.Double; bits = (ulong)BitConverter.DoubleToInt64Bits(v); return true;
            }
            kind = (byte)FieldKind.Reference;
            bits = 0;
            return false;
        }

        // The same for a value whose type is known where it is called: for a
        // value type the compiler keeps one branch (a box tested against a
        // known type folds away), and nothing is boxed.
        public static bool TryScalar<T>(T value, out byte kind, out ulong bits) where T : unmanaged
        {
            if (value is bool b) { kind = (byte)FieldKind.Bool; bits = b ? 1UL : 0UL; return true; }
            if (value is char c) { kind = (byte)FieldKind.Char; bits = c; return true; }
            if (value is sbyte sb) { kind = (byte)FieldKind.SByte; bits = (ulong)(long)sb; return true; }
            if (value is byte by) { kind = (byte)FieldKind.Byte; bits = by; return true; }
            if (value is short s) { kind = (byte)FieldKind.Int16; bits = (ulong)(long)s; return true; }
            if (value is ushort us) { kind = (byte)FieldKind.UInt16; bits = us; return true; }
            if (value is int i) { kind = (byte)FieldKind.Int32; bits = (ulong)(long)i; return true; }
            if (value is uint ui) { kind = (byte)FieldKind.UInt32; bits = ui; return true; }
            if (value is long l) { kind = (byte)FieldKind.Int64; bits = (ulong)l; return true; }
            if (value is ulong ul) { kind = (byte)FieldKind.UInt64; bits = ul; return true; }
            if (value is float f) { kind = (byte)FieldKind.Single; bits = (uint)BitConverter.SingleToInt32Bits(f); return true; }
            if (value is double d) { kind = (byte)FieldKind.Double; bits = (ulong)BitConverter.DoubleToInt64Bits(d); return true; }
            kind = (byte)FieldKind.Reference;
            bits = 0;
            return false;
        }

        public static object Box(byte kind, ulong bits)
        {
            switch ((FieldKind)kind)
            {
                case FieldKind.Bool: return bits != 0;
                case FieldKind.Char: return (char)bits;
                case FieldKind.SByte: return (sbyte)bits;
                case FieldKind.Byte: return (byte)bits;
                case FieldKind.Int16: return (short)bits;
                case FieldKind.UInt16: return (ushort)bits;
                case FieldKind.Int32: return (int)bits;
                case FieldKind.UInt32: return (uint)bits;
                case FieldKind.Int64: return (long)bits;
                case FieldKind.UInt64: return bits;
                case FieldKind.Single: return BitConverter.Int32BitsToSingle((int)bits);
                case FieldKind.Double: return BitConverter.Int64BitsToDouble((long)bits);
            }
            throw new InvalidOperationException("an Expando entry of kind " + kind.ToString() + " has no scalar");
        }
    }
}
