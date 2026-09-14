using OS.Hal;

namespace OS.Boot
{
    // Phase C experiment — physically call ExitBootServices and prove
    // the own substrate (16550 UART + GOP framebuffer + PS/2) survives
    // without UEFI. SharpOS has never done this before; the whole
    // Phase B off-ramp existed to make it survivable.
    //
    // Order is load-bearing:
    //   1. Bring up + REROUTE Console to the own UART/FbTty BEFORE the
    //      EBS call. After EBS, UEFI ConOut is dead — any Console.* on
    //      the old path would fault, and a silent triple-fault looks
    //      exactly like a truncated log. The 16550 is pure port I/O and
    //      the FB is our identity-mapped MMIO, so both stay valid.
    //   2. Probe the memory-map size, AllocatePool a generous buffer
    //      ONCE (this is the last allocation — it invalidates the map
    //      key, which is why GetMemoryMap is re-read AFTER it).
    //   3. GetMemoryMap -> key, immediately ExitBootServices(key). If
    //      the map changed (EFI_INVALID_PARAMETER) retry: re-GetMemoryMap
    //      (no allocation between it and ExitBootServices) and call again.
    //   4. Post-EBS: NO Boot Services calls. Prove the own substrate
    //      live, then halt (the UEFI launcher cannot run without UEFI).
    //
    // Gated default-off (Probes.ExitBootServices): never-returning and
    // it tears down UEFI, so it must never run in the headless
    // regression battery.
    internal static unsafe class ExitBootServicesProbe
    {
        private const uint TimerHz = 100;

        public static void Run()
        {
            BootInfo bi = Platform.GetBootInfo();
            EFI_SYSTEM_TABLE* st = bi.SystemTable;
            if (st == null || st->BootServices == null)
            {
                Console.WriteLine("[ebs] no boot services — skipping");
                return;
            }
            EFI_BOOT_SERVICES* bs = st->BootServices;

            // 1. Own substrate + console reroute (still on UEFI here).
            Serial.Init();
            bool com3 = Serial.InitCom3();
            bool com4 = Serial.InitCom4();
            if (Framebuffer.IsAvailable)
                FbTty.Init(0x00, 0xE6, 0x78, 0x00, 0x00, 0x28);   // green on navy
            Platform.UseOwnConsole();
            Console.WriteLine("[ebs] console rerouted to own UART+FbTty");
            // Said once, in the kernel log, so a reader missing an
            // application's output knows which port to look at.
            Console.WriteLine(com3
                ? "[ebs] program output -> COM3"
                : "[ebs] no COM3 - program output stays on COM1");
            Console.WriteLine(com4
                ? "[ebs] program errors -> COM4"
                : "[ebs] no COM4 - program errors go with program output");

            // 2. Size the memory map, then one (last) allocation.
            ulong mapSize = 0, mapKey = 0, descSize = 0;
            uint descVer = 0;
            bs->GetMemoryMap(&mapSize, null, &mapKey, &descSize, &descVer);
            mapSize += 16 * (descSize == 0 ? 48 : descSize);       // growth headroom
            void* mapBuf = null;
            ulong alloc = bs->AllocatePool(EFI_MEMORY_TYPE.EfiLoaderCode, mapSize, &mapBuf);
            if (alloc != 0 || mapBuf == null)
            {
                Console.Write("[ebs] map buffer alloc failed status=0x");
                Console.WriteHex(alloc);
                Console.WriteLine("");
                Platform.Halt();
                return;
            }

            // 3. GetMemoryMap -> ExitBootServices, retry on map change.
            ulong status = 0xFFFFFFFFFFFFFFFFUL;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                ulong sz = mapSize;
                ulong gm = bs->GetMemoryMap(
                    &sz, (EFI_MEMORY_DESCRIPTOR*)mapBuf, &mapKey, &descSize, &descVer);
                if (gm != 0)
                {
                    Console.Write("[ebs] GetMemoryMap status=0x");
                    Console.WriteHex(gm);
                    Console.WriteLine("");
                    break;
                }
                status = bs->ExitBootServices(bi.ImageHandle, mapKey);
                if (status == 0) break;                 // SUCCESS
                // else: map changed since GetMemoryMap — loop & retry.
            }

            if (status != 0)
            {
                Console.Write("[ebs] ExitBootServices FAILED status=0x");
                Console.WriteHex(status);
                Console.WriteLine(" (UEFI still up, console already on own UART)");
                Platform.Halt();
                return;
            }

            // 4. POST-EBS. UEFI is gone. No Boot Services from here.
            // Hardware IRQs must not keep using the firmware-owned IDT tail
            // copied pre-EBS. Until SharpOS installs its own IRQ/APIC stack,
            // all post-EBS drivers are polling-based.
            X64Asm.Cli();

            // Real hardware halts the HPET counter across the firmware
            // teardown while leaving ENABLE_CNF set, so it cannot be spotted
            // by reading the config register. Revive it here, before anything
            // post-EBS asks the time — Hpet.Init ran back in Phase 3 with UEFI
            // alive and the counter still moving, so it had nothing to fix.
            global::OS.Hal.Timer.Hpet.EnsureRunning();

            // Self-checking oracle that the OWN substrate is bit-for-bit
            // alive without firmware — headless-deterministic.
            Console.WriteLine("[ebs] ExitBootServices OK -- POST-EBS substrate LIVE");

            // UART: re-init the own 16550 post-EBS (loopback self-test
            // inside Init) — proves the port driver works with no UEFI.
            bool uartOk = Serial.Init();
            Serial.WriteString("[ebs] direct own-UART line written after ExitBootServices\n");

            // GOP: re-render the deterministic frame via the own path
            // and assert the SAME golden as pre-EBS (Phase B#2). Equal
            // crc => the renderer/font/MMIO mapping is identical without
            // firmware.
            bool fbOk = !Framebuffer.IsAvailable || OS.Kernel.Diagnostics.FbRenderProbe.Verify();
            if (Framebuffer.IsAvailable)
                FbConsole.DrawString(40, 380, "POST ExitBootServices - own substrate LIVE",
                    FbConsole.Pack(0, 230, 120), -1, 2);

            // PS/2: controller still answers.
            byte ks = Ps2Keyboard.ReadStatus();
            bool ps2Ok = Ps2Keyboard.IsPresent();

            // HPET: counter advances without UEFI (timekeeping survives).
            // EnsureRunning also revives a counter the firmware handed over
            // halted, which is what real hardware does (see Hpet).
            bool hpetOk = !global::OS.Hal.Timer.Hpet.IsInitialized
                          || global::OS.Hal.Timer.Hpet.EnsureRunning();

            bool pass = uartOk && fbOk && ps2Ok && hpetOk;
            Console.Write("[ebsx] uart=");
            Console.Write(uartOk ? "Y" : "N");
            Console.Write(" fb=");
            Console.Write(fbOk ? "PASS" : "FAIL");
            Console.Write(" ps2=0x");
            Console.WriteHex(ks);
            Console.Write(" hpet=");
            Console.Write(hpetOk
                ? (global::OS.Hal.Timer.Hpet.WasRestarted ? "adv(restarted)" : "adv")
                : "STUCK");
            // Name the failing fields instead of a bare verdict glued to the
            // last one: "hpet=adv FAIL" reads as if hpet failed when the
            // verdict is really about the whole line.
            if (pass)
            {
                Console.WriteLine(" => PASS");
            }
            else
            {
                Console.Write(" => FAIL(");
                if (!uartOk) Console.Write("uart ");
                if (!fbOk) Console.Write("fb ");
                if (!ps2Ok) Console.Write("ps2 ");
                if (!hpetOk) Console.Write("hpet ");
                Console.WriteLine(")");
            }

            // Kept for the case where even the restart does not take: these
            // are the values that told cache/mapping apart from a genuinely
            // halted counter, and re-deriving them costs a hardware trip.
            if (!hpetOk && global::OS.Hal.Timer.Hpet.IsInitialized)
            {
                Console.Write("[ebsx] hpet cfg0=0x");
                Console.WriteHex(global::OS.Hal.Timer.Hpet.ConfigBefore);
                Console.Write(" cfg1=0x");
                Console.WriteHex(global::OS.Hal.Timer.Hpet.ConfigAfter);
                Console.Write(" ctr=0x");
                Console.WriteHex(global::OS.Hal.Timer.Hpet.ReadCounter());
                Console.Write(" base=0x");
                Console.WriteHex(global::OS.Hal.Acpi.Hpet.Base);
                Console.WriteLine(OS.Kernel.Memory.VirtualMemory.LargePageDevice
                    ? " map=large(inherited cache)" : " map=4k");

                // pte bit 4 (PCD) says whether the page really ended up
                // uncached — that is what separates stale reads from a
                // counter that is genuinely not moving.
                Console.Write("[ebsx] hpet pte=0x");
                ulong ctrVa = global::OS.Hal.Timer.Hpet.CounterAddress;
                Console.WriteHex(
                    OS.Kernel.Paging.X64PageTable.TryGetKernelLeafPte(ctrVa, out ulong ctrPte)
                        ? ctrPte : 0xDEADUL);
                Console.Write(" caps=0x");
                Console.WriteHex(global::OS.Hal.Timer.Hpet.Capabilities);
                Console.WriteLine("");
            }


            // Take ownership of interrupt delivery, now that the firmware that
            // owned it is gone. Order matters and is not interchangeable:
            //   1. mask the legacy chips, so nothing can reach the firmware
            //      handlers our IDT still carries for vectors 32..255;
            //   2. enable the local APIC and point its spurious vector at our
            //      own stub;
            //   3. wire the two vectors we will actually raise;
            //   4. calibrate against the HPET (revived just above) and arm the
            //      periodic timer.
            // Interrupts stay disabled throughout — nothing is armed until the
            // handler behind it exists.
            if (OS.Kernel.Diagnostics.Probes.OwnInterrupts)
                TakeOverInterrupts();

            // Placement matters more than it looks: everything below this
            // point — the disk stack, the filesystem, the whole CoreCLR
            // session — runs inside this same function, and the session may
            // not return. Taking the interrupts at the END of it meant they
            // were taken only in builds that skip CoreCLR; in a full boot the
            // code was never reached. Here the tick is ours before anything
            // long-running starts, which is also where the STI-after-EBS
            // defect actually mattered.
            if (OS.Kernel.Diagnostics.Probes.Preemption)
                OS.Kernel.Threading.PreemptionProbe.Run();

            if (OS.Kernel.Diagnostics.Probes.PreemptedAlloc)
                OS.Kernel.Threading.PreemptedAllocProbe.Run();

            // Profile everything from here on: the disk stack, the
            // filesystem and — the reason it exists — the CoreCLR session.
            // Reports itself every ten seconds on the serial port.
            if (OS.Kernel.Diagnostics.Probes.SampleProfiler)
                OS.Kernel.Diagnostics.Sampler.Start();

            // Own disk stack — POST-EBS only: bringing up AHCI
            // reprograms the HBA, which would corrupt UEFI FS if
            // firmware were still alive. Here UEFI is gone, so we
            // legitimately own the controller. Reads the boot disk via
            // our AHCI + RO-FAT entirely without firmware.
            OS.Kernel.Diagnostics.AhciProbe.Run();

            // Before the mount, not after: on a machine with no AHCI the boot
            // medium IS the USB stick, so it has to exist by the time anything
            // asks for a disk. Same POST-EBS rule as AHCI — taking the
            // controller from the firmware is only legitimate once it is gone.
            OS.Kernel.Diagnostics.UsbProbe.RunXhci();

            OS.Kernel.Diagnostics.FatProbe.Run();

            // From here on every console line also lands on disk. Bound after
            // the mount because that is when the disk becomes ours; everything
            // printed before this point exists only on screen.
            if (OS.Hal.BootLog.TryInit())
                Console.WriteLine("[bootlog] on disk: sharpos/bootlog.txt");
            else
            {
                Console.Write("[bootlog] unavailable reason=");
                Console.WriteUInt(OS.Hal.BootLog.FailReason);
                Console.WriteLine(" (1=create failed, 2=fragmented)");
            }
            if (OS.Kernel.Diagnostics.Probes.FatWrite)
                OS.Kernel.Diagnostics.FatWriteProbe.Run();

            // Firmware-free hosted tier: run CoreCLR HERE, post-EBS.
            // Fs.Current is the FAT mounted above, so the host's
            // [host] FileOpen -> Platform.TryReadFile loads every
            // \sharpos\* assembly from our own FAT/AHCI, no UEFI. The
            // §1 milestone if census comes up green without firmware.
            //
            // Without a boot disk it cannot start, and started anyway it only
            // fails deep in coreclr_initialize, far from the reason: say the
            // reason here instead.
            if (OS.Kernel.Diagnostics.Probes.CoreClrInit && OS.Hal.Fs.Current == null)
            {
                Console.Write("[boot] no boot disk: ");
                Console.WriteLine(OS.Hal.BootDisk.MissingReason);
                Console.WriteLine("[boot] the hosted runtime (CoreCLR, PowerShell) loads \\sharpos\\ from it: not started");
            }
            else if (OS.Kernel.Diagnostics.Probes.CoreClrInit)
            {
                // Preemption for the hosted session.
                //
                // .NET starts several threads of its own — finalizer,
                // background compilation, thread pool — and under cooperative
                // scheduling any of them holds the CPU until it happens to
                // block. The thread waiting for keyboard input then simply
                // does not run, which looks exactly like a slow startup that
                // stalls and then works.
                //
                // Gated separately from the probes: this is the first time
                // preemption is applied to code that was not written knowing
                // about it. The per-thread numbers in the [prof] line say
                // whether it changed anything — one thread holding nearly
                // every sample is the signature of the problem.
                if (OS.Kernel.Diagnostics.Probes.PreemptHostedSession)
                    OS.Kernel.Threading.Preemption.Enable();

                // Keys have to be collected while a command runs, not only
                // while the shell is asking for them. Started here because it
                // needs preemption to be useful: without it the pump would
                // wait its turn behind the very command it exists to interrupt.
                OS.Kernel.Input.InputPump.Start();

                // Tasks, and here rather than among the early thread probes for
                // a reason found the hard way: a task is a thread plus a wait,
                // and waiting means sleeping. Before the timer interrupt exists,
                // a sleeping thread can only be woken by another thread yielding
                // — so the moment every thread sleeps at once, the machine parks
                // forever. That is exactly what a waiter plus a worker do.
                OS.Kernel.Threading.TaskBackendInstaller.Install();
                if (OS.Kernel.Diagnostics.Probes.Tasks)
                    OS.Kernel.Threading.TaskProbe.Run();

                if (OS.Kernel.Diagnostics.Probes.Locks)
                    OS.Kernel.Threading.LockProbe.Run();

                BootSequence.RunCoreClrSession(Platform.GetBootInfo());

                OS.Kernel.Threading.Preemption.Disable();
            }

            // Production end-state: a usable OS with UEFI gone. The
            // native shell on the own substrate (PS/2 + FbTty + own
            // 16550 + own FAT) IS the end now — not a halt dead-end.
            // Headless has no keys so it idles at the prompt, exactly
            // like a real OS waiting for input (all oracles/census are
            // already on the log above).
            // Return into the normal boot: Phase 5 (the launcher,
            // \apps\LAUNCHER.EXE) runs POST-EBS, read from our own FAT
            // (TryReadFile + DirectoryReadEntry are bridged to
            // Fs.Current). No halt, no UEFI — the boot just continues
            // firmware-free.

            Console.WriteLine("[ebs] post-EBS — continuing into launcher via own FAT");
        }

        // Phase F1: SharpOS starts delivering its own interrupts.
        //
        // Reports what it did either way. A tick that silently fails to start
        // is indistinguishable from one that works until something waits on
        // it, and the whole point of this step is to have a clock we can trust
        // before anything is built on top of it.
        private static void TakeOverInterrupts()
        {
            Pic.MaskAll();

            if (!OS.Hal.Apic.LocalApic.Initialize(OS.Hal.Idt.Idt.SpuriousVector))
            {
                Console.WriteLine("[apic] FAIL no local APIC - staying on polling only");
                return;
            }

            if (!OS.Hal.Idt.Idt.TryWireIrqVector(OS.Hal.Idt.Idt.TimerVector) ||
                !OS.Hal.Idt.Idt.TryWireIrqVector(OS.Hal.Idt.Idt.SpuriousVector))
            {
                Console.WriteLine("[apic] FAIL could not wire vectors");
                return;
            }

            if (!OS.Hal.Apic.LocalApic.StartPeriodic(OS.Hal.Idt.Idt.TimerVector, TimerHz))
            {
                Console.WriteLine("[apic] FAIL timer calibration");
                return;
            }

            Console.Write("[apic] id=");
            Console.WriteInt((int)OS.Hal.Apic.LocalApic.Id);
            Console.Write(" ver=0x");
            Console.WriteHex(OS.Hal.Apic.LocalApic.Version);
            Console.Write(" timer=");
            Console.WriteInt((int)(OS.Hal.Apic.LocalApic.TicksPerSecond / 1000));
            Console.WriteLine(" kHz");

            // Interrupts on. Safe now, and only now: the legacy lines are
            // masked, and both vectors the APIC can raise land in our own
            // dispatcher.
            X64Asm.Sti();

            // Correct the arming against the rate actually delivered, then
            // check. Order matters: verification without correction only
            // reports that the clock is wrong.
            bool retuned = OS.Hal.Apic.LocalApic.RetuneToDeliveredRate(TimerHz);
            Console.Write("[apic] retune: ");
            Console.Write(retuned ? "in range" : "OUT OF RANGE after correction");
            // What each 100 ms window saw: a shortfall that varies from round
            // to round is lost ticks, a steady one is a clock off by a factor.
            Console.Write(" (ticks per round:");
            for (uint round = 0; round < 3; round++)
            {
                Console.Write(" ");
                Console.WriteInt((int)OS.Hal.Apic.LocalApic.LastRetuneObserved[round]);
            }
            Console.WriteLine(")");

            VerifyTickIsMoving();
        }

        // A clock is not a clock until it has been watched moving. Measured
        // against the HPET, which is independent of the APIC — checking a
        // timer against itself would confirm nothing.
        private static void VerifyTickIsMoving()
        {
            if (!global::OS.Hal.Timer.Hpet.IsInitialized) return;

            ulong before = OS.Hal.Apic.LocalApic.TimerTicks;
            ulong hpetStart = global::OS.Hal.Timer.Hpet.ReadCounter();
            ulong window = global::OS.Hal.Timer.Hpet.FrequencyHz / 10;   // 100 ms

            while (global::OS.Hal.Timer.Hpet.ReadCounter() - hpetStart < window) { }

            ulong observed = OS.Hal.Apic.LocalApic.TimerTicks - before;
            uint expected = TimerHz / 10;

            Console.Write("[apic] ticks in 100ms: ");
            Console.WriteInt((int)observed);
            Console.Write(" expected ~");
            Console.WriteInt((int)expected);

            // Generous bounds: this asks "is the clock roughly right", not
            // "is it precise". Being out by half is a wiring or calibration
            // fault; being out by a few percent is a busy loop.
            if (observed >= expected / 2 && observed <= expected * 2)
                Console.WriteLine(" PASS");
            else
                Console.WriteLine(" FAIL");
        }
    }
}
