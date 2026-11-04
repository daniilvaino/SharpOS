using OS.Kernel.Memory;
using SharpOS.Std.Pipes;

namespace OS.Kernel.Pipes
{
    /// <summary>
    /// The kernel's pipes for CoreLib's kernel tier (KernelPipeBackend):
    /// KernelPipes and the exchange heap, always as the kernel itself
    /// (ExchangeHeap.OwnerKernel). Installed into PipeTransport.Backend at boot.
    /// </summary>
    internal sealed unsafe class KernelPipeBackendImpl : KernelPipeBackend
    {
        private const uint Holder = ExchangeHeap.OwnerKernel;

        public override void* Allocate(ulong size, bool zero) => ExchangeHeap.Allocate(size, Holder, zero);

        public override bool Free(void* block)
            => ExchangeHeap.OwnerOf(block) == Holder && ExchangeHeap.Free(block);

        public override PipeStatus Create(uint capacity, PipeOverflow overflow, out int writer, out int reader)
            => KernelPipes.Create(Holder, capacity, overflow, out writer, out reader);

        public override PipeStatus Connect(string name, PipeRole role, uint capacity, PipeOverflow overflow,
                                           byte[] schema, ulong rootKey, out int handle, out string error)
            => KernelPipes.Connect(Holder, name, role, capacity, overflow, schema, rootKey, out handle, out error);

        public override PipeStatus Declare(int handle, byte[] schema, ulong rootKey)
            => KernelPipes.Declare(Holder, handle, schema, rootKey);

        public override PipeStatus Send(int handle, void* block, ulong length)
            => KernelPipes.Send(Holder, handle, block, length);

        public override PipeStatus Receive(int handle, bool wait, out void* block, out ulong length, out uint dropped)
            => KernelPipes.Receive(Holder, handle, wait, out block, out length, out dropped);

        public override PipeStatus Close(int handle) => KernelPipes.Close(Holder, handle);
        public override PipeStatus Break(int handle) => KernelPipes.Break(Holder, handle);
        public override PipeStatus WaitSchema(int handle) => KernelPipes.WaitSchema(Holder, handle);
        public override PipeStatus WaitPeer(int handle) => KernelPipes.WaitPeer(Holder, handle);

        public override PipeStatus DeclareEnd(int handle, byte[] schema, ulong rootKey, out string error)
            => KernelPipes.DeclareEnd(Holder, handle, schema, rootKey, out error);

        public override PipeStatus Duplicate(int handle, out int copy) => KernelPipes.Duplicate(Holder, handle, out copy);

        public override PipeStatus Attributes(int handle, uint set, uint mask, out uint attributes)
            => KernelPipes.Attributes(Holder, handle, set, mask, out attributes);

        public override PipeStatus NextOf(int* handles, int count, bool wait, out int index)
            => KernelPipes.NextOf(Holder, handles, count, wait, out index);

        public override byte[] SchemaOf(int handle, out ulong rootKey) => KernelPipes.SchemaOf(Holder, handle, out rootKey);
    }
}
