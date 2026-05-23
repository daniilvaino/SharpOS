using System.Runtime.InteropServices;
using OS.Hal;
using OS.Kernel;

namespace OS.Boot
{
    // Owns the boot-stack switch sequence. Called once from `Boot.Entry`
    // (EfiEntry.cs) after `Platform.Init` (so BootInfo is already in the
    // Platform static) but BEFORE `KernelMain.Start` — so the entire
    // BootSequence runs on our owned stack.
    //
    // After Activate():
    //   * RSP points into BootStackPool [Base..Top).
    //   * Old UEFI stack region is abandoned (never written to again).
    //   * The continuation OnNewStack runs KernelMain.Start(bootInfo)
    //     reading bootInfo from Platform.GetBootInfo().
    //
    // Activate() never returns. If shellcode patching fails, Panic.
    internal static unsafe class BootStackSwitch
    {
        private static bool s_done;

        public static bool IsDone => s_done;

        // Stack bounds exposed for Scheduler.Init / HwFaultBridge so
        // Thread.StackBase/Top get populated for the wrapped boot
        // thread (previously null — boot thread had "no owned stack").
        public static byte* OwnedStackBase => BootStackPool.Base;
        public static byte* OwnedStackTop  => BootStackPool.Top;
        public static uint  OwnedStackBytes => (uint)BootStackPool.Size;

        public static void Activate()
        {
            if (s_done)
                Panic.Fail("BootStackSwitch.Activate called twice");

            if (!BootStackSwitchPatcher.TryInstall())
                Panic.Fail("BootStackSwitchPatcher failed");

            byte* newRspTop = BootStackPool.Top;
            // Align to 16 (paranoid; pool is naturally aligned by ILC
            // .bss layout, but the shellcode `push 0` only produces the
            // ABI-required RSP%16==8 if the starting RSP is %16==0).
            newRspTop = (byte*)((ulong)newRspTop & ~15UL);

            // Mark done BEFORE the switch — after the switch the old
            // stack frame is gone, so this write must already be in
            // memory. (It IS in memory — s_done is a static field.)
            s_done = true;

            delegate* unmanaged<byte*, delegate* unmanaged<void>, void> fn =
                (delegate* unmanaged<byte*, delegate* unmanaged<void>, void>)
                    BootStackSwitchStub.GetMethodAddress();

            fn(newRspTop, &OnNewStack);

            // shellcode does not return
            Panic.Fail("BootStackSwitchRaw returned (impossible)");
        }

        // Continuation. Runs on the new stack. Must never return — the
        // shellcode pushed a fake return-address of 0; a `ret` here
        // would jump to 0 (#GP / triple-fault).
        [UnmanagedCallersOnly]
        private static void OnNewStack()
        {
            // Hand off to the normal kernel entry. BootInfo lives in
            // Platform's static (set by Platform.Init before the switch).
            KernelMain.Start(Platform.GetBootInfo());

            // KernelMain.Start usually returns (boot finishes); the
            // original Boot.Entry then called Platform.Shutdown/Halt.
            // Re-do that here.
            Platform.Shutdown();
            Platform.Halt();

            // Halt() never returns; safety net.
            while (true) ;
        }
    }
}
