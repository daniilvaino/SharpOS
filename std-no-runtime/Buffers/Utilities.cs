// System.Buffers.Utilities — ported from dotnet/runtime v8.0 (MIT),
//   src/libraries/System.Private.CoreLib/src/System/Buffers/Utilities.cs
//
// SharpOS cut: GetMemoryPressure — only the per-thread pool trims by it, and
// that pool is not here (ArrayPool.cs).

using System.Numerics;

namespace System.Buffers
{
    internal static class Utilities
    {
        internal static int SelectBucketIndex(int bufferSize)
        {
            // Buffers are bucketed so that a request between 2^(n-1) + 1 and 2^n is given a buffer of 2^n
            // Bucket index is log2(bufferSize - 1) with the exception that buffers between 1 and 16 bytes
            // are combined, and the index is slid down by 3 to compensate.
            // Zero is a valid bufferSize, and it is assigned the highest bucket index so that zero-length
            // buffers are not retained by the pool. The pool will return the Array.Empty singleton for these.
            return BitOperations.Log2((uint)bufferSize - 1 | 15) - 3;
        }

        internal static int GetMaxSizeForBucket(int binIndex)
        {
            int maxSize = 16 << binIndex;
            return maxSize;
        }
    }
}
