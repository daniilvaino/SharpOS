using OS.Hal;
using OS.Hal.Timer;
using OS.Kernel.Memory;

namespace OS.Kernel.Threading
{
    // Allocation and collection at the same time, with the timer switching
    // between them at will.
    //
    // This is the case preemption makes possible and cooperative scheduling
    // made impossible: a thread stopped in the MIDDLE of the allocator while
    // another thread allocates or collects. The allocator updates a free list
    // and a bump pointer in several steps; interrupting it and letting someone
    // else in leaves both in a state neither expects.
    //
    // The worker never yields — it allocates in a tight loop and verifies what
    // it got. The main thread collects repeatedly. If suppression around the
    // allocator or around the collection is missing, this is where it shows:
    // as wrong data, as a hang, or as a fault inside the heap walk.
    //
    // Two independent checks, because a corrupted allocator can still return
    // plausible memory:
    //   - every block the worker allocated read back what it wrote;
    //   - collections completed and the heap kept serving allocations after.
    internal static unsafe class PreemptedAllocProbe
    {
        private const int BlockElements = 48;

        // Hard ceiling on how much the worker may allocate. It leans on the
        // main thread to collect, and on a host where the timer ticks slowly
        // the main thread barely runs: the worker then outruns the collector
        // and eats every physical page — VirtualBox died this way, two
        // gigabytes of heap growth with the rest of boot left with nothing.
        // A healthy run needs ~130k allocations, so this bounds the damage
        // without changing what the probe measures.
        private const uint AllocationCeiling = 500_000;

        private static volatile uint s_allocations;
        private static volatile uint s_corruptions;
        private static volatile bool s_workerDone;
        private static volatile bool s_stop;

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void WorkerEntry()
        {
            while (!s_stop && s_allocations < AllocationCeiling)
            {
                byte[] block = new byte[BlockElements];
                for (int i = 0; i < BlockElements; i++) block[i] = (byte)(i ^ 0xA5);

                for (int i = 0; i < BlockElements; i++)
                {
                    if (block[i] != (byte)(i ^ 0xA5)) { s_corruptions++; break; }
                }

                s_allocations++;
            }

            s_workerDone = true;
            Scheduler.Exit();
        }

        public static void Run()
        {
            if (!OS.Hal.Apic.LocalApic.IsEnabled)
            {
                Console.WriteLine("[preempt-alloc] SKIP no timer interrupt");
                return;
            }

            s_allocations = 0;
            s_corruptions = 0;
            s_workerDone = false;
            s_stop = false;

            delegate* unmanaged<void> entry = &WorkerEntry;
            Thread? worker = Scheduler.Spawn(entry, 16 * 4096);
            if (worker == null)
            {
                Console.WriteLine("[preempt-alloc] SKIP could not spawn worker");
                return;
            }

            ulong switchesBefore = Preemption.Switches;
            ulong declinedBefore = Preemption.Declined;
            ulong gcBefore = global::OS.Kernel.Memory.KernelGC.Collections;
            Preemption.Enable();
            Scheduler.Yield();

            // Two phases against the clock, not against a loop count.
            //
            // A collection runs entirely with preemption suppressed, and with
            // a heap this size one takes hundreds of milliseconds. Collecting
            // back-to-back leaves the timer nowhere to switch: an earlier
            // version spent ~99% of the window suppressed and interleaved the
            // threads exactly once. Counting iterations cannot fix that — the
            // two quantities are measured in different units. So the
            // preemptible phase is measured in time as well.
            //
            //   phase 1  collect repeatedly       — the allocator and the
            //                                       collector overlap
            //   phase 2  spin, nothing suppressed — the timer gets room to
            //                                       switch between the threads
            uint collections = 0;
            ulong hz = Hpet.IsInitialized ? Hpet.FrequencyHz : 0;
            ulong start = hz != 0 ? Hpet.ReadCounter() : 0;
            uint spins = 0;

            if (hz != 0)
            {
                ulong collectUntil = hz / 10;      // 100 ms of collecting

                while (Hpet.ReadCounter() - start < collectUntil)
                {
                    KernelGC.Collect();
                    collections++;
                }

                // The spin window starts when collecting ends, not when the
                // probe did. Both phases used to count from the same instant,
                // so a collecting phase that overran the total budget left no
                // spin phase at all — and the spin phase is the only part that
                // runs OUTSIDE a critical section, which is where preemption
                // can actually happen. The probe then failed for want of
                // switches while reporting spins=0 and no corruption: a broken
                // measurement, not a broken system.
                ulong spinStart = Hpet.ReadCounter();
                ulong spinFor = hz / 2;            // 500 ms of spinning
                while (Hpet.ReadCounter() - spinStart < spinFor)
                {
                    spins++;
                }
            }

            s_stop = true;
            for (int i = 0; i < 1000 && !s_workerDone; i++)
                Scheduler.Yield();

            Preemption.Disable();

            // The heap must still work afterwards. A corrupted free list often
            // survives the stress and dies on the next request.
            byte[] after = new byte[BlockElements];
            after[BlockElements - 1] = 0x5A;
            bool heapAlive = after.Length == BlockElements && after[BlockElements - 1] == 0x5A;

            Console.Write("[preempt-alloc] allocs=");
            Console.WriteUInt(s_allocations);
            Console.Write(" collects=");
            Console.WriteUInt(collections);
            Console.Write(" spins=");
            Console.WriteUInt(spins);
            Console.Write(" switches=");
            Console.WriteULong(Preemption.Switches - switchesBefore);
            Console.Write(" declined=");
            Console.WriteULong(Preemption.Declined - declinedBefore);
            Console.Write(" corrupt=");
            Console.WriteUInt(s_corruptions);

            // Collections the WORKER ran, as opposed to the ones this thread
            // asked for. Each one holds preemption off for its whole duration,
            // so a worker that collects is a worker the timer cannot move —
            // which shows up here as declines rather than switches.
            ulong gcTotal = global::OS.Kernel.Memory.KernelGC.Collections - gcBefore;
            Console.Write(" gcTotal=");
            Console.WriteULong(gcTotal);
            Console.Write(" gcWorker=");
            Console.WriteULong(gcTotal >= collections ? gcTotal - collections : 0);

            // Both counters carry meaning here, and neither alone is enough:
            //   switches — the two threads really were interleaved;
            //   declined — suppression really did hold ticks off during the
            //              allocator and the collection.
            // A run with switches but no declines would mean the critical
            // sections are not being entered at all.
            const ulong MinimumSwitches = 5;

            // Hitting the ceiling means the collector never kept up, which is a
            // real finding about the host — say it rather than passing quietly.
            if (s_allocations >= AllocationCeiling)
                Console.Write(" CEILING-HIT");

            bool ok = s_allocations > 0 && s_allocations < AllocationCeiling &&
                      collections > 0 &&
                      s_corruptions == 0 && heapAlive &&
                      (Preemption.Switches - switchesBefore) >= MinimumSwitches &&
                      (Preemption.Declined - declinedBefore) > 0;
            Console.WriteLine(ok ? " PASS" : " FAIL");
        }
    }
}
