using System.Runtime.InteropServices;
using OS.Boot;

namespace OS.Hal.Idt
{
    // Top-level IDT installer. Wires the 32 CPU-reserved exception vectors
    // through trampolines into a managed dispatcher that prints PanicDump
    // and halts. Vectors we raise ourselves are wired later — see
    // TryWireIrqVector.
    //
    // Buffer layout (single 8 KiB EfiLoaderCode allocation, IdtExecBuffer):
    //   0..4095        IDT (256 × 16-byte gate descriptors)
    //   4096..4191     Common stub
    //   4192..4703     Per-vector entry stubs (32 × 16 bytes)
    //   4704..4707     LIDT helper (4 bytes: lidt [rcx]; ret)
    //   4712..4715     SIDT helper (4 bytes: sidt [rcx]; ret)
    //   4736..4991     Late-wired vector stubs (16 × 16 bytes)
    //   4992..5007     The stub for every vector nobody owns (StubUnownedVectors)
    //
    // Hardware interrupt strategy has two eras, and Install() sits in the
    // first one:
    //
    //   Before ExitBootServices the firmware is still running, and it needs
    //   its own interrupts — its console, timer and RTC handlers are live
    //   and the kernel prints through UEFI ConOut for the whole of Phase 0-3.
    //   So we own vectors 0..31 (CPU exceptions) and copy UEFI's entries for
    //   32..255 verbatim. Without the copy, the first IRQ lands on a stub,
    //   EOI is never sent, and the PIC suppresses everything after it.
    //
    //   After ExitBootServices those handlers are code without an owner.
    //   ExitBootServicesProbe masks the legacy PIC, points the vectors we
    //   raise at our own dispatcher (Phase F1) and every other one above 31
    //   at a stub of ours (StubUnownedVectors): nothing in the table leads
    //   into the firmware any more.
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

        // Vectors we raise ourselves, wired late (post-EBS, after Pic.MaskAll)
        // rather than at Install time. Installing them at boot would override
        // the firmware entries our launcher still depends on while UEFI is
        // alive — the point of keeping that tail in the first place.
        public const int TimerVector    = 0x20;
        public const int SpuriousVector = 0xFF;

        // Entry stubs for late-wired vectors. The 0..31 stubs end at 4704,
        // where the LIDT/SIDT helpers sit; this region starts past them. The
        // buffer is 8 KiB (UefiBootInfoBuilder), so there is room to spare.
        private const uint IrqStubsOffset = 4736;
        private const uint IrqStubSlots   = 16;
        private const uint IrqStubsEnd    = IrqStubsOffset + (IrqStubSlots * IdtTrampolines.VectorStubSize);

        // Every other vector above the exceptions, once UEFI is gone: one
        // stub, pushing a number no real vector has (step210).
        public const int UnexpectedVector = 0x100;
        private const uint UnexpectedStubOffset = IrqStubsEnd;

        private static bool s_installed;
        private static byte* s_buffer;
        private static uint s_bufferSize;
        private static uint s_irqStubsUsed;
        private static ulong s_wired0, s_wired1, s_wired2, s_wired3;   // vectors with a gate of ours
        private static ulong s_unexpected;

        /// <summary>Interrupts that arrived on a vector nobody owns.</summary>
        public static ulong UnexpectedCount => s_unexpected;

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
            IdtTrampolines.WriteCommonStub(commonStub, (void*)dispatcher, out uint commonStubLength);

            // The stub lives in a fixed 96-byte slot with the per-vector stubs
            // immediately after it. Until now the emitted length was discarded,
            // so growing the stub past its slot would have silently overwritten
            // vector 0's entry — a corruption that surfaces as a wild jump on
            // the first divide-by-zero, nowhere near the cause. Refuse instead.
            if (commonStubLength > IdtTrampolines.CommonStubMaxSize)
                return false;

            // 2. Per-vector entry stubs.
            for (int vec = 0; vec < (int)IdtTrampolines.VectorCount; vec++)
            {
                byte* stub = buffer + IdtTrampolines.VectorStubsOffset +
                             (uint)vec * IdtTrampolines.VectorStubSize;
                IdtTrampolines.WriteVectorStub(stub, vec, commonStub);
            }

            // 3. LIDT/SIDT helper shellcode, from the same generator as the
            //    stubs above. Win64 first arg in rcx points to a 10-byte
            //    IdtRegister (limit + base).
            byte* lidtHelper = buffer + LidtHelperOffset;
            IdtTrampolines.WriteLoadIdtHelper(lidtHelper);

            byte* sidtHelper = buffer + SidtHelperOffset;
            IdtTrampolines.WriteStoreIdtHelper(sidtHelper);

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
            s_bufferSize = bootInfo.IdtExecBufferSize;
            s_installed = true;
            return true;
        }

        /// <summary>Gives a vector's gate an interrupt stack (TSS IST slot 1..7).</summary>
        public static void SetInterruptStack(int vector, byte ist)
        {
            if (!s_installed) return;
            ((IdtDescriptor*)s_buffer)[vector].IstAndReserved = (byte)(ist & 7);
        }

        /// <summary>
        /// Switch the interrupt path to XSAVE: the common stub is rewritten in
        /// place, the per-vector stubs keep jumping to it. Called once, at
        /// boot, right after XCR0 gains AVX state and before any interrupt
        /// frame is live — a frame saved by one variant and resumed by the
        /// other would restore garbage. X64Asm.UseXsave switches the resume
        /// side.
        /// </summary>
        public static bool UseXsave()
        {
            if (!s_installed || s_buffer == null) return false;

            byte* fresh = stackalloc byte[128];
            delegate* unmanaged<InterruptFrame*, void> dispatcher = &Dispatch;
            IdtTrampolines.WriteCommonStubXsave(fresh, (void*)dispatcher, out uint length);
            if (length > IdtTrampolines.CommonStubMaxSize) return false;

            bool interrupts = X64Asm.InterruptsEnabled();
            X64Asm.Cli();
            byte* stub = s_buffer + IdtTrampolines.CommonStubOffset;
            for (uint i = 0; i < length; i++) stub[i] = fresh[i];
            if (interrupts) X64Asm.Sti();
            return true;
        }

        /// <summary>
        /// Point one vector above the exception range at our own dispatcher,
        /// replacing the firmware entry inherited at Install. Call only after
        /// UEFI is gone and the legacy PIC is masked — before that the entry
        /// being replaced is still doing useful work.
        /// </summary>
        /// <remarks>
        /// The IDT is already live, and the CPU re-reads the descriptor on
        /// every delivery, so patching an entry in place needs no reload. The
        /// vector must be masked or unused at the moment of the write, which
        /// post-EBS it is: nothing can raise it until we arm the APIC.
        /// </remarks>
        public static bool TryWireIrqVector(int vector)
        {
            if (!s_installed || s_buffer == null) return false;

            // The late-wire region sits past the LIDT/SIDT helpers; Install's
            // own size check only covers the exception stubs, so this is the
            // first thing that would run off the end of a smaller buffer.
            if (s_bufferSize < IrqStubsEnd) return false;
            if (vector < (int)IdtTrampolines.VectorCount || vector > 255) return false;
            if (s_irqStubsUsed >= IrqStubSlots) return false;

            byte* stub = s_buffer + IrqStubsOffset +
                         (s_irqStubsUsed * IdtTrampolines.VectorStubSize);
            IdtTrampolines.WriteVectorStub(stub, vector, s_buffer + IdtTrampolines.CommonStubOffset);
            s_irqStubsUsed++;

            IdtDescriptor* idt = (IdtDescriptor*)s_buffer;
            IdtDescriptor.Build(
                &idt[vector],
                stub,
                KernelCodeSelector,
                ist: 0,
                typeAttr: IdtDescriptor.TypeInterruptGate);
            MarkWired(vector);
            return true;
        }

        private static void MarkWired(int vector)
        {
            ulong bit = 1UL << (vector & 63);
            switch (vector >> 6)
            {
                case 0: s_wired0 |= bit; break;
                case 1: s_wired1 |= bit; break;
                case 2: s_wired2 |= bit; break;
                default: s_wired3 |= bit; break;
            }
        }

        private static bool IsWired(int vector)
        {
            ulong bit = 1UL << (vector & 63);
            switch (vector >> 6)
            {
                case 0: return (s_wired0 & bit) != 0;
                case 1: return (s_wired1 & bit) != 0;
                case 2: return (s_wired2 & bit) != 0;
                default: return (s_wired3 & bit) != 0;
            }
        }

        /// <summary>
        /// Points every vector above the exceptions that is not ours at one
        /// stub of ours (step210). Install copied the firmware's entries for
        /// them so its handlers kept working while it lived; once it is gone
        /// those entries lead into code without an owner, and an interrupt on
        /// one of them — a stray MSI, a misprogrammed device — would jump
        /// there. Now it is counted, said, acknowledged and returned from.
        /// Call after ExitBootServices; vectors wired later replace the stub.
        /// </summary>
        public static bool StubUnownedVectors()
        {
            if (!s_installed || s_buffer == null) return false;
            if (s_bufferSize < UnexpectedStubOffset + IdtTrampolines.VectorStubSize) return false;

            byte* stub = s_buffer + UnexpectedStubOffset;
            IdtTrampolines.WriteVectorStub(stub, UnexpectedVector, s_buffer + IdtTrampolines.CommonStubOffset);

            IdtDescriptor* idt = (IdtDescriptor*)s_buffer;
            for (int vector = (int)IdtTrampolines.VectorCount; vector < 256; vector++)
            {
                if (IsWired(vector)) continue;
                IdtDescriptor.Build(&idt[vector], stub, KernelCodeSelector, ist: 0,
                                    typeAttr: IdtDescriptor.TypeInterruptGate);
            }
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

            // Our own vectors first: they are the common case and must not go
            // anywhere near the panic path.
            //
            // Both return by IRETQ through TryResumeFrame, which is also what
            // restores the FP state the common stub saved — without that, the
            // C# executed here would leave its XMM leftovers in the code we
            // interrupted.
            if (vector == TimerVector)
            {
                // Inside the handler from here to each TryResumeFrame. The same
                // object is decremented on the way out even if the tick
                // switched threads: it is this thread's handler that returns.
                OS.Kernel.Threading.Thread? interrupted = OS.Kernel.Threading.Scheduler.Current;
                if (interrupted != null) interrupted.InterruptDepth++;

                OS.Hal.Apic.LocalApic.OnTimerTick();
                OS.Kernel.Threading.Scheduler.NoteTick();

                // If the tick lands while the scheduler owns its queues or
                // the raw CoopSwitch stub is between stacks/GS bases, do only
                // the architectural minimum. Running sampler, activation, or
                // preemption here can mutate scheduler state recursively or
                // execute managed code on a transient stack.
                if (OS.Kernel.Threading.Scheduler.SwitchInProgress)
                {
                    OS.Hal.Apic.LocalApic.EndOfInterrupt();
                    if (interrupted != null) interrupted.InterruptDepth--;
                    if (OS.Hal.X64Asm.TryResumeFrame(frame))
                        return;                 // iretq — does not return
                }

                // Where was the CPU? Recorded before anything else touches
                // the frame, and cheap enough to leave on: a hash insert.
                OS.Kernel.Diagnostics.Sampler.OnTick(frame->Rip, frame->Rsp);
                OS.Kernel.Diagnostics.Sampler.MaybeReport();

                // Acknowledge BEFORE any scheduling: the APIC treats the
                // vector as in service until it is acknowledged, so parking
                // here without an EOI would silence every later tick and
                // strand whichever thread we switch to.
                OS.Hal.Apic.LocalApic.EndOfInterrupt();

                // Activation before preemption, and independent of it: this is
                // the runtime asking us to interrupt a thread so it can stop
                // the world. It has to work whether or not we also preempt.
                OS.PAL.SharpOSHost.ThreadActivation.OnTick(frame);

                // May run other threads and return much later. The frame is on
                // this thread's own stack, so it survives untouched.
                OS.Kernel.Threading.Preemption.OnTick(frame);

                if (interrupted != null) interrupted.InterruptDepth--;
                if (OS.Hal.X64Asm.TryResumeFrame(frame))
                    return;                 // iretq — does not return
            }
            else if (vector >= DeviceIrq.FirstVector && vector < DeviceIrq.FirstVector + DeviceIrq.MaxSources)
            {
                // A device (MSI). The handler clears the device's status and
                // posts its source; the scheduler wakes the driver on its next
                // pass (IrqWait). Never switches from here.
                OS.Kernel.Threading.Thread? interrupted = OS.Kernel.Threading.Scheduler.Current;
                if (interrupted != null) interrupted.InterruptDepth++;
                DeviceIrq.Dispatch(vector);
                OS.Hal.Apic.LocalApic.EndOfInterrupt();
                if (interrupted != null) interrupted.InterruptDepth--;
                if (OS.Hal.X64Asm.TryResumeFrame(frame))
                    return;
            }
            else if (vector == UnexpectedVector)
            {
                // A vector nobody owns (StubUnownedVectors). The first few are
                // said, with where the CPU was; all are acknowledged — if the
                // APIC delivered it, it is in service until an EOI.
                OS.Kernel.Threading.Thread? interrupted = OS.Kernel.Threading.Scheduler.Current;
                if (interrupted != null) interrupted.InterruptDepth++;
                s_unexpected++;
                if (s_unexpected <= 8)
                {
                    Console.Write("[idt] unexpected interrupt on a vector nobody owns, rip=0x");
                    Console.WriteHex(frame->Rip);
                    Console.WriteLine("");
                }
                OS.Hal.Apic.LocalApic.EndOfInterrupt();
                if (interrupted != null) interrupted.InterruptDepth--;
                if (OS.Hal.X64Asm.TryResumeFrame(frame))
                    return;
            }
            else if (vector == 1)
            {
                // #DB from a hardware write watch (Diagnostics.WriteWatch):
                // logged, disarmed, resumed.
                OS.Kernel.Diagnostics.WriteWatch.OnDebugTrap(frame);
                if (OS.Hal.X64Asm.TryResumeFrame(frame))
                    return;
            }
            else if (vector == SpuriousVector)
            {
                // A spurious interrupt is one the APIC withdrew after
                // signalling it. It carries no in-service state, so it must
                // NOT be acknowledged: an EOI here would retire somebody
                // else's interrupt.
                OS.Hal.Apic.LocalApic.OnSpurious();
                if (OS.Hal.X64Asm.TryResumeFrame(frame))
                    return;
            }

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

            // A double fault, on its own stack (Tss): an app thread's stack ran
            // out — the process ends and the machine goes on (step196). Any
            // other double fault is the kernel's and panics, with the dump
            // below instead of the silent reset it used to be.
            if (vector == 8 && OS.Kernel.Exec.JumpStub.TryRecoverStackOverflow(frame))
            {
                if (OS.Hal.X64Asm.TryResumeFrame(frame))
                    return;
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
