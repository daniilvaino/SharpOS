// Ported from dotnet/runtime release/8.0 (MIT):
//   GetSubArray — src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/RuntimeHelpers.cs
//   EnsureSufficientExecutionStack / TryEnsureSufficientExecutionStack —
//     src/coreclr/nativeaot/System.Private.CoreLib/src/System/Runtime/CompilerServices/RuntimeHelpers.NativeAot.cs
// The rest of the type lives in each tier's MinimalRuntime.cs (apps_native/sdk,
// OS/src/Boot); this part is shared by both.
// Cuts:
//   - GetSubArray: null check throws directly (our ThrowHelper has no
//     ThrowArgumentNullException(ExceptionArgument)).
//   - Stack check: no RhGetCurrentThreadStackBounds — threads here do not
//     publish their stack bounds, so the check always passes (marked
//     "SharpOS:"). Deep recursion ends in the guard-page fault, not in
//     InsufficientExecutionStackException.

using System.Runtime.InteropServices;

namespace System.Runtime.CompilerServices
{
    public partial class RuntimeHelpers
    {
        // BCL: [Intrinsic] - true only when the JIT sees a constant argument; the
        // non-intrinsic body (what runs when not folded) answers false, as here.
        [Intrinsic]
        internal static bool IsKnownConstant(int t) => false;

        [Intrinsic]
        internal static bool IsKnownConstant(char t) => false;

        [Intrinsic]
        internal static bool IsKnownConstant(string? t) => false;

        /// <summary>
        /// Whether T is a reference type or a value type with references in it.
        /// NativeAOT's CoreLib answers from the MethodTable's ContainsGCPointers
        /// flag (ILC may fold the call); the body reads the same flag.
        /// </summary>
        [Intrinsic]
        public static unsafe bool IsReferenceOrContainsReferences<T>()
        {
            ulong table = (ulong)(long)typeof(T).TypeHandle._value;
            return !((SharpOS.Std.NoRuntime.GcMethodTable*)table)->IsValueType
                || SharpOS.Std.Exchange.ObjectLayout.HasPointers(table);
        }

        /// <summary>
        /// Slices the specified array using the specified range.
        /// </summary>
        public static T[] GetSubArray<T>(T[] array, Range range)
        {
            if (array == null)
            {
                throw new ArgumentNullException("array"); // SharpOS: was ThrowHelper.ThrowArgumentNullException(ExceptionArgument.array)
            }

            (int offset, int length) = range.GetOffsetAndLength(array.Length);

            if (length == 0)
            {
                return Array.Empty<T>();
            }

            T[] dest = new T[length];

            // Due to array variance, it's possible that the incoming array is
            // actually of type U[], where U:T; or that an int[] <-> uint[] or
            // similar cast has occurred. In any case, since it's always legal
            // to reinterpret U as T in this scenario (but not necessarily the
            // other way around), we can use Buffer.Memmove here.

            Buffer.Memmove(
                ref MemoryMarshal.GetArrayDataReference(dest),
                ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(array), offset),
                (uint)length);

            return dest;
        }

        public static void EnsureSufficientExecutionStack()
        {
            if (!TryEnsureSufficientExecutionStack())
                throw new InsufficientExecutionStackException();
        }

        // SharpOS: upstream compares the stack pointer with a per-thread limit
        // computed from RhGetCurrentThreadStackBounds; no tier here exposes
        // the bounds of the current thread's stack.
        public static bool TryEnsureSufficientExecutionStack() => true;
    }
}
