using OS.Kernel.Memory;

namespace OS.PAL.SharpOSHost
{
    /// <summary>
    /// The C++ catch blocks a thread is executing, innermost last.
    /// </summary>
    /// <remarks>
    /// A catch block on x64 is a funclet: SehDispatch enters it on the
    /// parent function's frame, with a return address that comes back here
    /// (<see cref="Popped"/>) before going on to the continuation. While it
    /// runs, three things need what this keeps:
    ///
    ///  - <c>throw;</c> rethrows the exception being handled, and arrives
    ///    with no object and no type information of its own;
    ///  - an exception leaving the funclet continues from the parent
    ///    function — in the parent's saved context, and in its catch state,
    ///    so the try that caught it once does not catch it again;
    ///  - the thrown object: its storage was a frame below the parent, dead
    ///    once the funclet's own calls grow over it, so it is copied here.
    ///
    /// Per thread, on the scheduler's Thread (allocated at its first catch);
    /// before the scheduler exists, one shared record.
    /// </remarks>
    internal static unsafe class CxxActiveCatch
    {
        public const int Capacity = 8;
        public const int ObjectCapacity = 256;

        internal struct Entry
        {
            public ulong ParentFrame;       // establisher frame of the function whose catch runs
            public ulong ThrowInfo;
            public ulong ImageBase;
            public ulong Object;            // address of the thrown object: ObjectCopy, or the original
            public Context ParentContext;   // the parent at catch entry: its registers, its body RSP
            public fixed byte ObjectCopy[ObjectCapacity];
        }

        private struct Stack
        {
            public int Depth;
            public int Pad;
            // Entry[Capacity] follows.
        }

        private static byte* s_beforeScheduler;

        private static Stack* Current(bool create)
        {
            OS.Kernel.Threading.Thread thread = OS.Kernel.Threading.Scheduler.Current;
            byte* storage = thread != null ? thread.ActiveCatches : s_beforeScheduler;
            if (storage == null && create)
            {
                uint bytes = (uint)(sizeof(Stack) + Capacity * sizeof(Entry));
                storage = (byte*)KernelHeap.Alloc(bytes);
                if (storage == null)
                    return null;
                for (uint i = 0; i < bytes; i++) storage[i] = 0;
                if (thread != null) thread.ActiveCatches = storage;
                else s_beforeScheduler = storage;
            }
            return (Stack*)storage;
        }

        private static Entry* At(Stack* stack, int index)
            => (Entry*)((byte*)stack + sizeof(Stack)) + index;

        public static int Depth
        {
            get
            {
                Stack* stack = Current(create: false);
                return stack == null ? 0 : stack->Depth;
            }
        }

        /// <summary>A new innermost entry, or null when the stack is full.</summary>
        public static Entry* Push()
        {
            Stack* stack = Current(create: true);
            if (stack == null || stack->Depth >= Capacity)
                return null;
            return At(stack, stack->Depth++);
        }

        /// <summary>The entry <paramref name="fromTop"/> levels below the innermost, or null.</summary>
        public static Entry* Peek(int fromTop = 0)
        {
            Stack* stack = Current(create: false);
            if (stack == null || fromTop < 0 || fromTop >= stack->Depth)
                return null;
            return At(stack, stack->Depth - 1 - fromTop);
        }

        public static void Pop()
        {
            Stack* stack = Current(create: false);
            if (stack != null && stack->Depth > 0)
                stack->Depth--;
        }

        /// <summary>
        /// Where a catch funclet returns to on its way to the continuation:
        /// the catch is over.
        /// </summary>
        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        public static void Popped() => Pop();
    }
}
