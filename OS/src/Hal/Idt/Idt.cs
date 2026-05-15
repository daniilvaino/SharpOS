using System.Runtime.InteropServices;
using OS.Boot;

namespace OS.Hal.Idt
{
    // Top-level IDT installer. Wires the 32 CPU-reserved exception vectors
    // through trampolines into a managed dispatcher that prints PanicDump
    // and halts. Higher vectors (32..255) are left zero — until SUPER-Phase
    // 5 wires hardware IRQ/MSI delivery.
    //
    // Buffer layout (single 8 KiB EfiLoaderCode allocation, IdtExecBuffer):
    //   0..4095        IDT (256 × 16-byte gate descriptors)
    //   4096..4191     Common stub
    //   4192..4703     Per-vector entry stubs (32 × 16 bytes)
    //   4704..4707     LIDT helper (4 bytes: lidt [rcx]; ret)
    //   4712..4715     SIDT helper (4 bytes: sidt [rcx]; ret)
    //
    // Hardware interrupt strategy: we own vectors 0..31 (CPU exceptions) but
    // copy UEFI's IDT entries for vectors 32..255 verbatim into ours. That
    // way the firmware's interrupt handlers (PS/2 keyboard on IRQ 1, RTC,
    // PIT, etc.) keep firing, so UEFI Console Input keeps populating its
    // key buffer, and our launcher's `ReadKeyStroke` polls work. Without
    // this copy, the first IRQ delivers to a stub and EOI is never sent —
    // every subsequent IRQ is suppressed by the PIC, breaking keyboard.
    //
    // We also DO NOT issue CLI before LIDT — interrupts must keep flowing
    // to UEFI's handlers. Since LIDT is atomic, there's no race window:
    // pre-LIDT firmware IDT, post-LIDT our IDT with the firmware tail.
    //
    // Boot ordering: Idt.Install() runs as the very first thing in
    // KernelMain.Start, before any heap or pager work — so a fault during
    // KernelHeap.Init or pager setup gives a readable panic instead of
    // triple-fault.
    internal static unsafe class Idt
    {
        private const ushort KernelCodeSelector = 0x38; // UEFI long-mode CS
        private const uint LidtHelperOffset =
            IdtTrampolines.VectorStubsOffset +
            IdtTrampolines.VectorStubsTotalSize; // 4704
        private const uint SidtHelperOffset = LidtHelperOffset + 8; // 4712, 8-byte aligned

        private static bool s_installed;
        private static byte* s_buffer;

        public static bool IsInstalled => s_installed;

        public static bool Install(BootInfo bootInfo)
        {
            if (s_installed)
                return true;

            if (bootInfo.IdtExecBuffer == null ||
                bootInfo.IdtExecBufferSize < IdtTrampolines.TotalBufferSize + 16)
            {
                return false;
            }

            byte* buffer = (byte*)bootInfo.IdtExecBuffer;

            // 1. Common stub — must exist before per-vector stubs (they
            //    encode rel32 to its address).
            byte* commonStub = buffer + IdtTrampolines.CommonStubOffset;
            delegate* unmanaged<InterruptFrame*, void> dispatcher = &Dispatch;
            IdtTrampolines.WriteCommonStub(commonStub, (void*)dispatcher, out _);

            // 2. Per-vector entry stubs.
            for (int vec = 0; vec < (int)IdtTrampolines.VectorCount; vec++)
            {
                byte* stub = buffer + IdtTrampolines.VectorStubsOffset +
                             (uint)vec * IdtTrampolines.VectorStubSize;
                IdtTrampolines.WriteVectorStub(stub, vec, commonStub);
            }

            // 3. LIDT/SIDT helper shellcode.
            //    LIDT: 0F 01 19 (lidt [rcx]); SIDT: 0F 01 09 (sidt [rcx]).
            //    Both followed by C3 (ret). Win64 first arg in rcx points to
            //    a 10-byte IdtRegister (limit + base).
            byte* lidtHelper = buffer + LidtHelperOffset;
            lidtHelper[0] = 0x0F;
            lidtHelper[1] = 0x01;
            lidtHelper[2] = 0x19;
            lidtHelper[3] = 0xC3;

            byte* sidtHelper = buffer + SidtHelperOffset;
            sidtHelper[0] = 0x0F;
            sidtHelper[1] = 0x01;
            sidtHelper[2] = 0x09;
            sidtHelper[3] = 0xC3;

            // 4. Read UEFI's current IDTR via SIDT.
            var storeIdt = (delegate* unmanaged<IdtRegister*, void>)sidtHelper;
            IdtRegister uefiIdtr;
            uefiIdtr.Limit = 0;
            uefiIdtr.Base = 0;
            storeIdt(&uefiIdtr);

            // 5. Build OUR IDT.
            //    Vectors 0..31  → per-vector exception trampolines → PanicDump.
            //    Vectors 32..255 → copy UEFI's entries verbatim, so firmware
            //                      keyboard/timer/RTC handlers keep firing.
            IdtDescriptor* idt = (IdtDescriptor*)buffer;
            IdtDescriptor* uefiIdt = (IdtDescriptor*)uefiIdtr.Base;
            uint uefiEntryCount = (uint)((uefiIdtr.Limit + 1) / 16);

            for (int vec = 0; vec < 256; vec++)
            {
                if (vec < (int)IdtTrampolines.VectorCount)
                {
                    byte* stub = buffer + IdtTrampolines.VectorStubsOffset +
                                 (uint)vec * IdtTrampolines.VectorStubSize;
                    IdtDescriptor.Build(
                        &idt[vec],
                        stub,
                        KernelCodeSelector,
                        ist: 0,
                        typeAttr: IdtDescriptor.TypeInterruptGate);
                }
                else if (uefiIdt != null && (uint)vec < uefiEntryCount)
                {
                    idt[vec] = uefiIdt[vec];
                }
                else
                {
                    idt[vec] = default;
                }
            }

            // 6. LIDT — install our IDT atomically.
            IdtRegister idtr;
            idtr.Limit = (ushort)(256 * 16 - 1);   // 0xFFF
            idtr.Base = (ulong)idt;

            var loadIdt = (delegate* unmanaged<IdtRegister*, void>)lidtHelper;
            loadIdt(&idtr);

            s_buffer = buffer;
            s_installed = true;
            return true;
        }

        // Managed dispatcher invoked by the common stub via Win64 ABI.
        // RCX = InterruptFrame* (pointer to current stack-saved snapshot).
        //
        // Phase 1 step 10: для supported CPU exception vectors (#PF, #DE,
        // etc.) build managed exception + PAL + ExInfo и call DispatchEx.
        // На success (catch found) Dispatch transfers control via mov rsp+jmp;
        // we never return. На unsupported vector or unhandled exception
        // fall through to PanicDump (legacy path).
        [UnmanagedCallersOnly]
        private static void Dispatch(InterruptFrame* frame)
        {
            int vector = (int)frame->Vector;

            // Demand-fault completion for the VM window: a NOT-present #PF
            // whose CR2 is inside the demand-mapped reservation is a lazy
            // commit, not a fault. Back the page and IRETQ-resume the
            // faulting instruction. Scoped to the window — anything else
            // falls through to the normal exception/panic path unchanged.
            if (vector == 14 && (frame->ErrorCode & 1UL) == 0 &&
                OS.Kernel.Memory.VirtualMemory.TryDemandCommit(frame->Cr2))
            {
                if (OS.Hal.X64Asm.TryResumeFrame(frame))
                    return;                 // iretq — does not return
                // Resume stub unavailable: page IS now backed, but we can't
                // resume — fall through (will surface as AV; should not happen
                // once the exec buffer is wired, i.e. by the time CLR runs).
            }

            if (OS.Boot.EH.HwFaultBridge.IsSupported(vector))
            {
                OS.Boot.EH.HwFaultBridge.DispatchTrap(frame);
                // DispatchTrap does not return на success.
            }

            PanicDump.Print(frame);
            while (true) { }
        }
    }
}
