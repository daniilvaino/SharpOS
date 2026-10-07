using OS.Kernel.Memory;
using OS.Kernel.Pipes;

namespace SharpOS.Std.Pipes
{
    // The kernel's side of the pipe transport: the kernel is a participant like
    // any app (pipe spec Р43) and calls its own transport directly, as the
    // kernel — never as the app whose thread it may be running on.
    internal static unsafe class PipeTransport
    {
        private static uint Holder => ExchangeHeap.OwnerKernel;

        public static bool Available => true;

        public static void* Allocate(ulong size) => ExchangeHeap.Allocate(size, Holder);

        public static bool Free(void* block)
            => ExchangeHeap.OwnerOf(block) == Holder && ExchangeHeap.Free(block);

        public static PipeStatus Create(uint capacity, PipeOverflow overflow, out int writer, out int reader)
            => KernelPipes.Create(Holder, capacity, overflow, out writer, out reader);

        public static PipeStatus Connect(string name, PipeRole role, uint capacity, PipeOverflow overflow,
                                         byte[] schema, ulong rootKey, out int handle, out string error)
            => KernelPipes.Connect(Holder, name, role, capacity, overflow, schema, rootKey, out handle, out error);

        public static PipeStatus Declare(int handle, byte[] schema, ulong rootKey)
            => KernelPipes.Declare(Holder, handle, schema, rootKey);

        public static PipeStatus Send(int handle, void* block, ulong length)
            => KernelPipes.Send(Holder, handle, block, length);

        public static PipeStatus Receive(int handle, bool wait, out void* block, out ulong length, out uint dropped)
            => KernelPipes.Receive(Holder, handle, wait, out block, out length, out dropped);

        public static PipeStatus Close(int handle) => KernelPipes.Close(Holder, handle);

        public static PipeStatus WaitPeer(int handle) => KernelPipes.WaitPeer(Holder, handle);

        public static PipeStatus OpenEnd(int handle, byte[] schema, ulong rootKey, out string error)
            => KernelPipes.DeclareEnd(Holder, handle, schema, rootKey, out error);

        /// <summary>The kernel is started by nobody: it has no standard ends.</summary>
        public static int StandardEnd(uint role) => 0;

        /// <summary>A line on the screen: where a message goes when there is no output to send it to.</summary>
        public static void Print(string line) => OS.Hal.Console.WriteLine(line);

        public static byte[] Schema(int handle, out ulong rootKey) => KernelPipes.SchemaOf(Holder, handle, out rootKey);
    }
}
