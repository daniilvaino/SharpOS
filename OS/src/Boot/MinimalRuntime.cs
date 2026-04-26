using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Internal.Runtime
{
    internal struct MethodTable { }
}

namespace System
{
    public unsafe class Object
    {
#pragma warning disable 169
        // The layout of object is a contract with the compiler.
        private IntPtr m_pMethodTable;
#pragma warning restore 169

        // Reference-equality default. Value types override with value comparison;
        // reference types where object-identity is the right semantic use as is.
        public virtual bool Equals(object obj) => ReferenceEquals(this, obj);

        // Address-derived hash. Stable because our GC is non-moving. Not cryptographic,
        // good enough for bucket selection. Value types override with value hashing.
        public virtual int GetHashCode()
        {
            object self = this;
            nint addr = *(nint*)&self;
            return (int)addr ^ (int)((long)addr >> 32);
        }

        public virtual string ToString() => null;

        public static bool Equals(object objA, object objB)
        {
            if (ReferenceEquals(objA, objB)) return true;
            if (objA == null || objB == null) return false;
            return objA.Equals(objB);
        }

        public static bool ReferenceEquals(object objA, object objB) => objA == objB;
    }

    public struct Void { }

    // Primitive types. The recursive `<primitive> _value;` field is the BCL
    // convention — the compiler special-cases these types (their namespace
    // + name triggers intrinsic element-type flags in the MT) so the nested
    // self-reference is legal and gets laid out as the primitive's natural
    // size. The field lets us define `Equals(T)` / `GetHashCode()` bodies
    // that compare the raw value without going through Object.Equals
    // reference-equality on boxes.
    //
    // Only the subset that participates in IEquatable<T> dispatch through
    // our collection framework has the backing field + interface wired; the
    // rest stay shapeless until a caller needs them.

    public struct Boolean : IEquatable<bool>, IComparable<bool>, IComparable
    {
        private bool _value;
        public bool Equals(bool other) => _value == other;
        public override bool Equals(object obj) => obj is bool b && _value == b;
        public override int GetHashCode() => _value ? 1 : 0;
        public int CompareTo(bool other) => _value == other ? 0 : (!_value ? -1 : 1);
        public int CompareTo(object obj) => obj is bool b ? CompareTo(b) : 1;
    }

    public struct Char : IEquatable<char>, IComparable<char>, IComparable
    {
        private char _value;
        public bool Equals(char other) => _value == other;
        public override bool Equals(object obj) => obj is char c && _value == c;
        public override int GetHashCode() => _value;
        public int CompareTo(char other) => _value - other;
        public int CompareTo(object obj) => obj is char c ? CompareTo(c) : 1;
    }

    public struct SByte : IEquatable<sbyte>, IComparable<sbyte>, IComparable
    {
        private sbyte _value;
        public bool Equals(sbyte other) => _value == other;
        public override bool Equals(object obj) => obj is sbyte v && _value == v;
        public override int GetHashCode() => _value;
        public int CompareTo(sbyte other) => _value - other;
        public int CompareTo(object obj) => obj is sbyte v ? CompareTo(v) : 1;
    }

    public struct Byte : IEquatable<byte>, IComparable<byte>, IComparable
    {
        private byte _value;
        public bool Equals(byte other) => _value == other;
        public override bool Equals(object obj) => obj is byte v && _value == v;
        public override int GetHashCode() => _value;
        public int CompareTo(byte other) => _value - other;
        public int CompareTo(object obj) => obj is byte v ? CompareTo(v) : 1;
    }

    public struct Int16 : IEquatable<short>, IComparable<short>, IComparable
    {
        private short _value;
        public bool Equals(short other) => _value == other;
        public override bool Equals(object obj) => obj is short v && _value == v;
        public override int GetHashCode() => _value;
        public int CompareTo(short other) => _value - other;
        public int CompareTo(object obj) => obj is short v ? CompareTo(v) : 1;
    }

    public struct UInt16 : IEquatable<ushort>, IComparable<ushort>, IComparable
    {
        private ushort _value;
        public bool Equals(ushort other) => _value == other;
        public override bool Equals(object obj) => obj is ushort v && _value == v;
        public override int GetHashCode() => _value;
        public int CompareTo(ushort other) => _value - other;
        public int CompareTo(object obj) => obj is ushort v ? CompareTo(v) : 1;
    }

    public struct Int32 : IEquatable<int>, IComparable<int>, IComparable
    {
        public const int MaxValue = 0x7FFFFFFF;
        public const int MinValue = unchecked((int)0x80000000);

        private int _value;
        public bool Equals(int other) => _value == other;
        public override bool Equals(object obj) => obj is int v && _value == v;
        public override int GetHashCode() => _value;
        public int CompareTo(int other) => _value < other ? -1 : (_value > other ? 1 : 0);
        public int CompareTo(object obj) => obj is int v ? CompareTo(v) : 1;
    }

    public struct UInt32 : IEquatable<uint>, IComparable<uint>, IComparable
    {
        public const uint MaxValue = 0xFFFFFFFFu;
        public const uint MinValue = 0u;

        private uint _value;
        public bool Equals(uint other) => _value == other;
        public override bool Equals(object obj) => obj is uint v && _value == v;
        public override int GetHashCode() => (int)_value;
        public int CompareTo(uint other) => _value < other ? -1 : (_value > other ? 1 : 0);
        public int CompareTo(object obj) => obj is uint v ? CompareTo(v) : 1;
    }

    public struct Int64 : IEquatable<long>, IComparable<long>, IComparable
    {
        public const long MaxValue = 0x7FFFFFFFFFFFFFFFL;
        public const long MinValue = unchecked((long)0x8000000000000000L);

        private long _value;
        public bool Equals(long other) => _value == other;
        public override bool Equals(object obj) => obj is long v && _value == v;
        public override int GetHashCode() => (int)_value ^ (int)(_value >> 32);
        public int CompareTo(long other) => _value < other ? -1 : (_value > other ? 1 : 0);
        public int CompareTo(object obj) => obj is long v ? CompareTo(v) : 1;
    }

    public struct UInt64 : IEquatable<ulong>, IComparable<ulong>, IComparable
    {
        public const ulong MaxValue = 0xFFFFFFFFFFFFFFFFuL;
        public const ulong MinValue = 0uL;

        private ulong _value;
        public bool Equals(ulong other) => _value == other;
        public override bool Equals(object obj) => obj is ulong v && _value == v;
        public override int GetHashCode() => (int)_value ^ (int)(_value >> 32);
        public int CompareTo(ulong other) => _value < other ? -1 : (_value > other ? 1 : 0);
        public int CompareTo(object obj) => obj is ulong v ? CompareTo(v) : 1;
    }

    // IntPtr / UIntPtr = native-sized integer (8 bytes on x64, 4 on x86).
    // BCL defines explicit conversion operators (to/from int, long, void*)
    // which the C# compiler emits calls to whenever you cast. We give the
    // same operator surface so verbatim BCL code (Unsafe, Span, etc.) that
    // does `(IntPtr)someLong` or `(IntPtr)somePtr` compiles. The underlying
    // storage is a recursive `nint` — ILC special-cases primitives by
    // namespace+name, same trick as Int32._value.
    public readonly struct IntPtr
    {
        private readonly nint _value;

        public IntPtr(int value) { _value = (nint)value; }
        public IntPtr(long value) { _value = (nint)value; }
        public unsafe IntPtr(void* value) { _value = (nint)value; }

        public static readonly IntPtr Zero;

        public static unsafe int Size => sizeof(nint);

        public static IntPtr MaxValue => (IntPtr)long.MaxValue;
        public static IntPtr MinValue => (IntPtr)long.MinValue;

        public int ToInt32() => (int)_value;
        public long ToInt64() => (long)_value;
        public unsafe void* ToPointer() => (void*)_value;

        public static explicit operator IntPtr(int value) => new IntPtr(value);
        public static explicit operator IntPtr(long value) => new IntPtr(value);
        public static unsafe explicit operator IntPtr(void* value) => new IntPtr(value);
        public static explicit operator int(IntPtr value) => (int)value._value;
        public static explicit operator long(IntPtr value) => (long)value._value;
        public static unsafe explicit operator void*(IntPtr value) => (void*)value._value;

        public static bool operator ==(IntPtr value1, IntPtr value2) => value1._value == value2._value;
        public static bool operator !=(IntPtr value1, IntPtr value2) => value1._value != value2._value;

        public static IntPtr operator +(IntPtr pointer, int offset) => new IntPtr((long)(pointer._value + offset));
        public static IntPtr operator -(IntPtr pointer, int offset) => new IntPtr((long)(pointer._value - offset));
        public static IntPtr Add(IntPtr pointer, int offset) => pointer + offset;
        public static IntPtr Subtract(IntPtr pointer, int offset) => pointer - offset;

        public override bool Equals(object obj) => obj is IntPtr p && p._value == _value;
        public override int GetHashCode() => (int)_value ^ (int)(_value >> 32);
        public override string ToString() => SharpOS.Std.NoRuntime.NumberFormatting.LongToString((long)_value);
    }

    public readonly struct UIntPtr
    {
        private readonly nuint _value;

        public UIntPtr(uint value) { _value = (nuint)value; }
        public UIntPtr(ulong value) { _value = (nuint)value; }
        public unsafe UIntPtr(void* value) { _value = (nuint)value; }

        public static readonly UIntPtr Zero;

        public static unsafe int Size => sizeof(nuint);

        public static UIntPtr MaxValue => (UIntPtr)ulong.MaxValue;
        public static UIntPtr MinValue => (UIntPtr)0UL;

        public uint ToUInt32() => (uint)_value;
        public ulong ToUInt64() => (ulong)_value;
        public unsafe void* ToPointer() => (void*)_value;

        public static explicit operator UIntPtr(uint value) => new UIntPtr(value);
        public static explicit operator UIntPtr(ulong value) => new UIntPtr(value);
        public static unsafe explicit operator UIntPtr(void* value) => new UIntPtr(value);
        public static explicit operator uint(UIntPtr value) => (uint)value._value;
        public static explicit operator ulong(UIntPtr value) => (ulong)value._value;
        public static unsafe explicit operator void*(UIntPtr value) => (void*)value._value;

        public static bool operator ==(UIntPtr value1, UIntPtr value2) => value1._value == value2._value;
        public static bool operator !=(UIntPtr value1, UIntPtr value2) => value1._value != value2._value;

        public static UIntPtr operator +(UIntPtr pointer, int offset) => new UIntPtr((ulong)(pointer._value + (nuint)offset));
        public static UIntPtr operator -(UIntPtr pointer, int offset) => new UIntPtr((ulong)(pointer._value - (nuint)offset));
        public static UIntPtr Add(UIntPtr pointer, int offset) => pointer + offset;
        public static UIntPtr Subtract(UIntPtr pointer, int offset) => pointer - offset;

        public override bool Equals(object obj) => obj is UIntPtr p && p._value == _value;
        public override int GetHashCode() => (int)_value ^ (int)(_value >> 32);
        public override string ToString() => SharpOS.Std.NoRuntime.NumberFormatting.ULongToString((ulong)_value);
    }
    public struct Single { }
    public struct Double { }

    public abstract class ValueType { }
    public abstract class Enum : ValueType { }

    public struct Nullable<T> where T : struct { }

    public abstract class Type { }
    public class RuntimeType : Type { }

    // Base class for all arrays. Length is stored at offset 8 (after the
    // MethodTable pointer) by RhpNewArray and read here via managed field
    // access. Layout matches NativeAOT's convention; same pattern as String.
    //
    // `partial` so std/no-runtime/shared/Runtime/Array.cs can add Copy,
    // Empty<T>() and other BCL-compat statics without editing this file.
    [StructLayout(LayoutKind.Sequential)]
    public abstract partial class Array
    {
        public readonly int Length;
    }
    public abstract class Delegate { }
    public abstract class MulticastDelegate : Delegate { }

    public struct RuntimeTypeHandle { }
    public struct RuntimeMethodHandle { }
    public struct RuntimeFieldHandle { }

    public class Attribute { }

    [Flags]
    public enum AttributeTargets
    {
        Assembly = 1,
        Module = 2,
        Class = 4,
        Struct = 8,
        Enum = 16,
        Constructor = 0x20,
        Method = 0x40,
        Property = 128,
        Field = 0x100,
        Event = 512,
        Interface = 1024,
        Parameter = 2048,
        Delegate = 4096,
        ReturnValue = 8192,
        GenericParameter = 16384,
        All = 32767,
    }

    [AttributeUsage(AttributeTargets.Enum, Inherited = false)]
    public sealed class FlagsAttribute : Attribute
    {
        public FlagsAttribute() { }
    }

    [AttributeUsage(AttributeTargets.Parameter, Inherited = true)]
    public sealed class ParamArrayAttribute : Attribute
    {
        public ParamArrayAttribute() { }
    }

    public unsafe struct EETypePtr
    {
        internal Internal.Runtime.MethodTable* _value;

        internal EETypePtr(Internal.Runtime.MethodTable* value)
        {
            _value = value;
        }

        internal Internal.Runtime.MethodTable* ToPointer()
        {
            return _value;
        }

        [Intrinsic]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static EETypePtr EETypePtrOf<T>()
        {
            return default;
        }
    }

    public sealed class AttributeUsageAttribute : Attribute
    {
        public AttributeUsageAttribute(AttributeTargets validOn) { }
        public bool AllowMultiple { get; set; }
        public bool Inherited { get; set; }
    }

    public class AppContext
    {
        public static void SetData(string s, object o) { }
    }

    namespace Runtime.CompilerServices
    {
        public class RuntimeHelpers
        {
            public static unsafe int OffsetToStringData => sizeof(IntPtr) + sizeof(int);
        }

        public static class RuntimeFeature
        {
            public const string UnmanagedSignatureCallingConvention = nameof(UnmanagedSignatureCallingConvention);
            // Tells the C# compiler the runtime supports `ref T` fields in
            // ref structs (C#11 feature, used by Span<T>'s ByReference<T>
            // storage). Without this const the compiler emits CS9064.
            public const string ByRefFields = nameof(ByRefFields);
        }

        public enum MethodImplOptions
        {
            AggressiveInlining = 0x0100,
            InternalCall = 0x1000,
        }

        [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor)]
        public sealed class MethodImplAttribute : Attribute
        {
            public MethodImplAttribute(MethodImplOptions methodImplOptions) { }
        }

        [AttributeUsage(AttributeTargets.Method)]
        public sealed class IntrinsicAttribute : Attribute
        {
            public IntrinsicAttribute() { }
        }
    }
}

namespace System.Runtime.InteropServices
{
    public class UnmanagedType { }

    sealed class StructLayoutAttribute : Attribute
    {
        public StructLayoutAttribute(LayoutKind layoutKind) { }
        public int Size;
        public int Pack;
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class FieldOffsetAttribute : Attribute
    {
        public FieldOffsetAttribute(int offset) { }
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class UnmanagedCallersOnlyAttribute : Attribute
    {
        public Type[] CallConvs;
        public string EntryPoint;
    }

    internal enum LayoutKind
    {
        Sequential = 0,
        Explicit = 2,
        Auto = 3,
    }

    internal enum CharSet
    {
        None = 1,
        Ansi = 2,
        Unicode = 3,
        Auto = 4,
    }
}

namespace System
{
    namespace Runtime
    {
        internal sealed class RuntimeExportAttribute : Attribute
        {
            public RuntimeExportAttribute(string entry) { }
        }
    }

    class Array<T> : Array { }
}

namespace Internal.Runtime.CompilerHelpers
{
    using System.Runtime;

    class StartupCodeHelpers
    {
        [RuntimeExport("RhpReversePInvoke")]
        static void RhpReversePInvoke(IntPtr frame) { }

        [RuntimeExport("RhpReversePInvokeReturn")]
        static void RhpReversePInvokeReturn(IntPtr frame) { }

        [RuntimeExport("RhpPInvoke")]
        static void RhpPInvoke(IntPtr frame) { }

        [RuntimeExport("RhpPInvokeReturn")]
        static void RhpPInvokeReturn(IntPtr frame) { }

        [RuntimeExport("RhpFallbackFailFast")]
        static void RhpFallbackFailFast() { while (true) ; }
    }
}

namespace OS.Boot
{
    using SharpOS.Std.NoRuntime;
    using System.Runtime;

    internal static unsafe class NativeMemoryStubs
    {
        [RuntimeExport("memset")]
        private static void* Memset(void* destination, int value, ulong count)
        {
            return MemoryPrimitives.Memset(destination, (byte)value, count);
        }

        [RuntimeExport("memcpy")]
        private static void* Memcpy(void* destination, void* source, ulong count)
        {
            return MemoryPrimitives.Memcpy(destination, source, count);
        }

        [RuntimeExport("memmove")]
        private static void* Memmove(void* destination, void* source, ulong count)
        {
            return MemoryPrimitives.Memmove(destination, source, count);
        }
    }
}
