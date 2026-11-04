namespace SharpOS.Std.Pipes
{
    /// <summary>
    /// The kernel's pipes and exchange heap as CoreLib's kernel tier reaches
    /// them: implemented by the kernel (OS.Kernel.Pipes.KernelPipeBackendImpl)
    /// and installed at boot (<see cref="PipeTransport.Backend"/>). CoreLib is
    /// its own assembly and cannot name the kernel's types; this is its
    /// Interop for pipes. Every call is the kernel acting as itself — never as
    /// the app whose thread it may be running on.
    /// </summary>
    public abstract unsafe class KernelPipeBackend
    {
        public abstract void* Allocate(ulong size, bool zero);
        public abstract bool Free(void* block);
        public abstract PipeStatus Create(uint capacity, PipeOverflow overflow, out int writer, out int reader);
        public abstract PipeStatus Connect(string name, PipeRole role, uint capacity, PipeOverflow overflow,
                                           byte[] schema, ulong rootKey, out int handle, out string error);
        public abstract PipeStatus Declare(int handle, byte[] schema, ulong rootKey);
        public abstract PipeStatus Send(int handle, void* block, ulong length);
        public abstract PipeStatus Receive(int handle, bool wait, out void* block, out ulong length, out uint dropped);
        public abstract PipeStatus Close(int handle);
        public abstract PipeStatus Break(int handle);
        public abstract PipeStatus WaitSchema(int handle);
        public abstract PipeStatus WaitPeer(int handle);
        public abstract PipeStatus DeclareEnd(int handle, byte[] schema, ulong rootKey, out string error);
        public abstract PipeStatus Duplicate(int handle, out int copy);
        public abstract PipeStatus Attributes(int handle, uint set, uint mask, out uint attributes);
        public abstract PipeStatus NextOf(int* handles, int count, bool wait, out int index);
        public abstract byte[] SchemaOf(int handle, out ulong rootKey);
    }

    // The kernel's side of the pipe transport: the kernel is a participant like
    // any app (pipe spec Р43) and calls its own transport directly, as the
    // kernel — never as the app whose thread it may be running on.
    internal static unsafe class PipeTransport
    {
        /// <summary>The kernel's implementation; installed at boot (BootSequence, Phase 2).</summary>
        public static KernelPipeBackend Backend;

        public static bool Available => Backend != null;

        public static void* Allocate(ulong size) => Backend.Allocate(size, zero: true);

        /// <summary>Not zeroed: for a writer of every byte (Copy, step208).</summary>
        public static void* AllocateUnzeroed(ulong size) => Backend.Allocate(size, zero: false);

        public static bool Free(void* block) => Backend.Free(block);

        public static PipeStatus Create(uint capacity, PipeOverflow overflow, out int writer, out int reader)
            => Backend.Create(capacity, overflow, out writer, out reader);

        public static PipeStatus Connect(string name, PipeRole role, uint capacity, PipeOverflow overflow,
                                         byte[] schema, ulong rootKey, out int handle, out string error)
            => Backend.Connect(name, role, capacity, overflow, schema, rootKey, out handle, out error);

        public static PipeStatus Declare(int handle, byte[] schema, ulong rootKey)
            => Backend.Declare(handle, schema, rootKey);

        public static PipeStatus Send(int handle, void* block, ulong length)
            => Backend.Send(handle, block, length);

        public static PipeStatus Receive(int handle, bool wait, out void* block, out ulong length, out uint dropped)
            => Backend.Receive(handle, wait, out block, out length, out dropped);

        public static PipeStatus Close(int handle) => Backend.Close(handle);
        public static PipeStatus Break(int handle) => Backend.Break(handle);
        public static PipeStatus WaitSchema(int handle) => Backend.WaitSchema(handle);

        public static bool ExceptionInFlight
            => SharpOS.Std.NoRuntime.KernelServices.ExceptionInFlight != null
               && SharpOS.Std.NoRuntime.KernelServices.ExceptionInFlight();

        /// <summary>
        /// Closes a writing end the way the stage is leaving: normally — the
        /// reader sees the end of the stream — or, when an exception is being
        /// dispatched on this thread, broken (step205). A stage that fails
        /// halfway no longer looks like a stream that simply ended.
        /// </summary>
        public static PipeStatus End(int handle) => ExceptionInFlight ? Break(handle) : Close(handle);

        public static PipeStatus WaitPeer(int handle) => Backend.WaitPeer(handle);

        public static PipeStatus OpenEnd(int handle, byte[] schema, ulong rootKey, out string error)
            => Backend.DeclareEnd(handle, schema, rootKey, out error);

        /// <summary>The kernel is started by nobody: it has no standard ends.</summary>
        public static int StandardEnd(uint role) => 0;

        public static PipeStatus Duplicate(int handle, out int copy) => Backend.Duplicate(handle, out copy);

        public static PipeStatus Attributes(int handle, uint set, uint mask, out uint attributes)
            => Backend.Attributes(handle, set, mask, out attributes);

        public static PipeStatus NextOf(int* handles, int count, bool wait, out int index)
            => Backend.NextOf(handles, count, wait, out index);

        /// <summary>A line on the screen: where a message goes when there is no output to send it to.</summary>
        public static void Print(string line)
        {
            if (SharpOS.Std.NoRuntime.KernelServices.WriteLine != null) SharpOS.Std.NoRuntime.KernelServices.WriteLine(line);
        }

        public static void PrintText(string text)
        {
            if (SharpOS.Std.NoRuntime.KernelServices.Write != null) SharpOS.Std.NoRuntime.KernelServices.Write(text);
        }

        /// <summary>Text of an error stream: the kernel's log is its one stream.</summary>
        public static void PrintError(string text) => PrintText(text);

        public static byte[] Schema(int handle, out ulong rootKey) => Backend.SchemaOf(handle, out rootKey);
    }
}
