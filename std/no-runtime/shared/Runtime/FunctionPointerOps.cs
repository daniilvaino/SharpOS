// Ported from dotnet/runtime v8.0.27 (same servicing as our ilcompiler 8.0.27):
//   src/coreclr/nativeaot/System.Private.CoreLib/src/Internal/Runtime/
//   CompilerServices/FunctionPointerOps.cs  (MIT)
//
// Read-side only. Delegate/MulticastDelegate need IsGenericMethodPointer +
// Compare to recognise and compare "fat" function pointers (generic methods
// over __Canon: the pointer is tagged and points at a GenericMethodDescriptor
// carrying the instantiation argument).
//
// Cuts vs original:
//   - GetGenericMethodFunctionPointer (the CREATE side) + its statics
//     (LowLevelList / LowLevelDictionary / NativeMemory chunk allocator) —
//     ILC's generic dictionary machinery produces the fat pointers; we only
//     consume them. Dropping it also drops the non-portable native-heap path.
//   - GenericMethodDescriptorInfo (dictionary key) — only used by the cut side.
//
// TARGET_64BIT only (kernel is x64). FatFunctionPointerOffset = 2 → a fat
// pointer has bit 1 set; a regular code pointer is aligned (low bits 0). This
// is exactly the tag the Delegate fat-pointer tripwire keys on.

using System;

namespace Internal.Runtime.CompilerServices
{
    // Runtime descriptor a fat function pointer points at (minus the tag).
    // Two words: the canonical method entry + the instantiation argument
    // (dictionary / MethodTable) that disambiguates the shared-generic body.
    public struct GenericMethodDescriptor
    {
        public IntPtr MethodFunctionPointer;
        public IntPtr InstantiationArgument;

        public GenericMethodDescriptor(IntPtr methodFunctionPointer, IntPtr instantiationArgument)
        {
            MethodFunctionPointer = methodFunctionPointer;
            InstantiationArgument = instantiationArgument;
        }
    }

    public static class FunctionPointerOps
    {
        private const int FatFunctionPointerOffset = 2;

        public static unsafe bool IsGenericMethodPointer(IntPtr functionPointer)
        {
            // Check the low bit(s) to find out what kind of function pointer we have.
            if ((functionPointer.ToInt64() & FatFunctionPointerOffset) == FatFunctionPointerOffset)
            {
                return true;
            }
            return false;
        }

        public static unsafe GenericMethodDescriptor* ConvertToGenericDescriptor(IntPtr functionPointer)
        {
            return (GenericMethodDescriptor*)((byte*)functionPointer - FatFunctionPointerOffset);
        }

        public static unsafe bool Compare(IntPtr functionPointerA, IntPtr functionPointerB)
        {
            if (!IsGenericMethodPointer(functionPointerA))
            {
                return functionPointerA == functionPointerB;
            }

            if (!IsGenericMethodPointer(functionPointerB))
            {
                return false;
            }

            GenericMethodDescriptor* pointerDefA = ConvertToGenericDescriptor(functionPointerA);
            GenericMethodDescriptor* pointerDefB = ConvertToGenericDescriptor(functionPointerB);

            if (pointerDefA->InstantiationArgument != pointerDefB->InstantiationArgument)
            {
                return false;
            }

            return pointerDefA->MethodFunctionPointer == pointerDefB->MethodFunctionPointer;
        }
    }
}
