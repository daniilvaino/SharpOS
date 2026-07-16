using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Internal.Runtime
{
    internal struct MethodTable { }
}

namespace System.Runtime.CompilerServices
{
    // ILC GenericUnboxingThunk lookup target — see OS/src/Boot/MinimalRuntime.cs
    // for the longer explanation. Same shape as upstream.
    internal class RawData
    {
        public byte Data;
    }
}

namespace System
{
    public unsafe class Object
    {
#pragma warning disable 169
        // Field name `m_pEEType` is contract with NativeAOT ILC — see
        // OS/src/Boot/MinimalRuntime.cs for the longer explanation.
        private IntPtr m_pEEType;
#pragma warning restore 169

        public virtual bool Equals(object obj) => ReferenceEquals(this, obj);

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

        // The object's MethodTable as an EETypePtr. NativeAOT S.P.CoreLib
        // exposes this as an instance method on Object; Delegate/
        // MulticastDelegate use it for type-identity (InternalEqualTypes,
        // NewMulticastDelegate allocation). m_pEEType is the header word @0.
        internal EETypePtr GetEETypePtr() =>
            new EETypePtr((Internal.Runtime.MethodTable*)m_pEEType);
    }

    public struct Void { }

    // Primitives carry a self-referential backing field + IEquatable<T>/
    // IComparable<T> so `x is IEquatable<T>` (DefaultComparer<T>.Equals) and
    // Array.Sort/List.Contains/Dictionary lookups resolve through real interface
    // dispatch instead of falling through to Object.Equals reference-equality on
    // boxes. Mirrors OS/src/Boot/MinimalRuntime.cs (kernel tier); the app tier
    // was shapeless (`struct Int32 { }`) so int was NOT IEquatable<int> and every
    // value-type comparison silently gave the wrong answer.
    public struct Boolean : IEquatable<bool>, IComparable<bool>, IComparable
    {
        private bool _value;
        public bool Equals(bool other) => _value == other;
        public override bool Equals(object obj) => obj is bool b && _value == b;
        public override int GetHashCode() => _value ? 1 : 0;
        public int CompareTo(bool other) => _value == other ? 0 : (!_value ? -1 : 1);
        public int CompareTo(object obj) => obj is bool b ? CompareTo(b) : 1;
        public override string ToString() => _value ? "True" : "False";
    }

    public struct Char : IEquatable<char>, IComparable<char>, IComparable
    {
        public const char MaxValue = (char)0xFFFF;
        public const char MinValue = (char)0x00;

        private char _value;
        public bool Equals(char other) => _value == other;
        public override bool Equals(object obj) => obj is char c && _value == c;
        public override int GetHashCode() => _value;
        public int CompareTo(char other) => _value - other;
        public int CompareTo(object obj) => obj is char c ? CompareTo(c) : 1;
        public override string ToString() => new string(new char[] { _value });
    }

    public struct SByte : IEquatable<sbyte>, IComparable<sbyte>, IComparable
    {
        public const sbyte MaxValue = (sbyte)0x7F;
        public const sbyte MinValue = unchecked((sbyte)0x80);

        private sbyte _value;
        public bool Equals(sbyte other) => _value == other;
        public override bool Equals(object obj) => obj is sbyte v && _value == v;
        public override int GetHashCode() => _value;
        public int CompareTo(sbyte other) => _value - other;
        public int CompareTo(object obj) => obj is sbyte v ? CompareTo(v) : 1;
        public override string ToString() => SharpOS.Std.NoRuntime.NumberFormatting.IntToString(_value);
    }

    public struct Byte : IEquatable<byte>, IComparable<byte>, IComparable
    {
        public const byte MaxValue = (byte)0xFF;
        public const byte MinValue = 0;

        private byte _value;
        public bool Equals(byte other) => _value == other;
        public override bool Equals(object obj) => obj is byte v && _value == v;
        public override int GetHashCode() => _value;
        public int CompareTo(byte other) => _value - other;
        public int CompareTo(object obj) => obj is byte v ? CompareTo(v) : 1;
        public override string ToString() => SharpOS.Std.NoRuntime.NumberFormatting.UIntToString(_value);
    }

    public struct Int16 : IEquatable<short>, IComparable<short>, IComparable
    {
        public const short MaxValue = (short)0x7FFF;
        public const short MinValue = unchecked((short)0x8000);

        private short _value;
        public bool Equals(short other) => _value == other;
        public override bool Equals(object obj) => obj is short v && _value == v;
        public override int GetHashCode() => _value;
        public int CompareTo(short other) => _value - other;
        public int CompareTo(object obj) => obj is short v ? CompareTo(v) : 1;
        public override string ToString() => SharpOS.Std.NoRuntime.NumberFormatting.IntToString(_value);
    }

    public struct UInt16 : IEquatable<ushort>, IComparable<ushort>, IComparable
    {
        public const ushort MaxValue = (ushort)0xFFFF;
        public const ushort MinValue = 0;

        private ushort _value;
        public bool Equals(ushort other) => _value == other;
        public override bool Equals(object obj) => obj is ushort v && _value == v;
        public override int GetHashCode() => _value;
        public int CompareTo(ushort other) => _value - other;
        public int CompareTo(object obj) => obj is ushort v ? CompareTo(v) : 1;
        public override string ToString() => SharpOS.Std.NoRuntime.NumberFormatting.UIntToString(_value);
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
        public override string ToString() => SharpOS.Std.NoRuntime.NumberFormatting.IntToString(_value);
        public string ToString(string format) => SharpOS.Std.NoRuntime.NumberFormatting.FormatInt64(_value, format);

        public static int Parse(string s)
        {
            if (!SharpOS.Std.NoRuntime.NumberParsing.TryParseInt32(s, out int value))
                throw new FormatException("Input string was not in a correct format.");
            return value;
        }

        public static bool TryParse(string s, out int result)
            => SharpOS.Std.NoRuntime.NumberParsing.TryParseInt32(s, out result);
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
        public override string ToString() => SharpOS.Std.NoRuntime.NumberFormatting.UIntToString(_value);
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
        public override string ToString() => SharpOS.Std.NoRuntime.NumberFormatting.LongToString(_value);

        public static long Parse(string s)
        {
            if (!SharpOS.Std.NoRuntime.NumberParsing.TryParseInt64(s, out long value))
                throw new FormatException("Input string was not in a correct format.");
            return value;
        }

        public static bool TryParse(string s, out long result)
            => SharpOS.Std.NoRuntime.NumberParsing.TryParseInt64(s, out result);
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
        public override string ToString() => SharpOS.Std.NoRuntime.NumberFormatting.ULongToString(_value);
    }
    // IntPtr / UIntPtr = native-sized integer (8 bytes on x64, 4 on x86).
    // BCL defines explicit conversion operators (to/from int, long, void*)
    // which the C# compiler emits calls to whenever you cast. We give the
    // same operator surface so verbatim BCL code (Unsafe, Span, etc.) that
    // does `(IntPtr)someLong` or `(IntPtr)somePtr` compiles. The underlying
    // storage is a recursive `nint` — ILC special-cases primitives by
    // namespace+name, same trick as Int32._value. Mirrors the kernel's
    // OS/src/Boot/MinimalRuntime.cs.
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
        public override int GetHashCode() => (int)_value ^ (int)((long)_value >> 32);
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
        public override int GetHashCode() => (int)_value ^ (int)((long)_value >> 32);
        public override string ToString() => SharpOS.Std.NoRuntime.NumberFormatting.ULongToString((ulong)_value);
    }

    // Single / Double MUST carry the recursive `_value` instance field — the
    // same BCL convention as Int32/Boolean/Char. Without it the struct is an
    // empty (1-byte) type, so ILC mis-sizes its box: `box (double)x` allocates
    // a box too small for the 8-byte value, and writing the value overruns it
    // into the next heap object's header (surfaced step131 as a heap corruptor
    // in string.Format {0:F2} → bad MT 0xFFFFFFFF00000000). The field gives the
    // type its true 4/8-byte size so the box is correct. Bit patterns of the
    // constants match dotnet/runtime; NaN / Infinity are IEEE-754 const-foldable.
    public struct Single
    {
#pragma warning disable 169
        private float _value;
#pragma warning restore 169

        public const float MinValue = -3.40282347E+38F;
        public const float MaxValue = 3.40282347E+38F;
        public const float Epsilon = 1.401298E-45F;
        public const float PositiveInfinity = (float)1.0 / (float)0.0;
        public const float NegativeInfinity = (float)-1.0 / (float)0.0;
        public const float NaN = (float)0.0 / (float)0.0;
    }

    public struct Double
    {
        private double _value;

        public const double MinValue = -1.7976931348623157E+308;
        public const double MaxValue = 1.7976931348623157E+308;
        public const double Epsilon = 4.9406564584124654E-324;
        public const double PositiveInfinity = 1.0 / 0.0;
        public const double NegativeInfinity = -1.0 / 0.0;
        public const double NaN = 0.0 / 0.0;

        // Bit-pattern classification — avoids the self-comparison idiom
        // (d != d) that trips CS1718 at callsites.
        public static unsafe bool IsNaN(double d)
        {
            ulong bits = *(ulong*)&d;
            return (bits & 0x7FFFFFFFFFFFFFFFul) > 0x7FF0000000000000ul;
        }

        public static unsafe bool IsInfinity(double d)
        {
            ulong bits = *(ulong*)&d;
            return (bits & 0x7FFFFFFFFFFFFFFFul) == 0x7FF0000000000000ul;
        }

        public override string ToString() => SharpOS.Std.NoRuntime.NumberFormatting.DoubleToString(_value);
        public string ToString(string format) => SharpOS.Std.NoRuntime.NumberFormatting.DoubleToString(_value, format);
    }

    public abstract class ValueType { }
    public abstract class Enum : ValueType { }

    // Ported from dotnet/runtime
    // src/libraries/System.Private.CoreLib/src/System/Nullable.cs
    // Roslyn lowering for `T?` value-type chains (`arr?.Length ?? 0`,
    // `int? x = ...`, `x.HasValue`) calls into specific members via
    // `.Single(predicate)` ctor/method lookup. An empty stub here makes
    // Roslyn crash with "Sequence contains no elements". [Serializable]
    // in upstream dropped (no SerializableAttribute in our std). Mirrors
    // the kernel's OS/src/Boot/MinimalRuntime.cs.
    public struct Nullable<T> where T : struct
    {
        private readonly bool hasValue;
        internal T value;

        public Nullable(T value)
        {
            this.value = value;
            this.hasValue = true;
        }

        public readonly bool HasValue => hasValue;
        public readonly T Value
        {
            get
            {
                if (!hasValue) ThrowNoValue();
                return value;
            }
        }

        public readonly T GetValueOrDefault() => value;
        public readonly T GetValueOrDefault(T defaultValue) => hasValue ? value : defaultValue;

        public override bool Equals(object other)
        {
            if (!hasValue) return other == null;
            if (other == null) return false;
            return value.Equals(other);
        }

        public override int GetHashCode() => hasValue ? value.GetHashCode() : 0;

        public override string ToString() => hasValue ? value.ToString() : "";

        public static implicit operator Nullable<T>(T value) => new Nullable<T>(value);
        public static explicit operator T(Nullable<T> value) => value.Value;

        private static void ThrowNoValue()
        {
            throw new InvalidOperationException("Nullable object must have a value.");
        }
    }

    public abstract class Type { }
    public class RuntimeType : Type { }

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

        // Type-identity comparison (Delegate.InternalEqualTypes / GetHashCode).
        public static bool operator ==(EETypePtr a, EETypePtr b) => a._value == b._value;
        public static bool operator !=(EETypePtr a, EETypePtr b) => a._value != b._value;
        public override bool Equals(object o) => o is EETypePtr e && _value == e._value;
        public override int GetHashCode() => unchecked((int)(nint)_value);
    }

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

    // Delegate / MulticastDelegate come from std/no-runtime/shared/Runtime/
    // (Delegate.cs / MulticastDelegate.cs / ActionFunc.cs), same files the
    // kernel compiles — the ILC field-layout/Initialize* contract lives there.

    // Each carries one pointer-sized slot so ILC's ldtoken lowering
    // (Internal.Runtime.CompilerHelpers.LdTokenHelpers.GetRuntime*Handle,
    // required by ILC 8+) can pointer-store the token into it. Empty
    // structs would overflow on the store.
    public struct RuntimeTypeHandle { internal IntPtr _value; }
    public struct RuntimeMethodHandle { internal IntPtr _value; }
    public struct RuntimeFieldHandle { internal IntPtr _value; }

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

    // Required by the C# compiler for `params` parameters (e.g. the shared
    // std String.Trim(params char[]) overloads). Mirrors the kernel's.
    [AttributeUsage(AttributeTargets.Parameter, Inherited = true)]
    public sealed class ParamArrayAttribute : Attribute
    {
        public ParamArrayAttribute() { }
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

            // Roslyn lowers `ReadOnlySpan<T> x = [1,2,3,...]` and similar RVA
            // literals into `ldtoken <field> + call RuntimeHelpers.CreateSpan<T>`.
            // [Intrinsic] tells ILC to fold the pattern into a direct span over
            // the RData blob — the body never executes. Return default to avoid
            // pulling in any exception type for the dead path.
            [Intrinsic]
            public static ReadOnlySpan<T> CreateSpan<T>(RuntimeFieldHandle fldHandle)
                => default;
        }

        public static class RuntimeFeature
        {
            public const string UnmanagedSignatureCallingConvention = nameof(UnmanagedSignatureCallingConvention);
            // Tells the C# compiler the runtime supports `ref T` fields in
            // ref structs (C#11 feature, used by Span<T>'s ByReference<T>
            // storage). Without this const the compiler emits CS9064.
            public const string ByRefFields = nameof(ByRefFields);
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

        [AttributeUsage(AttributeTargets.Method)]
        internal sealed class RuntimeImportAttribute : Attribute
        {
            public RuntimeImportAttribute(string dllName) { }
            public RuntimeImportAttribute(string dllName, string entryPoint) { }
        }

        // `partial` — std/no-runtime/shared/Runtime/RuntimeImports.Delegate.cs
        // contributes RhNewObject (backed by our GcHeap) for MulticastDelegate.
        internal static unsafe partial class RuntimeImports
        {
            private const string RuntimeLibrary = "*";

            [MethodImpl(MethodImplOptions.InternalCall)]
            [RuntimeImport(RuntimeLibrary, "__managed__Startup")]
            internal static extern void ManagedStartup();

            [MethodImpl(MethodImplOptions.InternalCall)]
            [RuntimeImport(RuntimeLibrary, "RhNewString")]
            internal static extern string RhNewString(EETypePtr pEEType, int length);
        }
    }

    class Array<T> : Array { }
}

namespace System.Runtime.CompilerServices
{
    public enum MethodImplOptions
    {
        NoInlining = 0x0008,
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

    internal enum ExceptionStringID
    {
        Unknown = 0,
    }

    internal static class ThrowHelpers
    {
        public static void ThrowFeatureBodyRemoved() { while (true) ; }
        public static void ThrowTypeLoadException() { while (true) ; }
        public static void ThrowTypeLoadExceptionWithArgument(ExceptionStringID id) { while (true) ; }
        public static void ThrowMissingFieldException() { while (true) ; }
        public static void ThrowMissingMethodException() { while (true) ; }
        public static void ThrowFileNotFoundException() { while (true) ; }
        public static void ThrowInvalidProgramException() { while (true) ; }
        public static void ThrowInvalidProgramExceptionWithArgument(ExceptionStringID id) { while (true) ; }
        public static void ThrowInvalidProgramExceptionWithArgument(int id) { while (true) ; }
        public static void ThrowInvalidProgramExceptionWithArgument(uint id) { while (true) ; }
        public static void ThrowInvalidProgramExceptionWithArgument(string argumentName) { while (true) ; }
        public static void ThrowInvalidProgramExceptionWithArgument(object argument) { while (true) ; }
        public static void ThrowInvalidProgramExceptionWithArgument(System.IntPtr argument) { while (true) ; }
        public static void ThrowBadImageFormatException() { while (true) ; }
        public static void ThrowMarshalDirectiveException() { while (true) ; }
        public static void ThrowNullReferenceException() { while (true) ; }
        public static void ThrowIndexOutOfRangeException() { while (true) ; }
        public static void ThrowArgumentNullException() { while (true) ; }
        public static void ThrowArgumentOutOfRangeException() { while (true) ; }
        public static void ThrowArgumentException() { while (true) ; }
        public static void ThrowNotImplementedException() { while (true) ; }
        public static void ThrowPlatformNotSupportedException() { while (true) ; }
    }
}

namespace SharpOS.AppSdk
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
