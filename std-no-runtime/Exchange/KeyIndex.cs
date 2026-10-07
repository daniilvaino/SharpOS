using System;

namespace SharpOS.Std.Exchange
{
    /// <summary>
    /// A fixed set of values found by 64-bit key in O(1), without a
    /// dictionary (step195): a perfect hash — one multiplication, one shift,
    /// one comparison. Built once; lookups allocate nothing.
    /// </summary>
    public sealed class KeyIndex<T> where T : class
    {
        private ulong[] _keys;
        private T[] _values;
        private ulong _multiplier;
        private int _shift;

        private KeyIndex() { }

        /// <summary>An index of <paramref name="values"/> under <paramref name="keys"/> (distinct).</summary>
        public static KeyIndex<T> Build(ulong[] keys, T[] values)
        {
            int count = keys.Length;
            int bits = 4;
            while ((1 << bits) < 2 * count) bits++;
            for (; bits < 24; bits++)
            {
                ulong multiplier = 0x9E3779B97F4A7C15UL;
                for (int attempt = 0; attempt < 64; attempt++)
                {
                    int shift = 64 - bits;
                    var slotKeys = new ulong[1 << bits];
                    var slotValues = new T[1 << bits];
                    bool clash = false;
                    for (int i = 0; i < count && !clash; i++)
                    {
                        int at = (int)((keys[i] * multiplier) >> shift);
                        if (slotValues[at] != null) clash = true;
                        else
                        {
                            slotKeys[at] = keys[i];
                            slotValues[at] = values[i];
                        }
                    }
                    if (!clash)
                        return new KeyIndex<T> { _keys = slotKeys, _values = slotValues, _multiplier = multiplier, _shift = shift };
                    multiplier = (multiplier * 6364136223846793005UL + 1442695040888963407UL) | 1;
                }
            }
            throw new InvalidOperationException("no perfect hash for the keys");
        }

        /// <summary>The value under <paramref name="key"/>; null when there is none.</summary>
        public T Find(ulong key)
        {
            int at = (int)((key * _multiplier) >> _shift);
            return _keys[at] == key ? _values[at] : null;
        }
    }
}
