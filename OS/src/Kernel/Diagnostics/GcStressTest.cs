using OS.Hal;
using OS.Kernel.Memory;
using SharpOS.Std.NoRuntime;

namespace OS.Kernel.Diagnostics
{
    // Stress test for the conservative GC: exercises Mark traversal via
    // GcDescSeries across non-trivial object graphs, sweep of clearly dead
    // objects, and static-root rotation. Reports alloc deltas and sweep
    // stats per test; no hard asserts — conservative scan can keep a few
    // extra objects alive if their ptrs happen to linger in regs/stack.
    internal static unsafe class GcStressTest
    {
        private class Node
        {
            public Node Left;
            public Node Right;
            public byte[] Data;
        }

        // Linked list for Test 2 — avoids object[] which would require
        // covariance-check runtime helpers (RhpStelemRef) we don't provide.
        private class Chain
        {
            public Chain Next;
            public byte[] Data;
        }

        private class Payload
        {
            public byte[] Data;
            public object Next;
        }

        // Statics are `object` so GcRoots.Register(ref object) accepts them.
        private static object s_tree;
        private static object s_bag;
        private static object s_rot;

        public static void Run()
        {
            Log.Write(LogLevel.Info, "---- gc stress test begin ----");

            GcRoots.CaptureStackTop();

            ulong allocBefore = GcHeap.AllocCount;

            Test1_Tree();
            Test2_HalfLive();
            Test3_Rotation();

            Log.Begin(LogLevel.Info);
            Console.Write("stress total new allocs=");
            Console.WriteULongRaw(GcHeap.AllocCount - allocBefore);
            Log.EndLine();

            Log.Write(LogLevel.Info, "---- gc stress test end ----");
        }

        // Test 1: depth-5 binary tree = 31 Nodes + 31 byte[] = 62 live objs.
        // Mark must traverse Node.Left/Right/Data through GcDescSeries.
        private static void Test1_Tree()
        {
            Log.Write(LogLevel.Info, "-- test 1: binary tree depth 5 --");
            ulong before = GcHeap.AllocCount;

            Node root = BuildTree(5);
            s_tree = root;
            GcRoots.Register(ref s_tree);

            ulong after = GcHeap.AllocCount;

            RunGc();
            LogResult("tree", after - before);
        }

        private static Node BuildTree(int depth)
        {
            if (depth == 0) return null;
            var n = new Node();
            n.Data = new byte[8];
            n.Left = BuildTree(depth - 1);
            n.Right = BuildTree(depth - 1);
            return n;
        }

        // Test 2: chain of 50 Chain nodes + 50 byte[] held via registered
        // list head + 50 Chain/byte[] pairs allocated in a helper frame
        // that returns (ref popped off stack → unreachable).
        private static void Test2_HalfLive()
        {
            Log.Write(LogLevel.Info, "-- test 2: 50 live + 50 dead --");
            ulong before = GcHeap.AllocCount;

            Chain head = null;
            for (int i = 0; i < 50; i++)
            {
                var n = new Chain();
                n.Data = new byte[8];
                n.Next = head;
                head = n;
            }
            s_bag = head;
            GcRoots.Register(ref s_bag);

            AllocAndDiscard(50);

            ulong after = GcHeap.AllocCount;

            RunGc();
            LogResult("half", after - before);
        }

        // Separate frame so the list head ref pops off caller's stack
        // when this returns — the whole 50-Chain list becomes unreachable.
        private static void AllocAndDiscard(int count)
        {
            Chain localHead = null;
            for (int i = 0; i < count; i++)
            {
                var n = new Chain();
                n.Data = new byte[8];
                n.Next = localHead;
                localHead = n;
            }
        }

        // Test 3: 10× assign s_rot = new Payload(); each iteration orphans
        // the previous Payload + its byte[]. Final GC should keep the last
        // pair (via s_rot registration) and sweep the 9 previous pairs.
        private static void Test3_Rotation()
        {
            Log.Write(LogLevel.Info, "-- test 3: rotate 10 --");
            ulong before = GcHeap.AllocCount;

            GcRoots.Register(ref s_rot);

            for (int i = 0; i < 10; i++)
            {
                var p = new Payload();
                p.Data = new byte[8];
                s_rot = p;
            }

            ulong after = GcHeap.AllocCount;

            RunGc();
            LogResult("rot", after - before);
        }

        private static void RunGc()
        {
            GcMark.Begin();
            delegate* unmanaged<void> markFn = &GcRoots.MarkAllUnmanaged;
            GcStackSpill.Invoke(markFn);
            GcSweep.Run();
        }

        private static void LogResult(string label, ulong allocs)
        {
            Log.Begin(LogLevel.Info);
            Console.Write(label);
            Console.Write(" allocs=");
            Console.WriteULongRaw(allocs);
            Console.Write(" marked=");
            Console.WriteUIntRaw(GcMark.LastMarkedCount);
            Console.Write(" kept=");
            Console.WriteUIntRaw(GcSweep.LastKeptCount);
            Console.Write(" swept=");
            Console.WriteUIntRaw(GcSweep.LastSweptCount);
            Log.EndLine();
        }
    }
}
