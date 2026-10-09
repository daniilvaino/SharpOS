// System.Activator.CreateInstance<T>() — what C# compiles `new T()` to
// (step198). Ported from the NativeAOT CoreLib (Activator.NativeAot.cs,
// dotnet/runtime v8.0, MIT): the compiler expands DefaultConstructorOf<T>()
// and AllocatorOf<T>() — a constant in unshared code, a dictionary lookup in
// shared code — and the method allocates and calls the constructor.
//
// Cuts: RawCalliHelper becomes C# function pointers (managed convention, the
// instance as the first argument, as calli does); the debugger annotations;
// the non-generic CreateInstance(Type, …) overloads, which need metadata.

using System.Runtime.CompilerServices;

namespace System
{
    public static partial class Activator
    {
        [Intrinsic]
        public static unsafe T CreateInstance<T>()
        {
            // Grab the pointer to the default constructor of the type. If T doesn't have a default
            // constructor, the intrinsic returns a marker pointer that we check for.
            IntPtr defaultConstructor = DefaultConstructorOf<T>();
            if (defaultConstructor == GetFallbackDefaultConstructor())
                throw new MissingMethodException("No parameterless constructor defined for this type.");

            T t;
            try
            {
                void* table = EETypePtr.EETypePtrOf<T>().ToPointer();
                if (!((SharpOS.Std.NoRuntime.GcMethodTable*)table)->IsValueType)
                {
                    // Grab a pointer to the optimized allocator for the type and call it.
                    IntPtr allocator = AllocatorOf<T>();
                    t = ((delegate*<IntPtr, T>)allocator)((IntPtr)table);
                    ((delegate*<T, void>)defaultConstructor)(t);
                }
                else
                {
                    t = default;
                    ((delegate*<ref byte, void>)defaultConstructor)(ref Unsafe.As<T, byte>(ref t));
                }
                return t;
            }
            catch (Exception e)
            {
                throw new System.Reflection.TargetInvocationException(e);
            }
        }

        [Intrinsic]
        private static IntPtr DefaultConstructorOf<T>()
        {
            // Codegens must expand this intrinsic to the pointer to the default constructor of T
            // or to a marker that lets us detect there's no default constructor.
            throw new NotSupportedException();
        }

        [Intrinsic]
        private static IntPtr AllocatorOf<T>()
        {
            // Codegens must expand this intrinsic to the pointer to the allocator suitable to allocate an instance of T.
            throw new NotSupportedException();
        }

        internal static unsafe IntPtr GetFallbackDefaultConstructor()
        {
            return (IntPtr)(delegate*<Guid>)&MissingConstructorMethod;
        }

        // This is a marker method. We return a GUID just to make sure the body is unique
        // and under no circumstances gets folded.
        private static Guid MissingConstructorMethod() => new Guid(0x68be9718, unchecked((short)0xf787), 0x45ab, 0x84, 0x3b, 0x1f, 0x31, 0xb6, 0x12, 0x65, 0xeb);
    }
}

namespace System.Reflection
{
    // Ported from dotnet/runtime v8.0 (MIT): the exception a constructor or
    // method called through reflection — here, new T() — threw, wrapped.
    public sealed class TargetInvocationException : Exception
    {
        public TargetInvocationException(Exception inner)
            : base("Exception has been thrown by the target of an invocation.", inner)
        {
        }

        public TargetInvocationException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}
