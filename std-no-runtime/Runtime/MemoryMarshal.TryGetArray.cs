// MemoryMarshal.TryGetArray (step223, MemoryStream and the streams of the
// archive ports): the array behind a Memory, when an array is what it is.

namespace System.Runtime.InteropServices
{
    public static partial class MemoryMarshal
    {
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
