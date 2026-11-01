// System.Runtime.InteropServices.GCHandle — public surface and handle
// encoding from dotnet/runtime v8.0.27 (MIT)
//   src/libraries/System.Private.CoreLib/src/System/Runtime/InteropServices/GCHandle.cs
// The runtime's handle table (RuntimeImports.RhHandleAlloc and friends) is
// replaced by one here: a growable object[] reached from a static, so every
// entry is a root for both collectors; a free slot holds the next free index.
//
// SharpOS cut: Weak and WeakTrackResurrection hold their target strongly —
// neither collector has weak handles (WeakReference<T> and
// ConditionalWeakTable do the same; docs/nativeaot-nostd-kernel-limits.md).
// SharpOS cut: Pinned does not check Marshal.IsPinnable — nothing here moves,
// so every object stays where it is with or without a pin.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace System.Runtime.InteropServices
{
    public struct GCHandle : IEquatable<GCHandle>
    {
        // Slot index + 1, shifted left once; the low bit marks a pinned
        // handle, as in BCL. Zero is "not allocated".
        private IntPtr _handle;

        private GCHandle(object? value, GCHandleType type)
        {
            if ((uint)type > (uint)GCHandleType.Pinned)
                throw new ArgumentOutOfRangeException(nameof(type), "Enum value was out of legal range.");

            IntPtr handle = InternalAlloc(value);
            if (type == GCHandleType.Pinned)
                handle = (IntPtr)((nint)handle | 1);
            _handle = handle;
        }

        private GCHandle(IntPtr handle) => _handle = handle;

        public static GCHandle Alloc(object? value) => new GCHandle(value, GCHandleType.Normal);

        public static GCHandle Alloc(object? value, GCHandleType type) => new GCHandle(value, type);

        public void Free()
        {
            IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            ThrowIfInvalid(handle);
            InternalFree(GetHandleValue(handle));
        }

        public object? Target
        {
            get
            {
                IntPtr handle = _handle;
                ThrowIfInvalid(handle);
                return InternalGet(GetHandleValue(handle));
            }
            set
            {
                IntPtr handle = _handle;
                ThrowIfInvalid(handle);
                InternalSet(GetHandleValue(handle), value);
            }
        }

        public unsafe IntPtr AddrOfPinnedObject()
        {
            IntPtr handle = _handle;
            ThrowIfInvalid(handle);
            if (!IsPinned(handle))
                throw new InvalidOperationException("Handle is not pinned.");

            object? target = InternalGet(GetHandleValue(handle));
            if (target is null)
                return default;

            // Where BCL asks for the string's first char, the array's first
            // element or the object's first field: the same offsets, read
            // off this runtime's layout (MethodTable pointer, then the
            // length word for strings and arrays; an MD array's bounds block
            // after that).
            byte* address = *(byte**)Unsafe.AsPointer(ref target);
            if (target is string)
                return (IntPtr)(address + 12);
            if (target is Array array)
                return (IntPtr)(address + 16 + (array.Rank == 1 ? 0 : 8 * array.Rank));
            return (IntPtr)(address + 8);
        }

        public bool IsAllocated => (nint)_handle != 0;

        public static explicit operator GCHandle(IntPtr value) => FromIntPtr(value);

        public static GCHandle FromIntPtr(IntPtr value)
        {
            ThrowIfInvalid(value);
            return new GCHandle(value);
        }

        public static explicit operator IntPtr(GCHandle value) => ToIntPtr(value);

        public static IntPtr ToIntPtr(GCHandle value) => value._handle;

        public override int GetHashCode() => _handle.GetHashCode();

        public override bool Equals([NotNullWhen(true)] object? o) => o is GCHandle other && Equals(other);

        public bool Equals(GCHandle other) => _handle == other._handle;

        public static bool operator ==(GCHandle a, GCHandle b) => (nint)a._handle == (nint)b._handle;

        public static bool operator !=(GCHandle a, GCHandle b) => (nint)a._handle != (nint)b._handle;

        private static IntPtr GetHandleValue(IntPtr handle) => new IntPtr((nint)handle & ~(nint)1);

        private static bool IsPinned(IntPtr handle) => ((nint)handle & 1) != 0;

        private static void ThrowIfInvalid(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
                throw new InvalidOperationException("Handle is not initialized.");
        }

        // The table. Without an initialiser, so no class constructor: filled on
        // first use, under s_lock.
        private static object?[] s_slots = null!;
        private static int[] s_nextFree = null!;
        private static int s_firstFree = -1;
        private static int s_used;
        private static int s_lock;

        private static void Enter()
        {
            SpinWait spin = default;
            while (Interlocked.CompareExchange(ref s_lock, 1, 0) != 0)
                spin.SpinOnce();
        }

        private static void Exit() => Volatile.Write(ref s_lock, 0);

        private static IntPtr InternalAlloc(object? value)
        {
            Enter();
            try
            {
                int slot;
                if (s_firstFree >= 0)
                {
                    slot = s_firstFree;
                    s_firstFree = s_nextFree[slot];
                }
                else
                {
                    if (s_slots == null)
                    {
                        s_slots = new object?[16];
                        s_nextFree = new int[16];
                    }
                    else if (s_used == s_slots.Length)
                    {
                        var slots = new object?[s_slots.Length * 2];
                        var next = new int[slots.Length];
                        Array.Copy(s_slots, slots, s_used);
                        Array.Copy(s_nextFree, next, s_used);
                        s_slots = slots;
                        s_nextFree = next;
                    }
                    slot = s_used++;
                }
                s_slots[slot] = value;
                s_nextFree[slot] = -2;
                return (IntPtr)((nint)(slot + 1) << 1);
            }
            finally
            {
                Exit();
            }
        }

        private static int SlotOf(IntPtr handle)
        {
            int slot = (int)((nint)handle >> 1) - 1;
            if ((uint)slot >= (uint)s_used || s_nextFree[slot] != -2)
                throw new InvalidOperationException("Handle is not initialized.");
            return slot;
        }

        private static void InternalFree(IntPtr handle)
        {
            Enter();
            try
            {
                int slot = SlotOf(handle);
                s_slots[slot] = null;
                s_nextFree[slot] = s_firstFree;
                s_firstFree = slot;
            }
            finally
            {
                Exit();
            }
        }

        private static object? InternalGet(IntPtr handle)
        {
            Enter();
            try
            {
                return s_slots[SlotOf(handle)];
            }
            finally
            {
                Exit();
            }
        }

        private static void InternalSet(IntPtr handle, object? value)
        {
            Enter();
            try
            {
                s_slots[SlotOf(handle)] = value;
            }
            finally
            {
                Exit();
            }
        }
    }
}
