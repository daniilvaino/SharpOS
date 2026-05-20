namespace OS.Kernel.Threading
{
    // Phase E7 -- registry of live Process instances + PID allocator.
    // Single-CPU cooperative: no locks required (only one execution
    // context touches the list at a time). When preemption / SMP land,
    // wrap the head walk in a Mutex (E6 infra ready).
    //
    // Process objects are managed-heap allocated and tracked via the
    // singly-linked `Process.Next` field; head is `s_head`. Removal is
    // O(N) which is fine while live-process count stays low.
    internal static unsafe class ProcessTable
    {
        private static Process? s_head;
        private static uint s_nextPid = 1;

        public static Process? Head => s_head;

        // Allocate a fresh Process, register, return. Caller is expected
        // to immediately Spawn the primary thread (Process.Launch does
        // this in one step).
        public static Process Allocate(string? name)
        {
            Process p = new Process
            {
                Id = s_nextPid++,
                Name = name,
                State = ProcessLifecycle.Active,
                ExitCode = 0,
                FirstThread = null,
                ThreadCount = 0,
                Next = s_head,
            };
            s_head = p;
            return p;
        }

        // Remove `p` from the registry. Called either explicitly by the
        // launcher post-reap or implicitly when no observer cares about
        // the Zombie process anymore (future GC pass).
        public static bool Unregister(Process p)
        {
            if (p == null) return false;
            if (s_head == p)
            {
                s_head = p.Next;
                p.Next = null;
                return true;
            }
            Process? prev = s_head;
            while (prev != null && prev.Next != p)
                prev = prev.Next;
            if (prev == null) return false;
            prev.Next = p.Next;
            p.Next = null;
            return true;
        }

        public static Process? FindById(uint pid)
        {
            Process? c = s_head;
            while (c != null)
            {
                if (c.Id == pid) return c;
                c = c.Next;
            }
            return null;
        }

        // Count alive processes (non-Zombie). Diagnostic / probe use.
        public static uint LiveCount()
        {
            uint n = 0;
            Process? c = s_head;
            while (c != null)
            {
                if (c.State != ProcessLifecycle.Zombie) n++;
                c = c.Next;
            }
            return n;
        }
    }
}
