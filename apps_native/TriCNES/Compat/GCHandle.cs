using System.Runtime.CompilerServices;

// App-local stand-in — see Compat\Drawing.cs for why these live here and not
// in std.
//
// The upstream DirectBitmap pins its int[] so it can hand a raw pointer to
// WinForms. We never build that Bitmap for real, so nothing ever dereferences
// the address. What we DO need is for the pin to be honest while the handle is
// alive, because the emulator keeps that array for the whole run and our GC is
// non-moving anyway — so "pinned" is the status quo rather than a promise we
// have to keep. AddrOfPinnedObject returns the real address regardless, which
// keeps the type useful if someone later blits straight from it.

namespace System.Runtime.InteropServices
{
    public enum GCHandleType
    {
        Weak = 0,
        WeakTrackResurrection = 1,
        Normal = 2,
        Pinned = 3,
    }

    public struct GCHandle
    {
        private object _target;

        public static GCHandle Alloc(object value, GCHandleType type)
        {
            GCHandle handle = default;
            handle._target = value;
            _ = type;
            return handle;
        }

        public static GCHandle Alloc(object value) => Alloc(value, GCHandleType.Normal);

        public object Target => _target;
        public bool IsAllocated => _target != null;

        /// <summary>
        /// Address of the first element of the pinned array. Valid because the
        /// kernel GC does not move objects; if that ever changes, every caller
        /// of this needs revisiting, not just this method.
        /// </summary>
        public unsafe IntPtr AddrOfPinnedObject()
        {
            if (_target is int[] ints)
            {
                fixed (int* p = ints) return (IntPtr)p;
            }
            if (_target is byte[] bytes)
            {
                fixed (byte* p = bytes) return (IntPtr)p;
            }
            return IntPtr.Zero;
        }

        public void Free() { _target = null; }
    }
}
