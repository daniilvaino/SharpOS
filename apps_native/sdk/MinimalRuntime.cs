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
    public unsafe struct IntPtr
    {
        private void* _value;
    }

    public unsafe struct UIntPtr
    {
        private void* _value;
    }
    public struct Single { }
    public struct Double { }

    public abstract class ValueType { }
    public abstract class Enum : ValueType { }
    public struct Nullable<T> where T : struct { }

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
    }

    [StructLayout(LayoutKind.Sequential)]
    public abstract class Array
    {
        public readonly int Length;
    }
    public abstract class Delegate { }
    public abstract class MulticastDelegate : Delegate { }

    public struct RuntimeTypeHandle { }
    public struct RuntimeMethodHandle { }
    public struct RuntimeFieldHandle { }

    public class Attribute { }

    public sealed class FlagsAttribute : Attribute
    {
        public FlagsAttribute() { }
    }

    // Required by the C# compiler for `params` parameters (e.g. the shared
    // std String.Trim(params char[]) overloads). Mirrors the kernel's.
    public sealed class ParamArrayAttribute : Attribute
    {
        public ParamArrayAttribute() { }
    }

    public enum AttributeTargets
    {
        Field = 0x100,
        Constructor = 0x20,
        Method = 0x40,
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

        internal static unsafe class RuntimeImports
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
