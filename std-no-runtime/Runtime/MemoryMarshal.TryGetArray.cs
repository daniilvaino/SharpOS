// MemoryMarshal.TryGetArray (step223, MemoryStream and the streams of the
// archive ports), TryGetMemoryManager and TryGetString (step224,
// ReadOnlySequence): the owner behind a Memory, when it is of that kind.

namespace System.Runtime.InteropServices
{
    public static partial class MemoryMarshal
    {
        public static bool TryGetMemoryManager<T, TManager>(ReadOnlyMemory<T> memory, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TManager? manager)
            where TManager : System.Buffers.MemoryManager<T>
        {
            manager = memory.GetObjectStartLength(out _, out _) as TManager;
            return manager != null;
        }

        public static bool TryGetMemoryManager<T, TManager>(ReadOnlyMemory<T> memory, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out TManager? manager, out int start, out int length)
            where TManager : System.Buffers.MemoryManager<T>
        {
            manager = memory.GetObjectStartLength(out start, out length) as TManager;
            if (manager == null)
            {
                start = default;
                length = default;
                return false;
            }
            return true;
        }

        public static bool TryGetString(ReadOnlyMemory<char> memory, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? text, out int start, out int length)
        {
            if (memory.GetObjectStartLength(out int offset, out int count) is string s)
            {
                text = s;
                start = offset;
                length = count;
                return true;
            }
            text = null;
            start = 0;
            length = 0;
            return false;
        }

        public static bool TryGetArray<T>(ReadOnlyMemory<T> memory, out ArraySegment<T> segment)
        {
            if (memory.TryGetArrayCore(out T[]? array, out int start, out int length))
            {
                segment = new ArraySegment<T>(array!, start, length);
                return true;
            }
            segment = default;
            return false;
        }
    }
}
