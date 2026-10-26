// Object.MemberwiseClone for ported BCL code (SharpOS, not BCL).
//
// System.Object here (MinimalRuntime.cs of each tier) has no MemberwiseClone.
// Ported code that clones itself (Calendar.Clone, DateTimeFormatInfo.Clone /
// ReadOnly) calls this instead, marked "SharpOS:" at the call. Same contract
// as the BCL's: a new object of the same type, every field copied as bits
// (references shared). As Array.Clone does: the collector does not move
// objects and has no write barrier to feed, so a raw copy of the field bytes
// is a valid object.

using System;
using System.Runtime.CompilerServices;

namespace SharpOS.Std
{
    internal static unsafe class ObjectCloning
    {
        internal static object MemberwiseClone(object obj)
        {
            object self = obj;
            byte* source = *(byte**)Unsafe.AsPointer(ref self);
            var mt = *(SharpOS.Std.NoRuntime.GcMethodTable**)source;
            void* copy = SharpOS.Std.NoRuntime.GcRuntimeExports.RhpNewFast(mt);
            // The layout GcStaticsInit fills: MethodTable* at 0, the fields after it, BaseSize in all.
            uint bytes = mt->BaseSize - 8;
            if (bytes != 0)
                SharpOS.Std.MemoryOps.Memmove(ref *((byte*)copy + 8), ref *(source + 8), bytes);
            return Unsafe.As<IntPtr, object>(ref *(IntPtr*)&copy);
        }
    }
}
