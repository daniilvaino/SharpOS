// A pool that does not pool.
//
// Shaped after dotnet/runtime v8.0 (MIT),
//   src/libraries/System.Private.CoreLib/src/System/Buffers/ArrayPool.cs
// but with the buckets and the thread-local cache left out.
//
// This is under the canonical name deliberately, and it is not a stand-in: the
// ArrayPool contract says Rent returns an array of AT LEAST the requested
// length and Return hands one back — it never promises the same array comes
// back, nor that renting is cheap. Allocating each time and dropping what is
// returned satisfies all of it. What is lost is the performance the type
// exists for, so consumers that rent in a tight loop will feel it.
//
// Keeping the name matters more than keeping the speed here: library code
// written against ArrayPool<T> compiles and behaves correctly, which is the
// whole point of the naming rule.

namespace System.Buffers
{
    public sealed class ArrayPool<T>
    {
        // A property rather than a static field: a static field holding a new
        // object gives this type a class constructor, and the check ILC emits
        // for one does not work here (limits §1). Each access allocates a small
        // object, which is nothing beside the arrays it hands out.
        public static ArrayPool<T> Shared => new ArrayPool<T>();

        public T[] Rent(int minimumLength)
        {
            if (minimumLength < 0)
                throw new ArgumentOutOfRangeException(nameof(minimumLength));

            return new T[minimumLength];
        }

        /// <summary>
        /// Hands an array back. Nothing is kept, so the array is collectable
        /// from here on — a caller that keeps using it after returning it is
        /// making the same mistake it would with a real pool.
        /// </summary>
        public void Return(T[] array, bool clearArray = false)
        {
            if (array == null)
                throw new ArgumentNullException(nameof(array));

            if (clearArray)
                Array.Clear(array, 0, array.Length);
        }
    }
}
