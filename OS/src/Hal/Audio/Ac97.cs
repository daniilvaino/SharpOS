// Driver implementation adapted from MOOS by nifanfa
// (https://github.com/nifanfa/MOOS), released under the Unlicense
// (public domain).
//
// Ported from MOOS Kernel/Driver/AC97.cs (commit 6dc9964): the device ids, the
// two BARs, the cold-reset bit, the mixer reset and volumes, the buffer
// descriptor layout and the PCM-out box registers. Cuts / changes (step246):
//  - no interrupt: MOOS took the card's INTx line; ours are all MSI and there
//    is no INTx routing (no ACPI _PRT). A kernel thread polls the current
//    index instead — at 5 ms against 21 ms buffers it never falls behind;
//  - continuous ring: the last valid index always trails the current one, so
//    the DMA never stops; a buffer is filled just before it plays and cleared
//    once played, so a program that falls silent is heard as silence rather
//    than the ring repeating;
//  - codec-ready wait after cold reset, box reset waited for, 48 kHz (the
//    rate every AC'97 codec has without VRA);
//  - Allocator.Allocate -> DmaMemory (identity-mapped, below 4 GiB checked:
//    descriptors hold 32-bit addresses);
//  - Native.Out* -> PortIo; Audio.require -> OS.Kernel.Audio.AudioOut.Read.

using OS.Kernel.Audio;

namespace OS.Hal.Audio
{
    internal static unsafe class Ac97
    {
        // Intel 82801AA (ICH) AC'97 — what QEMU's -device AC97 is.
        private const ushort VendorIntel = 0x8086;
        private const ushort DeviceIch = 0x2415;

        // Native audio mixer (NAM) registers.
        private const ushort NamReset = 0x00;
        private const ushort NamMasterVolume = 0x02;
        private const ushort NamPcmOutVolume = 0x18;
        private const ushort NamFrontDacRate = 0x2C;

        // Native audio bus master (NABM): the PCM-out box and global control.
        private const ushort PoBdbar = 0x10;      // dword: buffer descriptor list base
        private const ushort PoCiv = 0x14;        // byte: current index
        private const ushort PoLvi = 0x15;        // byte: last valid index
        private const ushort PoSr = 0x16;         // word: status
        private const ushort PoCr = 0x1B;         // byte: control
        private const ushort GlobCnt = 0x2C;      // dword
        private const ushort GlobSta = 0x30;      // dword

        private const int Descriptors = 32;       // the hardware's ring size
        private const int BufferBytes = 4096;     // 1024 stereo frames, 21.3 ms at 48 kHz
        private const int Lead = 4;               // buffers filled ahead: ~85 ms
        public const int SampleRate = 48000;

        private static ushort s_nam, s_nabm;
        private static uint* s_list;               // 32 x { address, samples | flags << 16 }
        private static ulong s_buffers;            // 32 x BufferBytes, contiguous
        private static int s_next;                 // next buffer to fill
        private static int s_cleared;              // next buffer to clear once played

        public static bool Present { get; private set; }

        /// <summary>Why Start declined, for the boot line.</summary>
        public static string Missing { get; private set; } = "";

        public static bool Start()
        {
            Pci.Init();
            if (!Pci.TryFindDevice(VendorIntel, DeviceIch, out Pci.PciDev dev)) { Missing = "no device"; return false; }
            if ((dev.Bar0 & 1) == 0 || (dev.Bar1 & 1) == 0) { Missing = "BARs not in I/O space"; return false; }
            Pci.EnableIoAndBusMaster(ref dev);
            s_nam = (ushort)(dev.Bar0 & ~3u);
            s_nabm = (ushort)(dev.Bar1 & ~3u);

            // Cold reset off (bit 1 set), then the codec says it is ready.
            PortIo.Out32((ushort)(s_nabm + GlobCnt), 0x2);
            if (!WaitFor((ushort)(s_nabm + GlobSta), dword: true, 0x100, set: true)) { Missing = "codec not ready"; return false; }

            PortIo.Out16((ushort)(s_nam + NamReset), 0);           // any write resets the mixer
            PortIo.Out16((ushort)(s_nam + NamMasterVolume), 0x0000); // 0 dB, unmuted
            PortIo.Out16((ushort)(s_nam + NamPcmOutVolume), 0x0808); // 0 dB, unmuted

            ulong list = DmaMemory.AllocPages(1);
            s_buffers = DmaMemory.AllocPages(Descriptors * BufferBytes / 4096);
            if (list == 0 || s_buffers == 0) { Missing = "no memory"; return false; }
            if (s_buffers + Descriptors * BufferBytes > 0x1_0000_0000UL || list >= 0x1_0000_0000UL)
            {
                Missing = "memory above 4 GiB";
                return false;
            }

            s_list = (uint*)list;
            for (int i = 0; i < Descriptors; i++)
            {
                s_list[i * 2] = (uint)(s_buffers + (ulong)(i * BufferBytes));
                s_list[i * 2 + 1] = BufferBytes / 2;               // length in 16-bit samples; no IOC
            }

            // Reset the box, wait for it, point it at the list, run.
            PortIo.Out8((ushort)(s_nabm + PoCr), 0x02);
            if (!WaitFor((ushort)(s_nabm + PoCr), dword: false, 0x02, set: false)) { Missing = "PCM out reset stuck"; return false; }
            PortIo.Out32((ushort)(s_nabm + PoBdbar), (uint)list);
            PortIo.Out8((ushort)(s_nabm + PoLvi), Descriptors - 1);
            PortIo.Out8((ushort)(s_nabm + PoCr), 0x01);

            AudioOut.Attach(SampleRate);
            Present = true;
            OS.Kernel.Threading.Scheduler.Spawn(&FeedEntry, 16 * 1024);
            return true;
        }

        public static ushort FrontDacRate => Present ? PortIo.In16((ushort)(s_nam + NamFrontDacRate)) : (ushort)0;

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void FeedEntry()
        {
            while (true)
            {
                Feed();
                OS.Kernel.Threading.Scheduler.Sleep(5);
            }
        }

        // Keep Lead buffers ahead of the one playing filled, clear the ones it
        // has passed, and keep the last valid index just behind it so the DMA
        // never runs out of ring.
        private static void Feed()
        {
            int civ = PortIo.In8((ushort)(s_nabm + PoCiv)) % Descriptors;

            // Played: silence, so a stalled feeder is heard as a gap.
            while (s_cleared != civ)
            {
                Silence(s_cleared);
                s_cleared = (s_cleared + 1) % Descriptors;
            }

            // Filled up to Lead ahead of the one playing — never that one. At
            // the start, or after a stall long enough that the card overtook
            // us, filling starts again just after it.
            int ahead = Ahead(civ, s_next);
            if (ahead == 0 || ahead > Lead + 1) s_next = (civ + 1) % Descriptors;
            while (Ahead(civ, s_next) is > 0 and <= Lead)
            {
                AudioOut.Read((byte*)(s_buffers + (ulong)(s_next * BufferBytes)), BufferBytes);
                s_next = (s_next + 1) % Descriptors;
            }

            PortIo.Out8((ushort)(s_nabm + PoLvi), (byte)((civ + Descriptors - 1) % Descriptors));
            PortIo.Out16((ushort)(s_nabm + PoSr), 0x1C);           // clear LVBCI, BCIS, FIFOE
        }

        // How many buffers after the playing one <paramref name="index"/> is.
        private static int Ahead(int civ, int index) => (index - civ + Descriptors) % Descriptors;

        private static void Silence(int index)
        {
            ulong* p = (ulong*)(s_buffers + (ulong)(index * BufferBytes));
            for (int i = 0; i < BufferBytes / 8; i++) p[i] = 0;
        }

        // Up to 100 ms by HPET, as the xHCI waits; the spin cap keeps a counter
        // that never moves from turning a failure into a hang.
        private static bool WaitFor(ushort port, bool dword, uint mask, bool set)
        {
            ulong deadline = 0;
            if (OS.Hal.Timer.Hpet.IsInitialized)
                deadline = OS.Hal.Timer.Hpet.ReadCounter() + OS.Hal.Timer.Hpet.FrequencyHz / 10UL;
            for (uint spin = 0; spin < 50_000_000u; spin++)
            {
                uint value = dword ? PortIo.In32(port) : PortIo.In8(port);
                if (((value & mask) != 0) == set) return true;
                if (deadline != 0 && OS.Hal.Timer.Hpet.ReadCounter() >= deadline) return false;
            }
            return false;
        }
    }
}
