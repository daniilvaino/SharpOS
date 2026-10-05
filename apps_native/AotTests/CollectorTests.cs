using System;
using SharpOS.AppSdk;
using SharpOS.Std.NoRuntime;
using SharpOS.Std.Pipes;
using SharpOS.Std.Pipes.Probe;

namespace AotTests
{
    // The places a collection is hardest to get right, each with the stress
    // mode switched on for the length of the check (GcStress: a collection
    // before every allocation, the heap walked around each, freed blocks
    // poisoned). Under --gc-stress the whole battery runs that way; these run
    // so in every battery.
    internal static unsafe partial class AppEntry
    {
        private sealed class Probe2
        {
            public int Value;
        }

        private sealed class TwoFields
        {
            public int A;
            public int B;
            public long C;
        }

        private sealed class Link
        {
            public int Value;
            public Link Next;
        }

        private static void CheckCollectorPaths()
        {
            uint every = GcStress.Every;
            bool verify = GcStress.VerifyHeap;
            bool poison = GcSweep.PoisonFreed;
            GcStress.Every = 1;
            GcStress.VerifyHeap = true;
            GcSweep.PoisonFreed = true;
            try
            {
                CheckCollectionInFaultPath();
                CheckCollectionInBarrierRefusal();
                CheckInteriorReferences();
                CheckWorkersUnderStress();
            }
            finally
            {
                GcStress.Every = every;
                GcStress.VerifyHeap = verify;
                GcSweep.PoisonFreed = poison;
            }
        }

        private static int[] MakeHeld(int seed)
        {
            int[] a = new int[16];
            for (int i = 0; i < a.Length; i++) a[i] = seed * 100 + i;
            return a;
        }

        private static bool HeldIntact(int[] a, int seed)
        {
            if (a == null || a.Length != 16) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != seed * 100 + i) return false;
            return true;
        }

        // Allocations that would land in any block a collection wrongly freed.
        private static void Churn(int count)
        {
            for (int i = 0; i < count; i++)
            {
                int[] filler = new int[16];
                for (int j = 0; j < filler.Length; j++) filler[j] = -1;
            }
        }

        // A null dereference: the exception object comes from the app's factory,
        // called from the kernel's fault handler — an allocation, so a
        // collection. The frames under the fault (this one, with its finally
        // and its own objects, and the caller) are reached only through the
        // fault's ExInfo.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static int FaultWithFinally(Probe2 nothing, ref bool finallyOk)
        {
            int[] mine = MakeHeld(7);
            try
            {
                return nothing.Value + mine[0];
            }
            finally
            {
                Churn(8);
                finallyOk = HeldIntact(mine, 7);
            }
        }

        private static void CheckCollectionInFaultPath()
        {
            ulong before = GcStress.Collections;
            int[] held = MakeHeld(41);
            bool caught = false;
            bool finallyOk = false;
            bool insideCatch = false;
            try
            {
                FaultWithFinally(null, ref finallyOk);
            }
            catch (NullReferenceException e)
            {
                Churn(8);
                caught = e != null;
                insideCatch = HeldIntact(held, 41);
            }
            Churn(8);
            Check("collector: collections in a hardware fault's path (factory, finally, catch) keep the frames under the fault",
                  caught && finallyOk && insideCatch && HeldIntact(held, 41) && GcStress.Collections > before);
        }

        // The barrier refuses by faulting on purpose; the exception comes from
        // the same factory.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void StoreForeign(EchoMessage m) => m.Tag = new string('x', 2);

        private static void CheckCollectionInBarrierRefusal()
        {
            Pipe.Create<EchoMessage>(4, PipeOverflow.DropOldest, out PipeWriter<EchoMessage> w, out PipeReader<EchoMessage> r);
            w.Copy(EchoSample("g"));
            Region<EchoMessage> region = r.Receive();
            int[] held = MakeHeld(9);
            bool refused = false;
            bool intact = false;
            if (region != null)
            {
                EchoMessage m = region.Root;
                try
                {
                    StoreForeign(m);
                }
                catch (RegionReferenceException e)
                {
                    Churn(8);
                    refused = e != null;
                }
                intact = m.Tag == "g" && m.Items.Length == 4 && m.Items[2].Name == "g2";
                region.Dispose();
            }
            w.Dispose();
            r.Dispose();
            Check("collector: collections in the barrier's refusal path keep the region and the frames under it",
                  refused && intact && HeldIntact(held, 9));
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static ref int ElementOfFresh()
        {
            int[] a = MakeHeld(13);
            return ref a[5];
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static ref int FieldOfFresh()
        {
            var t = new TwoFields { A = 1, B = 77, C = 3 };
            return ref t.B;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static Span<int> SpanOfFresh() => MakeHeld(17);

        // References into the middle of an object, the only thing holding it:
        // the walk reports them as interior pointers and the marker has to
        // find the object they point into.
        private static void CheckInteriorReferences()
        {
            ref int element = ref ElementOfFresh();
            ref int field = ref FieldOfFresh();
            Span<int> span = SpanOfFresh();
            Churn(32);
            System.GC.Collect();
            Churn(32);
            bool elementOk = element == 13 * 100 + 5;
            bool fieldOk = field == 77;
            bool spanOk = span.Length == 16 && span[0] == 1700 && span[15] == 1715;
            element = 1;
            Check("collector: a ref to an array element keeps the array", elementOk && element == 1);
            Check("collector: a ref to a field keeps the object", fieldOk);
            Check("collector: a Span over a heap array keeps the array", spanOk);
        }

        private static volatile int s_workerErrors;

        private static void BuildAndVerify(int seed)
        {
            Link head = null;
            for (int i = 0; i < 150; i++)
                head = new Link { Value = seed + i, Next = head };
            int expected = seed + 149;
            for (Link l = head; l != null; l = l.Next, expected--)
                if (l.Value != expected) { s_workerErrors++; return; }
            if (expected != seed - 1) s_workerErrors++;
        }

        // Three workers and the main thread building and checking linked lists
        // at once, a collection before each allocation, on whichever thread
        // allocated: every other thread is walked where it was stopped.
        private static void CheckWorkersUnderStress()
        {
            s_workerErrors = 0;
            var a = System.Threading.Tasks.Task.Run(() => BuildAndVerify(1000));
            var b = System.Threading.Tasks.Task.Run(() => BuildAndVerify(2000));
            var c = System.Threading.Tasks.Task.Run(() => BuildAndVerify(3000));
            BuildAndVerify(4000);
            a.Wait();
            b.Wait();
            c.Wait();
            Check("collector: worker threads allocate under collection stress", s_workerErrors == 0);
        }
    }
}
