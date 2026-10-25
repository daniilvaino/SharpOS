using OS.Boot;
using OS.Hal;
using SharpOS.Std.Pipes;
using SharpOS.Std.SystemInformation;

namespace OS.Kernel
{
    /// <summary>
    /// The kernel's side of SystemInfo (step211): what this system is and what
    /// came up, gathered from the subsystems when a program asks, and sent to
    /// it over a pipe (ProcessService op SystemInfo).
    /// </summary>
    internal static unsafe class SystemFacts
    {
        // From the firmware, copied while it still owns its memory: after
        // ExitBootServices the vendor string and the memory map are not ours
        // to read.
        private static string s_firmwareVendor = "";
        private static uint s_firmwareRevision;
        private static ulong s_usablePages;

        /// <summary>Copies what only the firmware knows. Phase 2, once statics exist.</summary>
        public static void CaptureFirmware(BootInfo bootInfo)
        {
            char* vendor = bootInfo.FirmwareVendor;
            s_firmwareVendor = vendor == null ? "" : string.FromUtf16Z(vendor, 63);
            s_firmwareRevision = bootInfo.FirmwareRevision;
            s_usablePages = MemoryDiagnostics.CountUsablePages(bootInfo.MemoryMap);
        }

        public static SystemInfo Collect()
        {
            return new SystemInfo
            {
                Build = new BuildFacts
                {
                    Id = BuildInfo.Id,
                    Fork = BuildInfo.ForkConfig,
                    Tests = OS.Kernel.Diagnostics.Probes.Tests,
                    HostedRuntime = OS.Kernel.Exec.CoreClrHost.IsRunning,
                },
                Firmware = new FirmwareFacts
                {
                    Vendor = s_firmwareVendor,
                    Revision = s_firmwareRevision,
                    BootMedium = BootDisk.SourceName,
                },
                Cpu = Cpu(),
                Memory = new MemoryFacts
                {
                    TotalMiB = s_usablePages / 256,
                    FreeMiB = PhysicalMemory.AvailablePages() / 256,
                },
                Storage = Storage(),
                Input = new[]
                {
                    new InputFacts { Kind = "ps2", Present = Ps2Keyboard.IsPresent() },
                    new InputFacts { Kind = "usb", Present = OS.Hal.Usb.UsbKeyboard.IsPresent },
                    new InputFacts { Kind = "serial", Present = Serial.IsPresent },
                },
                Display = new DisplayFacts
                {
                    Present = Framebuffer.IsAvailable,
                    Width = Framebuffer.Width,
                    Height = Framebuffer.Height,
                    Stride = Framebuffer.Stride,
                    WriteCombining = Framebuffer.IsWriteCombining,
                },
                Timers = new TimerFacts
                {
                    HpetHz = OS.Hal.Timer.Hpet.FrequencyHz,
                    ApicTimer = OS.Hal.Apic.LocalApic.IsEnabled,
                },
                Bus = new BusFacts
                {
                    PciDevices = Pci.Count,
                    XhciControllers = OS.Hal.Usb.Xhci.Count,
                    UsbDevices = UsbDevices(),
                    MsiSources = DeviceIrq.Count,
                },
                Power = new PowerFacts { PowerOff = OS.Hal.Acpi.Power.CanPowerOff },
            };
        }

        /// <summary>Sends SystemInfo to the reader waiting on the pipe <paramref name="name"/>.</summary>
        public static PipeStatus SendTo(string name)
        {
            if (PipeWriter<SystemInfo>.Connect(name, out PipeWriter<SystemInfo> writer, out _) != PipeStatus.Ok)
                return PipeStatus.Refused;
            using (writer)
                return writer.Copy(Collect());
        }

        private static CpuFacts Cpu()
        {
            uint* regs = stackalloc uint[4];
            string vendor = "";
            if (X64Asm.Cpuid(0, 0, regs))
            {
                // EBX, EDX, ECX: "Genu" "ineI" "ntel".
                char* text = stackalloc char[13];
                uint* order = stackalloc uint[3];
                order[0] = regs[1];
                order[1] = regs[3];
                order[2] = regs[2];
                for (int i = 0; i < 12; i++)
                    text[i] = (char)((order[i / 4] >> (8 * (i % 4))) & 0xFF);
                text[12] = '\0';
                vendor = string.FromUtf16Z(text, 13);
            }

            ulong features = CpuFeatures.Value;
            return new CpuFacts
            {
                Vendor = vendor,
                Processors = OS.Hal.Acpi.Madt.IsAvailable ? OS.Hal.Acpi.Madt.LocalApicCount : 1,
                Sse41 = (features & SharpOS.Std.NoRuntime.LibmPatcher.FeatureSse41) != 0,
                Avx = CpuFeatures.AvxState,
                Fma = (features & SharpOS.Std.NoRuntime.LibmPatcher.FeatureFma) != 0,
            };
        }

        private static StorageFacts[] Storage()
        {
            string kind = BootDisk.SourceName;
            if (kind == "none") return new StorageFacts[0];
            return new[]
            {
                new StorageFacts
                {
                    Kind = kind,
                    Mounted = Fat32.Mounted,
                    FileSystem = !Fat32.Mounted ? "" : Fat32.IsFat32 ? "FAT32" : "FAT16",
                },
            };
        }

        private static int UsbDevices()
        {
            int devices = 0;
            for (int i = 0; i < OS.Hal.Usb.Xhci.Count; i++)
            {
                OS.Hal.Usb.XhciController controller = OS.Hal.Usb.Xhci.Get(i);
                if (controller != null) devices += controller.DeviceCount;
            }
            return devices;
        }
    }
}
