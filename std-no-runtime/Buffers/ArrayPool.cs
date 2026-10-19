// System.Buffers.ArrayPool<T> — ported from dotnet/runtime v8.0 (MIT),
//   src/libraries/System.Private.CoreLib/src/System/Buffers/ArrayPool.cs
//
// SharpOS cut: Shared is a ConfigurableArrayPool, not the original's
// TlsOverPerCoreLockedStacksArrayPool — that one keeps a per-thread cache in
// [ThreadStatic] fields (working since step209; the port, with its per-core
// stacks and trimming, is not done). The configurable pool is the original's
// own, buckets under a lock, and what Create() hands out there too.
//
// Until step208 this was a pool that did not pool: Rent allocated, Return
// dropped, and Shared made a new pool object on every access, because a
// static field was believed to break (that limit closed long before). Every
// BinaryWriter.Write(int) over a stream without span overloads cost three
// allocations, and BinaryWriter ran 10-20 times slower than on the host.

namespace System.Buffers
{
    public abstract class ArrayPool<T>
    {
        // Store the shared ArrayPool in a field of its derived sealed type so the Jit can "see" the exact type
        // when the Shared property is inlined which will allow it to devirtualize calls made on it.
        private static readonly ConfigurableArrayPool<T> s_shared = new ConfigurableArrayPool<T>();

        public static ArrayPool<T> Shared => s_shared;

        public static ArrayPool<T> Create() => new ConfigurableArrayPool<T>();

        public static ArrayPool<T> Create(int maxArrayLength, int maxArraysPerBucket) =>
            new ConfigurableArrayPool<T>(maxArrayLength, maxArraysPerBucket);

        public abstract T[] Rent(int minimumLength);

        public abstract void Return(T[] array, bool clearArray = false);
    }
}
