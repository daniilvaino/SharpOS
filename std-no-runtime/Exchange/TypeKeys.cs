using System;
using System.Collections.Generic;

namespace SharpOS.Std.Exchange
{
    /// <summary>
    /// Type keys: what a region carries in an object's table word instead of a
    /// pointer into the producer's image.
    /// </summary>
    /// <remarks>
    /// The MethodTable's own hash cannot be the key: ILC computes it from the
    /// namespace and name alone, and ordinary names collide (App.Msg10 and
    /// App.Msg32 give one number). The key here is a 64-bit FNV-1a over the name
    /// AND the layout fingerprint — base size, component size, GCDesc series,
    /// and every field as name:type@offset.
    ///
    /// Computed by each image from its OWN table and its own measured offsets,
    /// so two images agree on a key exactly when they agree on the layout. A
    /// generator would emit the keys as literals and let each image check its
    /// layout against them; this registry is the runtime half of that, enough to
    /// run the experiments without the generator.
    ///
    /// Keys are odd and non-canonical. An odd value where the runtime expects a
    /// table pointer can never be taken for one: a cast or a virtual call on an
    /// untranslated object faults instead of answering quietly. Non-canonical
    /// tells a key from a table carrying a collector's mark in bit 0.
    /// </remarks>
    public static unsafe class TypeKeys
    {
        /// <summary>A named field and its offset from the start of the object (its table word).</summary>
        /// <remarks>
        /// <see cref="Enum"/> names the enum a field (or an array's elements)
        /// is declared as; <see cref="Type"/> is then its underlying number.
        /// Description only — not in the key: the layout is the number's.
        /// </remarks>
        public readonly struct Field
        {
            public readonly string Name;
            public readonly string Type;
            public readonly int Offset;
            public readonly string Enum;

            public Field(string name, string type, int offset) { Name = name; Type = type; Offset = offset; Enum = null; }

            public Field(string name, string type, int offset, string enumType)
            {
                Name = name; Type = type; Offset = offset; Enum = enumType;
            }
        }

        /// <summary>What a reader needs to print an object it has no type for.</summary>
        public sealed class Description
        {
            public ulong Key;
            public string Name;
            public uint BaseSize;
            public ushort ComponentSize;
            public bool IsValueType;
            public Field[] Fields;

            /// <summary>An enum's members, names and values; null for anything else. Description only.</summary>
            public string[] EnumNames;
            public long[] EnumValues;
        }

        /// <summary>Attaches an enum's member names to its declared description (not to its key).</summary>
        public static void DescribeEnum(ulong key, string[] names, long[] values)
        {
            Description d = DescriptionOf(key);
            if (d == null) return;
            d.EnumNames = names;
            d.EnumValues = values;
        }

        private sealed class RawObject { public byte Data; }

        private static Dictionary<ulong, ulong> s_tableToKey;
        private static Dictionary<ulong, ulong> s_keyToTable;
        private static List<Description> s_declared;

        public static int Count => s_keyToTable == null ? 0 : s_keyToTable.Count;

        /// <summary>Where this type's statics live: for the root-coverage detector.</summary>
        public static nint StaticsAddress => (nint)System.Runtime.CompilerServices.Unsafe.AsPointer(ref s_declared);

        /// <summary>Every declared type, in declaration order.</summary>
        public static List<Description> Declared => s_declared ??= new List<Description>();

        /// <summary>A field's offset from the object's address, without reflection.</summary>
        /// <remarks>
        /// Both refs are interior and the collectors here do not move objects, so
        /// the difference is stable. The first field of any class lies right after
        /// the table word, hence +8.
        /// </remarks>
        public static int Offset<T>(object owner, ref T field)
            => (int)System.Runtime.CompilerServices.Unsafe.ByteOffset(
                   ref System.Runtime.CompilerServices.Unsafe.As<RawObject>(owner).Data,
                   ref System.Runtime.CompilerServices.Unsafe.As<T, byte>(ref field)) + 8;

        /// <summary>A field's offset inside a boxed struct, measured on a stack copy.</summary>
        /// <remarks>Unsafe.Unbox is a stub here; a box's data lies right after the table word.</remarks>
        public static int StructOffset<TStruct, TField>(ref TStruct value, ref TField field)
            => (int)System.Runtime.CompilerServices.Unsafe.ByteOffset(
                   ref System.Runtime.CompilerServices.Unsafe.As<TStruct, byte>(ref value),
                   ref System.Runtime.CompilerServices.Unsafe.As<TField, byte>(ref field)) + 8;

        /// <summary>
        /// Declares a type by a witness instance: its table gets a key, and the key a table.
        /// </summary>
        /// <returns>The key; 0 when the key or the table is already taken under another name.</returns>
        public static ulong Declare(string name, object witness, Field[] fields)
            => Declare(name, ObjectLayout.TableOf(AddressOf(witness)), fields);

        /// <summary>The same, by the type's table.</summary>
        public static ulong Declare(string name, ulong table, Field[] fields)
        {
            s_tableToKey ??= new Dictionary<ulong, ulong>();
            s_keyToTable ??= new Dictionary<ulong, ulong>();

            ulong key = KeyOf(name, table, fields);
            if (s_tableToKey.TryGetValue(table, out ulong existingKey))
                return existingKey == key ? key : 0;
            if (s_keyToTable.ContainsKey(key))
                return 0;

            s_tableToKey[table] = key;
            s_keyToTable[key] = table;
            Declared.Add(new Description
            {
                Key = key,
                Name = name,
                BaseSize = ObjectLayout.BaseSizeOf(table),
                ComponentSize = ObjectLayout.ComponentSizeOf(table),
                IsValueType = ((SharpOS.Std.NoRuntime.GcMethodTable*)table)->IsValueType,
                Fields = fields ?? new Field[0],
            });
            return key;
        }

        /// <summary>The description declared under a key; null when none.</summary>
        public static Description DescriptionOf(ulong key)
        {
            List<Description> all = Declared;
            for (int i = 0; i < all.Count; i++)
                if (all[i].Key == key) return all[i];
            return null;
        }

        /// <summary>FNV-1a over the name and the layout fingerprint, low bit set.</summary>
        public static ulong KeyOf(string name, ulong table, Field[] fields)
        {
            ulong h = 0xCBF29CE484222325UL;
            h = Mix(h, name);
            h = Mix(h, ObjectLayout.BaseSizeOf(table));
            h = Mix(h, ObjectLayout.ComponentSizeOf(table));
            if (ObjectLayout.HasPointers(table))
            {
                // Raw series, not offsets: for an array of references the offsets
                // depend on the length, the series do not.
                long n = ((long*)table)[-1];
                h = Mix(h, (ulong)n);
                long words = n > 0 ? 2 * n : n < 0 ? 1 - n : 0;
                if (words > 130) words = 130;
                for (long i = 2; i <= words + 1; i++)
                    h = Mix(h, (ulong)((long*)table)[-i]);
            }
            if (fields != null)
                foreach (Field f in fields)
                {
                    h = Mix(h, f.Name);
                    h = Mix(h, f.Type);
                    h = Mix(h, (ulong)f.Offset);
                }
            return (h & 0x3FFF_FFFF_FFFF_FFFFUL) | KeyTopBits | 1;
        }

        // Bits 63:62 = 10: never a canonical address. A collector's mark is
        // bit 0 of a real table pointer — odd too, but canonical once the bit
        // is cleared — so the two cannot be confused (IsKeyWord).
        private const ulong KeyTopBits = 0x8000_0000_0000_0000UL;

        /// <summary>
        /// True when a table word holds a type key rather than a table, marked
        /// or not: odd, and not canonical.
        /// </summary>
        public static bool IsKeyWord(ulong word)
            => (word & 1) != 0 && (ulong)((long)(word << 16) >> 16) != word;

        public static bool TryKey(ulong table, out ulong key)
        {
            key = 0;
            return s_tableToKey != null && s_tableToKey.TryGetValue(table, out key);
        }

        public static bool TryTable(ulong key, out ulong table)
        {
            table = 0;
            return s_keyToTable != null && s_keyToTable.TryGetValue(key, out table);
        }

        private static ulong Mix(ulong h, string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                h ^= s[i];
                h *= 0x100000001B3UL;
            }
            h ^= '|';
            h *= 0x100000001B3UL;
            return h;
        }

        private static ulong Mix(ulong h, ulong v)
        {
            for (int i = 0; i < 8; i++)
            {
                h ^= (byte)(v >> (8 * i));
                h *= 0x100000001B3UL;
            }
            return h;
        }

        private static ulong AddressOf(object o)
            => System.Runtime.CompilerServices.Unsafe.As<object, ulong>(ref o);
    }
}
