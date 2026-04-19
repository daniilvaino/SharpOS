using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Internal.Runtime
{
    internal struct MethodTable { }
}

namespace System
{
    public class Object
    {
#pragma warning disable 169
        private IntPtr m_pMethodTable;
#pragma warning restore 169
    }

    public struct Void { }
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
