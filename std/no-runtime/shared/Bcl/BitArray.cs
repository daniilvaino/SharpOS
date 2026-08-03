// Ported from dotnet/runtime v8.0.27:
//   src/libraries/System.Collections/src/System/Collections/BitArray.cs (MIT)
//
// Cuts vs original:
//   - And/Or/Xor/Not and CopyTo — their fast paths are Vector128/Vector256
//     (Ssse3/Avx2/AdvSimd) which we do not have; the scalar fallbacks alone
//     were not worth carrying until a caller appears.
//   - ctor(bool[]) / ctor(byte[]) / ctor(int[]) / ctor(BitArray), Clone,
//     ICollection / ICloneable / IEnumerable and BitArrayEnumeratorSimple —
//     no caller yet.
//   - [Serializable] / TypeForwardedFrom, _version bump + concurrent-mod
//     detection (the enumerator that used it is cut).
//   - SR.* message lookups — throw the same exception types with literals.
// Field names (m_array/m_length) and the bit arithmetic are unchanged.

using System.Runtime.CompilerServices;

namespace System.Collections
{
    // A vector of bits.  Use this to store bits efficiently, without having to do bit
    // shifting yourself.
    public sealed class BitArray
    {
        private int[] m_array; // Do not rename (binary serialization)
        private int m_length; // Do not rename (binary serialization)

        private const int _ShrinkThreshold = 256;

        /*=========================================================================
        ** Allocates space to hold length bit values. All of the values in the bit
        ** array are set to false.
        =========================================================================*/
        public BitArray(int length)
            : this(length, false)
        {
        }

        /*=========================================================================
        ** Allocates space to hold length bit values. All of the values in the bit
        ** array are set to defaultValue.
        =========================================================================*/
        public BitArray(int length, bool defaultValue)
        {
            if (length < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            m_array = new int[GetInt32ArrayLengthFromBitLength(length)];
            m_length = length;

            if (defaultValue)
            {
                for (int i = 0; i < m_array.Length; i++)
                {
                    m_array[i] = -1;
                }

                // clear high bit values in the last int
                Div32Rem(length, out int extraBits);
                if (extraBits > 0)
                {
                    m_array[m_array.Length - 1] = (1 << extraBits) - 1;
                }
            }
        }

        public bool this[int index]
        {
            get => Get(index);
            set => Set(index, value);
        }

        /*=========================================================================
        ** Returns the bit value at position index.
        =========================================================================*/
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Get(int index)
        {
            if ((uint)index >= (uint)m_length)
                throw new ArgumentOutOfRangeException(nameof(index));

            return (m_array[index >> 5] & (1 << index)) != 0;
        }

        /*=========================================================================
        ** Sets the bit value at position index to value.
        =========================================================================*/
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(int index, bool value)
        {
            if ((uint)index >= (uint)m_length)
                throw new ArgumentOutOfRangeException(nameof(index));

            int bitMask = 1 << index;
            ref int segment = ref m_array[index >> 5];

            if (value)
            {
                segment |= bitMask;
            }
            else
            {
                segment &= ~bitMask;
            }
        }

        /*=========================================================================
        ** Sets all the bit values to value.
        =========================================================================*/
        public void SetAll(bool value)
        {
            int fillValue = value ? -1 : 0;
            int arrayLength = GetInt32ArrayLengthFromBitLength(Length);
            for (int i = 0; i < arrayLength; i++)
            {
                m_array[i] = fillValue;
            }

            if (value)
            {
                // clear high bit values in the last int
                Div32Rem(m_length, out int extraBits);
                if (extraBits > 0)
                {
                    m_array[arrayLength - 1] &= (1 << extraBits) - 1;
                }
            }
        }

        public int Length
        {
            get
            {
                return m_length;
            }
            set
            {
                if (value < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                int newints = GetInt32ArrayLengthFromBitLength(value);
                if (newints > m_array.Length || newints + _ShrinkThreshold < m_array.Length)
                {
                    // grow or shrink (if wasting more than _ShrinkThreshold ints)
                    var resized = new int[newints];
                    int copy = newints < m_array.Length ? newints : m_array.Length;
                    for (int i = 0; i < copy; i++)
                    {
                        resized[i] = m_array[i];
                    }
                    m_array = resized;
                }

                if (value > m_length)
                {
                    // clear high bit values in the last int
                    int last = (m_length - 1) >> BitShiftPerInt32;
                    Div32Rem(m_length, out int bits);
                    if (bits > 0)
                    {
                        m_array[last] &= (1 << bits) - 1;
                    }

                    // clear remaining int values
                    for (int i = last + 1; i < newints; i++)
                    {
                        m_array[i] = 0;
                    }
                }

                m_length = value;
            }
        }

        public int Count => m_length;

        private const int BitsPerInt32 = 32;

        private const int BitShiftPerInt32 = 5;

        /// <summary>
        /// Used for conversion between different representations of bit array.
        /// Returns (n + (32 - 1)) / 32, rearranged to avoid arithmetic overflow.
        /// For example, in the bit to int case, the straightforward calc would
        /// be (n + 31) / 32, but that would cause overflow. So instead it's
        /// rearranged to ((n - 1) / 32) + 1.
        /// Due to sign extension, we don't need to special case for n == 0, if we use
        /// bitwise operations (since ((n - 1) >> 5) + 1 = 0).
        /// This doesn't hold true for ((n - 1) / 32) + 1, which equals 1.
        /// </summary>
        private static int GetInt32ArrayLengthFromBitLength(int n)
        {
            return (int)((uint)(n - 1 + (1 << BitShiftPerInt32)) >> BitShiftPerInt32);
        }

        private static int Div32Rem(int number, out int remainder)
        {
            uint quotient = (uint)number / 32;
            remainder = number & (32 - 1);    // equivalent to number % 32, since 32 is a power of 2
            return (int)quotient;
        }
    }
}
