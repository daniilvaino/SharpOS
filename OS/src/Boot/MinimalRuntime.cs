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

    // The layout of primitive types is special cased because it would be recursive.
    // These really don't need any fields to work.
    public struct Boolean { }
    public struct Char { }
    public struct SByte { }
    public struct Byte { }
    public struct Int16 { }
    public struct UInt16 { }
    public struct Int32 { }
    public struct UInt32 { }
    public struct Int64 { }
    public struct UInt64 { }
    public struct IntPtr { }
    public struct UIntPtr { }
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

    public enum AttributeTargets
    {
        Field = 0x100,
        Constructor = 0x20,
        Method = 0x40,
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
