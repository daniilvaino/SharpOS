using OS.Hal;

namespace OS.Kernel.Process
{
    // What a running program holds that outlives its own stack and heap, and
    // the one place it is given back (pipe_plan.md "Подготовить под трубы",
    // item 2).
    //
    // A program is known here by its app generation — the number its threads
    // carry and its run ends with (Scheduler.EnterApp / LeaveApp); it is the
    // only per-run identity there is. Today the resources are exchange-heap
    // blocks; pipe ends and regions in pipe queues come next and are released
    // from the same OnAppEnded.
    //
    // The record keeps the state a process ends in, Exited or Failed, for a
    // while after: ProcessContext is overwritten the moment the parent takes
    // its slot back, so ProcessState used to stop at Running forever.
    internal static class ProcessResources
    {
        private const int Capacity = 16;

        private struct Record
        {
            public uint Generation;
            public ProcessState State;
            public int ExchangeBlocksReleased;
        }

        private static Record[] s_records;
        private static int s_next;

        /// <summary>A run begins under <paramref name="generation"/>.</summary>
        public static void OnAppStarted(uint generation)
        {
            if (generation == 0) return;
            Threading.Preemption.Suppress();
            try
            {
                s_records ??= new Record[Capacity];
                ref Record r = ref s_records[s_next];
                s_next = (s_next + 1) % Capacity;
                r.Generation = generation;
                r.State = ProcessState.Running;
                r.ExchangeBlocksReleased = 0;
            }
            finally
            {
                Threading.Preemption.Allow();
            }
        }

        /// <summary>The run ended because an unhandled exception ended it.</summary>
        public static void MarkFailed(uint generation)
        {
            Threading.Preemption.Suppress();
            try
            {
                int i = Find(generation);
                if (i >= 0) s_records[i].State = ProcessState.Failed;
            }
            finally
            {
                Threading.Preemption.Allow();
            }
        }

        /// <summary>
        /// The run is over: everything it still holds goes back, and it is
        /// recorded as Exited unless it already failed.
        /// </summary>
        public static void OnAppEnded(uint generation)
        {
            if (generation == 0) return;

            int released = OS.Kernel.Memory.ExchangeHeap.ReleaseOwner(generation);

            ProcessState state = ProcessState.Exited;
            Threading.Preemption.Suppress();
            try
            {
                int i = Find(generation);
                if (i >= 0)
                {
                    if (s_records[i].State == ProcessState.Failed) state = ProcessState.Failed;
                    s_records[i].State = state;
                    s_records[i].ExchangeBlocksReleased = released;
                }
            }
            finally
            {
                Threading.Preemption.Allow();
            }

            if (released != 0)
            {
                DebugLog.Begin(LogLevel.Info);
                UiText.Write("[proc] generation ");
                UiText.WriteUInt(generation);
                UiText.Write(state == ProcessState.Failed ? " failed" : " exited");
                UiText.Write(": exchange blocks returned ");
                UiText.WriteInt(released);
                DebugLog.EndLine();
            }
        }

        /// <summary>The state a run is in or ended in; None when it is unknown or too old.</summary>
        public static ProcessState StateOf(uint generation)
        {
            int i = Find(generation);
            return i < 0 ? ProcessState.None : s_records[i].State;
        }

        private static int Find(uint generation)
        {
            if (s_records == null || generation == 0) return -1;
            for (int i = 0; i < Capacity; i++)
                if (s_records[i].Generation == generation)
                    return i;
            return -1;
        }
    }
}
