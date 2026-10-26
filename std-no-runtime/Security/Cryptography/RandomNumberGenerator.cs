// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from dotnet/runtime main (.NET 10, the fork dotnet-runtime-sharpos):
//   src/libraries/System.Security.Cryptography/src/System/Security/Cryptography/
//     RandomNumberGenerator.cs, RandomNumberGeneratorImplementation.cs,
//     CryptographicException.cs
// Bodies verbatim. Where the bytes come from is the platform part, as in the
// BCL (RandomNumberGeneratorImplementation.Windows.cs → BCryptGenRandom): here
// the CPU's RDRAND, reached through SystemRandom — the kernel installs its
// HardwareRandom, an app the kernel's service. A machine without RDRAND has no
// source, and the generator throws CryptographicException rather than handing
// out predictable bytes.
//
// SharpOS cuts: Create(string) (CryptoConfig, reflection over algorithm
// names); [Obsolete]/[RequiresUnreferencedCode] attributes with it.

using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace System.Security.Cryptography
{
    public abstract class RandomNumberGenerator : IDisposable
    {
        protected RandomNumberGenerator() { }

        public static RandomNumberGenerator Create() => RandomNumberGeneratorImplementation.s_singleton;

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
            return;
        }

        protected virtual void Dispose(bool disposing) { }

        public abstract void GetBytes(byte[] data);

        public virtual void GetBytes(byte[] data, int offset, int count)
        {
            VerifyGetBytes(data, offset, count);
            if (count > 0)
            {
                if (offset == 0 && count == data.Length)
                {
                    GetBytes(data);
                }
                else
                {
                    // For compat we can't avoid an alloc here since we must call GetBytes(data).
                    // However RandomNumberGeneratorImplementation avoids extra allocs.
                    var tempData = new byte[count];
                    GetBytes(tempData);
                    Buffer.BlockCopy(tempData, 0, data, offset, count);
                }
            }
        }

        public virtual void GetBytes(Span<byte> data)
        {
            // Use ArrayPool.Shared instead of CryptoPool because the array is passed out.
            byte[] array = ArrayPool<byte>.Shared.Rent(data.Length);
            try
            {
                GetBytes(array, 0, data.Length);
                new ReadOnlySpan<byte>(array, 0, data.Length).CopyTo(data);
            }
            finally
            {
                Array.Clear(array, 0, data.Length);
                ArrayPool<byte>.Shared.Return(array);
            }
        }

        public virtual void GetNonZeroBytes(byte[] data)
        {
            // For compatibility we cannot have it be abstract. Since this technically is an abstract method
            // with no implementation, we'll just throw NotImplementedException.
            throw new NotImplementedException();
        }

        public virtual void GetNonZeroBytes(Span<byte> data)
        {
            // Use ArrayPool.Shared instead of CryptoPool because the array is passed out.
            byte[] array = ArrayPool<byte>.Shared.Rent(data.Length);
            try
            {
                // NOTE: There is no GetNonZeroBytes(byte[], int, int) overload, so this call
                // may end up retrieving more data than was intended, if the array pool
                // gives back a larger array than was actually needed.
                GetNonZeroBytes(array);
                new ReadOnlySpan<byte>(array, 0, data.Length).CopyTo(data);
            }
            finally
            {
                Array.Clear(array, 0, data.Length);
                ArrayPool<byte>.Shared.Return(array);
            }
        }

        public static void Fill(Span<byte> data)
        {
            RandomNumberGeneratorImplementation.FillSpan(data);
        }

        public static int GetInt32(int fromInclusive, int toExclusive)
        {
            if (fromInclusive >= toExclusive)
                throw new ArgumentException("Range of random number does not contain at least one possibility.");

            // The total possible range is [0, 4,294,967,295).
            // Subtract one to account for zero being an actual possibility.
            uint range = (uint)toExclusive - (uint)fromInclusive - 1;

            // If there is only one possible choice, nothing random will actually happen, so return
            // the only possibility.
            if (range == 0)
            {
                return fromInclusive;
            }

            // Create a mask for the bits that we care about for the range. The other bits will be
            // masked away.
            uint mask = range;
            mask |= mask >> 1;
            mask |= mask >> 2;
            mask |= mask >> 4;
            mask |= mask >> 8;
            mask |= mask >> 16;

            uint oneUint = 0;
            Span<byte> oneUintBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref oneUint, 1)); // SharpOS: new Span<uint>(ref oneUint)
            uint result;

            do
            {
                RandomNumberGeneratorImplementation.FillSpan(oneUintBytes);
                result = mask & oneUint;
            }
            while (result > range);

            return (int)result + fromInclusive;
        }

        public static int GetInt32(int toExclusive)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(toExclusive);

            return GetInt32(0, toExclusive);
        }

        /// <summary>
        /// Creates an array of bytes with a cryptographically strong random sequence of values.
        /// </summary>
        public static byte[] GetBytes(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);

            byte[] ret = new byte[count];
            RandomNumberGeneratorImplementation.FillSpan(ret);
            return ret;
        }

        /// <summary>
        ///   Fills the elements of a specified span with items chosen at random from the provided set of choices.
        /// </summary>
        public static void GetItems<T>(ReadOnlySpan<T> choices, Span<T> destination)
        {
            if (choices.IsEmpty)
                throw new ArgumentException("The value cannot be an empty span.", nameof(choices));

            GetItemsCore<T>(choices, destination);
        }

        /// <summary>
        ///   Creates an array populated with items chosen at random from choices.
        /// </summary>
        public static T[] GetItems<T>(ReadOnlySpan<T> choices, int length)
        {
            if (choices.IsEmpty)
                throw new ArgumentException("The value cannot be an empty span.", nameof(choices));

            ArgumentOutOfRangeException.ThrowIfNegative(length);

            T[] result = new T[length];
            GetItemsCore<T>(choices, result);
            return result;
        }

        /// <summary>
        ///   Creates a string populated with characters chosen at random from choices.
        /// </summary>
        public static string GetString(ReadOnlySpan<char> choices, int length)
        {
            if (choices.IsEmpty)
                throw new ArgumentException("The value cannot be an empty span.", nameof(choices));

            ArgumentOutOfRangeException.ThrowIfNegative(length);

            // SharpOS: string.Create's SpanAction cannot take a span as its state.
            var chars = new char[length];
            GetItemsCore(choices, chars);
            return new string(chars);
        }

        /// <summary>
        ///   Fills a buffer with cryptographically random hexadecimal characters.
        /// </summary>
        public static void GetHexString(Span<char> destination, bool lowercase = false)
        {
            if (destination.IsEmpty)
                return;

            GetHexStringCore(destination, lowercase);
        }

        /// <summary>
        ///   Creates a string filled with cryptographically random hexadecimal characters.
        /// </summary>
        public static string GetHexString(int stringLength, bool lowercase = false)
        {
            if (stringLength == 0)
                return string.Empty;

            return string.Create(stringLength, lowercase, GetHexStringCore);
        }

        /// <summary>
        ///   Performs an in-place shuffle of a span using cryptographically random number generation.
        /// </summary>
        public static void Shuffle<T>(Span<T> values)
        {
            int n = values.Length;

            for (int i = 0; i < n - 1; i++)
            {
                int j = GetInt32(i, n);

                if (i != j)
                {
                    T temp = values[i];
                    values[i] = values[j];
                    values[j] = temp;
                }
            }
        }

        private static void GetHexStringCore(Span<char> destination, bool lowercase)
        {
            Debug.Assert(!destination.IsEmpty);

            const int RandomBufferSize = 64; // If this changes, the tests need to be updated since they try to exercise boundary conditions.
            Span<byte> randomBuffer = stackalloc byte[RandomBufferSize];
            HexConverter.Casing casing = lowercase ? HexConverter.Casing.Lower : HexConverter.Casing.Upper;

            // Don't overfill the buffer if the destination is smaller than the buffer size. We need to round up when
            // when dividing by two to account for an odd-length destination.
            int needed = (destination.Length + 1) >>> 1;
            Span<byte> remainingRandom = randomBuffer.Slice(0, Math.Min(RandomBufferSize, needed));
            RandomNumberGenerator.Fill(remainingRandom);

            // HexConverter can only write in multiples of two. If the length is odd, get back to an even length.
            if (destination.Length % 2 != 0)
            {
                destination[0] = lowercase ?
                    HexConverter.ToCharLower(remainingRandom[0]) :
                    HexConverter.ToCharUpper(remainingRandom[0]);

                destination = destination.Slice(1);
                remainingRandom = remainingRandom.Slice(1);
            }

            while (!destination.IsEmpty)
            {
                needed = destination.Length / 2;

                if (remainingRandom.IsEmpty)
                {
                    remainingRandom = randomBuffer.Slice(0, Math.Min(RandomBufferSize, needed));
                    RandomNumberGenerator.Fill(remainingRandom);
                }

                HexConverter.EncodeToUtf16(remainingRandom, destination, casing);
                destination = destination.Slice(remainingRandom.Length * 2);
                remainingRandom = default;
            }
        }

        private static void GetItemsCore<T>(ReadOnlySpan<T> choices, Span<T> destination)
        {
            Debug.Assert(choices.Length > 0);

            // The most expensive part of this operation is the call to get random data. If the number of
            // choices is <= 256 (which is the majority use case), we can use a single byte per element,
            // which means we can ammortize the cost of getting random data by getting random bytes in bulk.
            if (choices.Length <= 256)
            {
                // Get stack space to store random bytes. This size was chosen to balance between
                // stack consumed and number of random calls required.
                Span<byte> randomBytes = stackalloc byte[512];

                if (BitOperations.IsPow2(choices.Length))
                {
                    // If the number of choices is a power of 2, we can just mask off the extraneous bits.
                    int mask = choices.Length - 1;

                    while (!destination.IsEmpty)
                    {
                        // If this will be the last iteration, avoid over-requesting randomness.
                        if (destination.Length < randomBytes.Length)
                        {
                            randomBytes = randomBytes.Slice(0, destination.Length);
                        }

                        RandomNumberGeneratorImplementation.FillSpan(randomBytes);

                        for (int i = 0; i < randomBytes.Length; i++)
                        {
                            destination[i] = choices[randomBytes[i] & mask];
                        }

                        destination = destination.Slice(randomBytes.Length);
                    }
                }
                else
                {
                    // Rejection sampling over the masked bits.
                    int mask = (int)BitOperations.RoundUpToPowerOf2((uint)choices.Length) - 1;

                    while (!destination.IsEmpty)
                    {
                        // On average, half the bytes may be rejected, so shrink to twice the destination length.
                        if (destination.Length * 2 < randomBytes.Length)
                        {
                            randomBytes = randomBytes.Slice(0, destination.Length * 2);
                        }

                        RandomNumberGeneratorImplementation.FillSpan(randomBytes);

                        int i = 0;
                        foreach (byte b in randomBytes)
                        {
                            if ((uint)i >= (uint)destination.Length)
                            {
                                break;
                            }

                            byte masked = (byte)(b & mask);
                            if (masked < (uint)choices.Length)
                            {
                                destination[i++] = choices[masked];
                            }
                        }

                        destination = destination.Slice(i);
                    }
                }
            }
            else
            {
                // Simple fallback: get each item individually, generating a new random Int32 for each item.
                for (int i = 0; i < destination.Length; i++)
                {
                    destination[i] = choices[GetInt32(choices.Length)];
                }
            }
        }

        internal static void VerifyGetBytes(byte[] data, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(data, nameof(data));

            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (count > data.Length - offset)
                throw new ArgumentException("Offset and length were out of bounds for the array or count is greater than the number of elements from index to the end of the source collection.");
        }
    }

    internal sealed partial class RandomNumberGeneratorImplementation : RandomNumberGenerator
    {
        // a singleton which always calls into a thread-safe implementation
        // and whose Dispose method no-ops
        private static RandomNumberGeneratorImplementation? s_instance;

        // SharpOS: a property over a lazily made instance, not the BCL's static
        // readonly field — std types avoid class constructors on early paths.
        internal static RandomNumberGeneratorImplementation s_singleton => s_instance ??= new RandomNumberGeneratorImplementation();

        // private ctor used only by singleton
        private RandomNumberGeneratorImplementation()
        {
        }

        // As long as each implementation can provide a static GetBytes(ref byte buf, int length)
        // they can share this one implementation of FillSpan.
        internal static unsafe void FillSpan(Span<byte> data)
        {
            if (data.Length > 0)
            {
                fixed (byte* ptr = data) GetBytes(ptr, data.Length);
            }
        }

        public override void GetBytes(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data, nameof(data));

            GetBytes(new Span<byte>(data));
        }

        public override void GetBytes(byte[] data, int offset, int count)
        {
            VerifyGetBytes(data, offset, count);
            GetBytes(new Span<byte>(data, offset, count));
        }

        public override unsafe void GetBytes(Span<byte> data)
        {
            if (data.Length > 0)
            {
                fixed (byte* ptr = data) GetBytes(ptr, data.Length);
            }
        }

        public override void GetNonZeroBytes(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data, nameof(data));

            FillNonZeroBytes(data);
        }

        public override void GetNonZeroBytes(Span<byte> data)
        {
            FillNonZeroBytes(data);
        }

        internal static void FillNonZeroBytes(Span<byte> data)
        {
            while (data.Length > 0)
            {
                // Fill the remaining portion of the span with random bytes.
                FillSpan(data);

                // Find the first zero in the remaining portion. If there isn't any, we're all done.
                int first0Byte = data.IndexOf((byte)0);
                if (first0Byte < 0)
                {
                    return;
                }

                // There's at least one zero.  Shift down all non-zeros.
                int zerosFound = 1;
                Span<byte> remainder = data.Slice(first0Byte + 1);
                while (true)
                {
                    // Find the next zero.
                    int next0Byte = remainder.IndexOf((byte)0);
                    if (next0Byte < 0)
                    {
                        // There weren't any more zeros. Copy the remaining valid data down.
                        remainder.CopyTo(data.Slice(first0Byte));
                        break;
                    }

                    // Copy down until the next zero, then reset to continue copying from there.
                    remainder.Slice(0, next0Byte).CopyTo(data.Slice(first0Byte));
                    zerosFound++;
                    first0Byte += next0Byte;
                    remainder = remainder.Slice(next0Byte + 1);
                }

                // Slice to any remaining space that needs to be filled. This is equal to the
                // number of zeros found.
                data = data.Slice(data.Length - zerosFound);
            }
        }

        // SharpOS: the platform part (BCryptGenRandom on Windows) — SystemRandom.
        private static unsafe void GetBytes(byte* pbBuffer, int count)
        {
            Debug.Assert(count > 0);

            if (!SharpOS.Std.NoRuntime.SystemRandom.TryFill(pbBuffer, count))
                throw new CryptographicException("No source of cryptographic random numbers: the CPU has no RDRAND, or it kept failing.");
        }
    }

    // SharpOS: derives from Exception — std has no SystemException.
    public class CryptographicException : Exception
    {
        public CryptographicException() : base("Error occurred during a cryptographic operation.") { }
        public CryptographicException(int hr) : this() { HResult = hr; }
        public CryptographicException(string? message) : base(message) { }
        public CryptographicException(string? message, Exception? inner) : base(message, inner) { }
        public CryptographicException(string format, string? insert) : base(string.Format(format, insert)) { }
    }
}

namespace SharpOS.Std.NoRuntime
{
    /// <summary>
    /// Where cryptographic random bytes come from: a function the tier installs
    /// — the kernel its RDRAND reader, an app the kernel's service. Not
    /// installed, there is no source.
    /// </summary>
    public static unsafe class SystemRandom
    {
        // int fill(byte* buffer, int count): 0 = filled.
        private static delegate* unmanaged<byte*, int, int> s_fill;

        public static bool IsAvailable => s_fill != null;

        public static void Install(delegate* unmanaged<byte*, int, int> fill) { s_fill = fill; }

        public static bool TryFill(byte* buffer, int count)
        {
            if (count <= 0) return true;
            return s_fill != null && s_fill(buffer, count) == 0;
        }
    }
}
