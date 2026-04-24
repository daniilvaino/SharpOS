// Verbatim port from dotnet/runtime:
//   src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/Unsafe.cs
//
// Method bodies are `throw new PlatformNotSupportedException()` — never
// executed. `[Intrinsic]` tells ILC to replace the call site with direct
// IL (ldarg / sizeof / mul / add / etc.). We declare the signatures so
// ILC's intrinsic transform has a match; no logic of our own.
//
// Only visible edit: `#pragma warning disable` at the top and removal of
// the CoreCLR-specific `typeof(T).ToString()` branch inside `#if CORECLR`
// (we don't define CORECLR, so the `#else` branch wins at compile).

#pragma warning disable IDE0060 // implementations provided as intrinsics
using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;

namespace System.Runtime.CompilerServices
{
    /// <summary>
    /// Contains generic, low-level functionality for manipulating pointers.
    /// </summary>
    public static unsafe partial class Unsafe
    {
        [Intrinsic]
        [NonVersionable]
        [CLSCompliant(false)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void* AsPointer<T>(ref T value)
        {
            throw new PlatformNotSupportedException();
        }

        [Intrinsic]
        [NonVersionable]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int SizeOf<T>()
        {
            throw new PlatformNotSupportedException();
        }

        [Intrinsic]
        [NonVersionable]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [return: NotNullIfNotNull(nameof(o))]
        public static T As<T>(object o) where T : class
        {
            throw new PlatformNotSupportedException();
        }

        [Intrinsic]
        [NonVersionable]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref TTo As<TFrom, TTo>(ref TFrom source)
        {
            throw new PlatformNotSupportedException();
        }

        [Intrinsic]
        [NonVersionable]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T Add<T>(ref T source, int elementOffset)
        {
            return ref AddByteOffset(ref source, (IntPtr)(elementOffset * (nint)SizeOf<T>()));
        }

        [Intrinsic]
        [NonVersionable]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T Add<T>(ref T source, IntPtr elementOffset)
        {
            return ref AddByteOffset(ref source, (IntPtr)((nint)elementOffset * (nint)SizeOf<T>()));
        }

        [Intrinsic]
        [NonVersionable]
        [CLSCompliant(false)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void* Add<T>(void* source, int elementOffset)
        {
            return (byte*)source + (elementOffset * (nint)SizeOf<T>());
        }

        [Intrinsic]
        [NonVersionable]
        [CLSCompliant(false)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T Add<T>(ref T source, nuint elementOffset)
        {
            return ref AddByteOffset(ref source, (nuint)(elementOffset * (nuint)SizeOf<T>()));
        }

        [Intrinsic]
        [NonVersionable]
        [CLSCompliant(false)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T AddByteOffset<T>(ref T source, nuint byteOffset)
        {
            return ref AddByteOffset(ref source, (IntPtr)(void*)byteOffset);
        }

        [Intrinsic]
        [NonVersionable]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool AreSame<T>([AllowNull] ref T left, [AllowNull] ref T right)
        {
            throw new PlatformNotSupportedException();
        }

        [Intrinsic]
        [NonVersionable]
        [CLSCompliant(false)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Copy<T>(void* destination, ref T source)
        {
            throw new PlatformNotSupportedException();
        }

        [Intrinsic]
        [NonVersionable]
        [CLSCompliant(false)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Copy<T>(ref T destination, void* source)
        {
            throw new PlatformNotSupportedException();
        }

        [Intrinsic]
        [NonVersionable]
        [CLSCompliant(false)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CopyBlock(void* destination, void* source, uint byteCount)
        {
            throw new PlatformNotSupportedException();
        }

        [Intrinsic]
        [NonVersionable]
        [CLSCompliant(false)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CopyBlock(ref byte destination, ref byte source, uint byteCount)
        {
            throw new PlatformNotSupportedException();
        }

        [Intrinsic]
        [NonVersionable]
        [CLSCompliant(false)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CopyBlockUnaligned(void* destination, void* source, uint byteCount)
        {
            throw new PlatformNotSupportedException();
        }

        [Intrinsic]
        [NonVersionable]
        [CLSCompliant(false)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void CopyBlockUnaligned(ref byte destination, ref byte source, uint byteCount)
        {
            throw new PlatformNotSupportedException();
        }

        [Intrinsic]
        [NonVersionable]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAddressGreaterThan<T>([AllowNull] ref T left, [AllowNull] ref T right)
        {
            throw new PlatformNotSupportedException();
        }

        [Intrinsic]
        [NonVersionable]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAddressLessThan<T>([AllowNull] ref T left, [AllowNull] ref T right)
        {
            throw new PlatformNotSupportedException();
        }

        [Intrinsic]
        [NonVersionable]
        [CLSCompliant(false)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void InitBlock(void* startAddress, byte value, uint byteCount)
        {
            throw new PlatformNotSupportedException();
        }

        [Intrinsic]
        [NonVersionable]
        [CLSCompliant(false)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void InitBlock(ref byte startAddress, byte value, uint byteCount)
        {
            throw new PlatformNotSupportedException();
        }

        [Intrinsic]
        [NonVersionable]
        [CLSCompliant(false)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void InitBlockUnaligned(void* startAddress, byte value, uint byteCount)
        {
            throw new PlatformNotSupportedException();
        }

        [Intrinsic]
        [NonVersionable]
        [CLSCompliant(false)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void InitBlockUnaligned(ref byte startAddress, byte value, uint byteCount)
        {
            for (uint i = 0; i < byteCount; i++)
            {
                AddByteOffset(ref startAddress, i) = value;
            }
        }

        [Intrinsic]
        [NonVersionable]
        [CLSCompliant(false)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T ReadUnaligned<T>(void* source)
        {
            return Unsafe.As<byte, T>(ref *(byte*)source);
        }

        [Intrinsic]
        [NonVersionable]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T ReadUnaligned<T>(ref byte source)
        {
            return Unsafe.As<byte, T>(ref source);
        }

        [Intrinsic]
        [NonVersionable]
        [CLSCompliant(false)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUnaligned<T>(void* destination, T value)
        {
            Unsafe.As<byte, T>(ref *(byte*)destination) = value;
        }

        [Intrinsic]
        [NonVersionable]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void WriteUnaligned<T>(ref byte destination, T value)
        {
            Unsafe.As<byte, T>(ref destination) = value;
        }

        [Intrinsic]
        [NonVersionable]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T AddByteOffset<T>(ref T source, IntPtr byteOffset)
        {
            throw new PlatformNotSupportedException();
        }

        [NonVersionable]
        [CLSCompliant(false)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Read<T>(void* source)
        {
            return Unsafe.As<byte, T>(ref *(byte*)source);
        }

        [NonVersionable]
        [CLSCompliant(false)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Write<T>(void* destination, T value)
        {
            Unsafe.As<byte, T>(ref *(byte*)destination) = value;
        }

        [Intrinsic]
        [NonVersionable]
        [CLSCompliant(false)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T AsRef<T>(void* source)
        {
            return ref Unsafe.As<byte, T>(ref *(byte*)source);
        }

        [Intrinsic]
        [NonVersionable]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T AsRef<T>(scoped in T source)
        {
            throw new PlatformNotSupportedException();
        }

        [Intrinsic]
        [NonVersionable]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IntPtr ByteOffset<T>([AllowNull] ref T origin, [AllowNull] ref T target)
        {
            throw new PlatformNotSupportedException();
        }

        [Intrinsic]
        [NonVersionable]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T NullRef<T>()
        {
            return ref Unsafe.AsRef<T>(null);
        }

        [Intrinsic]
        [NonVersionable]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsNullRef<T>(ref T source)
        {
            return Unsafe.AsPointer(ref source) == null;
        }

        [Intrinsic]
        [NonVersionable]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SkipInit<T>(out T value)
        {
            throw new PlatformNotSupportedException();
        }

        [Intrinsic]
        [NonVersionable]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T Subtract<T>(ref T source, int elementOffset)
        {
            return ref SubtractByteOffset(ref source, (IntPtr)(elementOffset * (nint)SizeOf<T>()));
        }

        [Intrinsic]
        [NonVersionable]
        [CLSCompliant(false)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void* Subtract<T>(void* source, int elementOffset)
        {
            return (byte*)source - (elementOffset * (nint)Unsafe.SizeOf<T>());
        }

        [Intrinsic]
        [NonVersionable]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T Subtract<T>(ref T source, IntPtr elementOffset)
        {
            return ref SubtractByteOffset(ref source, (IntPtr)((nint)elementOffset * (nint)SizeOf<T>()));
        }

        [Intrinsic]
        [NonVersionable]
        [CLSCompliant(false)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T Subtract<T>(ref T source, nuint elementOffset)
        {
            return ref SubtractByteOffset(ref source, (nuint)(elementOffset * (nuint)Unsafe.SizeOf<T>()));
        }

        [Intrinsic]
        [NonVersionable]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T SubtractByteOffset<T>(ref T source, IntPtr byteOffset)
        {
            throw new PlatformNotSupportedException();
        }

        [Intrinsic]
        [NonVersionable]
        [CLSCompliant(false)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T SubtractByteOffset<T>(ref T source, nuint byteOffset)
        {
            return ref SubtractByteOffset(ref source, (IntPtr)(void*)byteOffset);
        }

        [Intrinsic]
        [NonVersionable]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref T Unbox<T>(object box)
            where T : struct
        {
            throw new PlatformNotSupportedException();
        }
    }
}
