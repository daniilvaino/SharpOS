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
            if (Framebuffer.IsAvailable)
                FbTty.Init(0x00, 0xE6, 0x78, 0x00, 0x00, 0x28);   // green on navy
            Platform.UseOwnConsole();
            Console.WriteLine("[ebs] console rerouted to own UART+FbTty");

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
            if (OS.Kernel.Diagnostics.Probes.CoreClrInit)
                BootSequence.RunCoreClrSession(Platform.GetBootInfo());

            // Production end-state: a usable OS with UEFI gone. The
            // native shell on the own substrate (PS/2 + FbTty + own
            // 16550 + own FAT) IS the end now — not a halt dead-end.
            // Headless has no keys so it idles at the prompt, exactly
            // like a real OS waiting for input (all oracles/census are
            // already on the log above).
            // Return into the normal boot: Phase 5 (the ELF launcher
            // + app-batch tests) now runs POST-EBS, reading every
            // \EFI\BOOT\*.ELF from our own FAT (TryReadFile +
            // DirectoryReadEntry are bridged to Fs.Current). No halt,
            // no UEFI — the boot just continues firmware-free.
            Console.WriteLine("[ebs] post-EBS — continuing into launcher via own FAT");
        }
    }
}
