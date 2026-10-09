// System.Runtime.TypeLoaderExports — the helper ILC calls for a generic
// virtual method (GVM) call: GVMLookupForSlot(obj, slot) gives the code to
// call for obj's type and the instantiated method `slot` (a
// RuntimeMethodHandle: a cell pointing at its NativeLayout signature). The
// answer is a code pointer or a fat pointer to {shared code, dictionary};
// SharpOS.Std.NoRuntime.GenericVirtualMethods finds it (step198).

namespace System.Runtime
{
    public static unsafe class TypeLoaderExports
    {
        public static IntPtr GVMLookupForSlot(object obj, RuntimeMethodHandle slot) =>
            SharpOS.Std.NoRuntime.GenericVirtualMethods.Lookup(obj, slot._value);
    }
}
