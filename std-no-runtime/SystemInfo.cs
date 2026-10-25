using SharpOS.Std.Pipes;

namespace SharpOS.Std.SystemInformation
{
    // What this system is and what came up (step211): the kernel fills it,
    // programs ask for it (Process.SystemInfo, SYSINFO). Both sides compile
    // this one source, so the record crosses as itself; a reader that knows
    // fewer fields reads it through a view by name, so new fields need no new
    // ABI. Replaces the boot-time PlatformCapabilities mask, which described
    // the firmware before ExitBootServices rather than the running system.

    [Message]
    public sealed class SystemInfo
    {
        public BuildFacts Build;
        public FirmwareFacts Firmware;
        public CpuFacts Cpu;
        public MemoryFacts Memory;
        public StorageFacts[] Storage;
        public InputFacts[] Input;
        public DisplayFacts Display;
        public TimerFacts Timers;
        public BusFacts Bus;
        public PowerFacts Power;
    }

    [Message]
    public sealed class BuildFacts
    {
        /// <summary>The kernel image: git sha, dirty mark, time, tag.</summary>
        public string Id;
        /// <summary>The CoreCLR fork linked in ("Release", "Debug"), "none" without one.</summary>
        public string Fork;
        /// <summary>Built with its boot-time tests and their kernel services (not -NoTests).</summary>
        public bool Tests;
        /// <summary>A hosted runtime is up: .DLL programs and PowerShell can run.</summary>
        public bool HostedRuntime;
    }

    [Message]
    public sealed class FirmwareFacts
    {
        public string Vendor;
        public uint Revision;
        /// <summary>What the system booted from: "usb", "ahci", "none".</summary>
        public string BootMedium;
    }

    [Message]
    public sealed class CpuFacts
    {
        public string Vendor;
        /// <summary>Processors the ACPI tables list; the kernel runs on one.</summary>
        public int Processors;
        public bool Sse41;
        /// <summary>The AVX register state is on and interrupts save it.</summary>
        public bool Avx;
        public bool Fma;
    }

    [Message]
    public sealed class MemoryFacts
    {
        /// <summary>Usable RAM the firmware's map reported, MiB.</summary>
        public ulong TotalMiB;
        /// <summary>Physical memory not handed out yet, MiB.</summary>
        public ulong FreeMiB;
    }

    [Message]
    public sealed class StorageFacts
    {
        /// <summary>"usb" (mass storage over xHCI) or "ahci" (SATA).</summary>
        public string Kind;
        public bool Mounted;
        /// <summary>"FAT32", "FAT16", or "" when nothing is mounted.</summary>
        public string FileSystem;
    }

    [Message]
    public sealed class InputFacts
    {
        /// <summary>"ps2", "usb", "serial".</summary>
        public string Kind;
        public bool Present;
    }

    [Message]
    public sealed class DisplayFacts
    {
        public bool Present;
        public uint Width;
        public uint Height;
        /// <summary>Pixels per scan line.</summary>
        public uint Stride;
        public bool WriteCombining;
    }

    [Message]
    public sealed class TimerFacts
    {
        public ulong HpetHz;
        public bool ApicTimer;
    }

    [Message]
    public sealed class BusFacts
    {
        public int PciDevices;
        public int XhciControllers;
        public int UsbDevices;
        /// <summary>Device interrupt sources on MSI or MSI-X.</summary>
        public int MsiSources;
    }

    [Message]
    public sealed class PowerFacts
    {
        /// <summary>ACPI S5 found in the DSDT: the machine can switch itself off.</summary>
        public bool PowerOff;
    }
}
