using System;
using OS.Hal;
using OS.Kernel.Memory;
using OS.Kernel.Threading;
using SharpOS.Std.Pipes.Probe;

namespace OS.Kernel.Diagnostics
{
    // The kernel half of AotTests' collector checks: the places a collection
    // is hardest to get right, each run with a collection before every
    // allocation on the kernel heap (KernelGC.Stress(1)), the heap walked
    // around each and freed blocks poisoned.
    internal static unsafe class GcStressProbe
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

        public static void Run()
        {
            ulong before = SharpOS.Std.NoRuntime.GcStress.Collections;
            KernelGC.Stress(1);
            try
            {
                FaultPath();
                InteriorReferences();
                ByRef();
                Workers();
            }
            finally
            {
                KernelGC.Stress(Probes.GcStressEvery);
            }
            Report("collections run by these probes", SharpOS.Std.NoRuntime.GcStress.Collections > before,
                   (uint)(SharpOS.Std.NoRuntime.GcStress.Collections - before));
        }

        private static void Report(string name, bool ok, uint value)
        {
            Log.Begin(ok ? LogLevel.Info : LogLevel.Error);
            Console.Write("[gcstress] ");
            Console.Write(name);
            Console.Write(ok ? ": ok" : ": FAIL");
            Console.Write(" val=");
            Console.WriteUInt(value);
            Log.EndLine();
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

        private static void Churn(int count)
        {
            for (int i = 0; i < count; i++)
            {
                int[] filler = new int[16];
                for (int j = 0; j < filler.Length; j++) filler[j] = -1;
            }
        }

        // The kernel's exception object for a null dereference is allocated in
        // the fault handler, on the far side of the interrupt frame from the
        // frames below the fault; so are the finally's and the catch's.
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

        private static void FaultPath()
        {
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
            Report("collections in a hardware fault's path keep the frames under the fault",
                   caught && finallyOk && insideCatch && HeldIntact(held, 41),
                   (caught ? 1u : 0) | (finallyOk ? 2u : 0) | (insideCatch ? 4u : 0));
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

        private static void InteriorReferences()
        {
            ref int element = ref ElementOfFresh();
            ref int field = ref FieldOfFresh();
            Span<int> span = SpanOfFresh();
            Churn(32);
            KernelGC.Collect();
            Churn(32);
            Report("a ref to an array element keeps the array", element == 1305, (uint)element);
            Report("a ref to a field keeps the object", field == 77, (uint)field);
            Report("a Span over a heap array keeps the array", span.Length == 16 && span[0] == 1700 && span[15] == 1715,
                   (uint)span[15]);
        }

        private static void ByRef()
        {
            int result = ByRefProbe.RunOnPipe();
            Report("byref barrier copies under collection stress", result == (ByRefProbe.InsidePassed | ByRefProbe.OutsideRefused),
                   (uint)result);
        }

        private static volatile int s_workerErrors;
        private static volatile int s_workersDone;

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

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void WorkerA()
        {
            BuildAndVerify(1000);
            s_workersDone++;
            Scheduler.Exit();
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void WorkerB()
        {
            BuildAndVerify(2000);
            s_workersDone++;
            Scheduler.Exit();
        }

        // Two kernel threads and this one building and checking lists, a
        // collection before each allocation on whichever thread allocated.
        private static void Workers()
        {
            s_workerErrors = 0;
            s_workersDone = 0;
            int spawned = 0;
            if (Scheduler.Spawn(&WorkerA, 64 * 1024) != null) spawned++;
            if (Scheduler.Spawn(&WorkerB, 64 * 1024) != null) spawned++;
            BuildAndVerify(3000);
            ulong deadline = 0;
            while (s_workersDone < spawned && deadline++ < 100000)
                Scheduler.Sleep(1);
            Report("kernel threads allocate under collection stress",
                   spawned == 2 && s_workersDone == 2 && s_workerErrors == 0, (uint)s_workerErrors);
        }
    }
}
