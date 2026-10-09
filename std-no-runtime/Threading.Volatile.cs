// Ported from dotnet/runtime (MIT):
//   src/libraries/System.Private.CoreLib/src/System/Threading/Volatile.cs
//
// Bodies verbatim: each access goes through a struct whose single field is
// declared `volatile`, so the compiler emits the `volatile.` prefix and the
// code generator keeps acquire/release ordering — the same semantics CoreLib
// has when the JIT does not replace the call with its own expansion.
//
// SharpOS cuts:
//  - [Intrinsic] / [NonVersionable] / [CLSCompliant(false)] attributes: the
//    bodies are complete without the JIT's expansion, and R2R versioning and
//    CLS checks do not apply to this image.
//  - TARGET_64BIT branches: x64 only, so Int64 is the plain volatile path.
//  - ReadBarrier / WriteBarrier (.NET 10): CoreLib's bodies are self-calls the
//    JIT must replace, and this ILC does not know them; here they are a full
//    fence (Interlocked.MemoryBarrier), which is stronger, never weaker.

#nullable enable annotations

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace System.Threading
{
    /// <summary>Methods for accessing memory with volatile semantics.</summary>
    public static class Volatile
    {
        #region Boolean
        private struct VolatileBoolean { public volatile bool Value; }

        public static bool Read(ref readonly bool location) =>
            Unsafe.As<bool, VolatileBoolean>(ref Unsafe.AsRef(in location)).Value;

        public static void Write(ref bool location, bool value) =>
            Unsafe.As<bool, VolatileBoolean>(ref location).Value = value;
        #endregion

        #region Byte
        private struct VolatileByte { public volatile byte Value; }

        public static byte Read(ref readonly byte location) =>
            Unsafe.As<byte, VolatileByte>(ref Unsafe.AsRef(in location)).Value;

        public static void Write(ref byte location, byte value) =>
            Unsafe.As<byte, VolatileByte>(ref location).Value = value;
        #endregion

        #region Double
        public static double Read(ref readonly double location)
        {
            long result = Read(ref Unsafe.As<double, long>(ref Unsafe.AsRef(in location)));
            return BitConverter.Int64BitsToDouble(result);
        }

        public static void Write(ref double location, double value) =>
            Write(ref Unsafe.As<double, long>(ref location), BitConverter.DoubleToInt64Bits(value));
        #endregion

        #region Int16
        private struct VolatileInt16 { public volatile short Value; }

        public static short Read(ref readonly short location) =>
            Unsafe.As<short, VolatileInt16>(ref Unsafe.AsRef(in location)).Value;

        public static void Write(ref short location, short value) =>
            Unsafe.As<short, VolatileInt16>(ref location).Value = value;
        #endregion

        #region Int32
        private struct VolatileInt32 { public volatile int Value; }

        public static int Read(ref readonly int location) =>
            Unsafe.As<int, VolatileInt32>(ref Unsafe.AsRef(in location)).Value;

        public static void Write(ref int location, int value) =>
            Unsafe.As<int, VolatileInt32>(ref location).Value = value;
        #endregion

        #region Int64
        public static long Read(ref readonly long location) =>
            (long)Unsafe.As<long, VolatileIntPtr>(ref Unsafe.AsRef(in location)).Value;

        public static void Write(ref long location, long value) =>
            Unsafe.As<long, VolatileIntPtr>(ref location).Value = (nint)value;
        #endregion

        #region IntPtr
        private struct VolatileIntPtr { public volatile nint Value; }

        public static nint Read(ref readonly nint location) =>
            Unsafe.As<nint, VolatileIntPtr>(ref Unsafe.AsRef(in location)).Value;

        public static void Write(ref nint location, nint value) =>
            Unsafe.As<nint, VolatileIntPtr>(ref location).Value = value;
        #endregion

        #region SByte
        private struct VolatileSByte { public volatile sbyte Value; }

        public static sbyte Read(ref readonly sbyte location) =>
            Unsafe.As<sbyte, VolatileSByte>(ref Unsafe.AsRef(in location)).Value;

        public static void Write(ref sbyte location, sbyte value) =>
            Unsafe.As<sbyte, VolatileSByte>(ref location).Value = value;
        #endregion

        #region Single
        private struct VolatileSingle { public volatile float Value; }

        public static float Read(ref readonly float location) =>
            Unsafe.As<float, VolatileSingle>(ref Unsafe.AsRef(in location)).Value;

        public static void Write(ref float location, float value) =>
            Unsafe.As<float, VolatileSingle>(ref location).Value = value;
        #endregion

        #region UInt16
        private struct VolatileUInt16 { public volatile ushort Value; }

        public static ushort Read(ref readonly ushort location) =>
            Unsafe.As<ushort, VolatileUInt16>(ref Unsafe.AsRef(in location)).Value;

        public static void Write(ref ushort location, ushort value) =>
            Unsafe.As<ushort, VolatileUInt16>(ref location).Value = value;
        #endregion

        #region UInt32
        private struct VolatileUInt32 { public volatile uint Value; }

        public static uint Read(ref readonly uint location) =>
            Unsafe.As<uint, VolatileUInt32>(ref Unsafe.AsRef(in location)).Value;

        public static void Write(ref uint location, uint value) =>
            Unsafe.As<uint, VolatileUInt32>(ref location).Value = value;
        #endregion

        #region UInt64
        public static ulong Read(ref readonly ulong location) =>
            (ulong)Read(ref Unsafe.As<ulong, long>(ref Unsafe.AsRef(in location)));

        public static void Write(ref ulong location, ulong value) =>
            Write(ref Unsafe.As<ulong, long>(ref location), (long)value);
        #endregion

        #region UIntPtr
        private struct VolatileUIntPtr { public volatile nuint Value; }

        public static nuint Read(ref readonly nuint location) =>
            Unsafe.As<nuint, VolatileUIntPtr>(ref Unsafe.AsRef(in location)).Value;

        public static void Write(ref nuint location, nuint value) =>
            Unsafe.As<nuint, VolatileUIntPtr>(ref location).Value = value;
        #endregion

        #region T
        private struct VolatileObject { public volatile object? Value; }

        [return: NotNullIfNotNull(nameof(location))]
        public static T Read<T>([NotNullIfNotNull(nameof(location))] ref readonly T location) where T : class? =>
            Unsafe.As<T>(Unsafe.As<T, VolatileObject>(ref Unsafe.AsRef(in location)).Value);

        public static void Write<T>([NotNullIfNotNull(nameof(value))] ref T location, T value) where T : class? =>
            Unsafe.As<T, VolatileObject>(ref location).Value = value;
        #endregion

        #region Barriers
        /// <summary>
        /// Synchronizes memory access as follows:
        /// The processor that executes the current thread cannot reorder instructions in such a way that memory reads before
        /// the call to <see cref="ReadBarrier"/> execute after memory accesses that follow the call to <see cref="ReadBarrier"/>.
        /// </summary>
        // SharpOS cut: full fence instead of the JIT-expanded acquire barrier.
        public static void ReadBarrier() => Interlocked.MemoryBarrier();

        /// <summary>
        /// Synchronizes memory access as follows:
        /// The processor that executes the current thread cannot reorder instructions in such a way that memory writes after
        /// the call to <see cref="WriteBarrier"/> execute before memory accesses that precede the call to <see cref="WriteBarrier"/>.
        /// </summary>
        // SharpOS cut: full fence instead of the JIT-expanded release barrier.
        public static void WriteBarrier() => Interlocked.MemoryBarrier();
        #endregion
    }
}
