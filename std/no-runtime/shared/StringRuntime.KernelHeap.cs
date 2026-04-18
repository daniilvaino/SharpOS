// Kernel-side StringRuntime backed by KernelHeap.
//
// Linked into OS.csproj only. The reference to OS.Kernel.Memory.KernelHeap
// makes this file kernel-specific (other consumers should pick a different
// StringRuntime.*.cs variant, e.g. StringRuntime.RhNewString.cs for apps).
//
// String layout (NativeAOT on x64):
//   offset 0  : MethodTable* (8 bytes)
//   offset 8  : Length (4 bytes)
//   offset 12 : char _firstChar; ... ; char '\0'  ((length+1) * 2 bytes)
//
// MethodTable pointer comes from a live string literal (string.Empty).
// This is more robust than relying on [Intrinsic] EETypePtrOf<T>() which
// may not be honoured by NativeAOT in the win-x64 EFI_APPLICATION target.

namespace SharpOS.Std.NoRuntime
{
    internal static unsafe class StringRuntime
    {
        private const int HeaderSize = 12;

        internal static string FastAllocateString(int length)
        {
            if (length <= 0)
                return string.Empty;

            if (!OS.Kernel.Memory.KernelHeap.IsInitialized)
                return string.Empty;

            uint bytes = (uint)(HeaderSize + (length + 1) * 2);

            void* raw = OS.Kernel.Memory.KernelHeap.Alloc(bytes);
            if (raw == null)
                return string.Empty;

            void* methodTable;
            fixed (char* emptyChars = string.Empty)
            {
                methodTable = *(void**)((byte*)emptyChars - HeaderSize);
            }

            *(void**)raw = methodTable;
            *(int*)((byte*)raw + 8) = length;

            ushort* chars = (ushort*)((byte*)raw + HeaderSize);
            for (int i = 0; i <= length; i++)
                chars[i] = 0;

            return *(string*)&raw;
        }
    }
}
