// Ported from SerenityOS Kernel/Devices/Audio/IntelHDA/Stream.{h,cpp}
// (commit e5966bd, BSD-2-Clause, (c) 2023 Jelle Raaijmakers; see
// LICENSE-SerenityOS.txt). Cuts / changes (step247):
//  - no interrupt and no blocking write: the stream runs continuously over
//    32 one-page buffers (~680 ms at 48 kHz); a kernel thread reads the link
//    position, fills buffers up to Lead ahead of it from AudioOut and clears
//    the ones played (Ac97's feeding, step246), so the stream never stops on
//    underrun and a silent program is heard as silence;
//  - the cyclic buffer is one physically contiguous DmaMemory run (identity
//    mapped) rather than a page list; no BDL interrupt-on-completion;
//  - ErrorOr -> bool; InputStream and the spinlock-protected buffer swap are
//    not needed for one fixed output.

using OS.Kernel.Audio;

namespace OS.Hal.Audio.Hda
{
    internal sealed unsafe class HdaOutputStream
    {
        // 3.3: stream descriptor registers.
        private const ulong Control = 0x00, Status = 0x03, LinkPosition = 0x04, CyclicBufferLength = 0x08,
                            LastValidIndex = 0x0c, Format = 0x12, BdlLowerBaseAddress = 0x18, BdlUpperBaseAddress = 0x1c;
        // 3.3.35.
        private const uint StreamReset = 1u << 0, StreamRun = 1u << 1;

        public const byte FixedChannel = 0;
        private const int Buffers = 32;
        private const int BufferBytes = 4096;
        private const int Lead = 4;

        private readonly HdaRegisters _registers;
        public readonly byte StreamNumber;
        private ulong _bdl, _data;
        private int _next, _cleared;
        public FormatParameters CurrentFormat;
        public bool Running { get; private set; }

        public HdaOutputStream(HdaRegisters registers, byte streamNumber)
        {
            // 3.3.35: stream 0 is reserved by convention.
            _registers = registers;
            StreamNumber = streamNumber;
        }

        private uint ReadControl() => _registers.Read32(Control) & 0xffffffu;

        private void WriteControl(uint control)
        {
            byte status = _registers.Read8(Status);
            _registers.Write32(Control, ((uint)status << 24) | ((uint)(StreamNumber & 0xf) << 20) | (control & 0xfffffu));
        }

        private bool WaitControl(uint bit, bool set)
        {
            ulong deadline = HdaTiming.Deadline(HdaTiming.ControllerTimeoutMicroseconds);
            uint spins = 0;
            while (((ReadControl() & bit) != 0) != set)
                if (HdaTiming.Expired(deadline, ref spins)) return false;
            return true;
        }

        private bool Reset()
        {
            if (Running && !Stop()) return false;
            WriteControl(ReadControl() | StreamReset);
            if (!WaitControl(StreamReset, set: true)) return false;
            WriteControl(ReadControl() & ~StreamReset);
            return WaitControl(StreamReset, set: false);
        }

        public bool Stop()
        {
            WriteControl(ReadControl() & ~StreamRun);
            if (!WaitControl(StreamRun, set: false)) return false;
            Running = false;
            return true;
        }

        public bool SetFormat(FormatParameters format)
        {
            if (!Reset()) return false;
            if (!HdaFormat.Encode(format, out ushort payload)) return false;
            _registers.Write16(Format, payload);
            CurrentFormat = format;
            return InitializeBuffer();
        }

        // 3.6.2, 3.6.3: the cyclic buffer and its descriptor list.
        private bool InitializeBuffer()
        {
            if (_data == 0)
            {
                _bdl = DmaMemory.AllocPages(1);
                _data = DmaMemory.AllocPages(Buffers * BufferBytes / 4096);
                if (_bdl == 0 || _data == 0) return false;
            }

            _registers.Write32(CyclicBufferLength, Buffers * BufferBytes);
            _registers.Write16(LastValidIndex, Buffers - 1);
            _registers.Write32(BdlLowerBaseAddress, (uint)_bdl);
            _registers.Write32(BdlUpperBaseAddress, (uint)(_bdl >> 32));

            // Entry: 64-bit address, 32-bit length, 32-bit flags (no IOC).
            for (int i = 0; i < Buffers; i++)
            {
                ulong* entry = (ulong*)(_bdl + (ulong)(i * 16));
                entry[0] = _data + (ulong)(i * BufferBytes);
                entry[1] = BufferBytes;
            }
            _next = 0;
            _cleared = 0;
            return true;
        }

        public void Start()
        {
            WriteControl(ReadControl() | StreamRun);
            Running = true;
        }

        // Ac97.Feed's rule over the link position: Lead buffers ahead of the
        // one playing filled, the ones it passed cleared.
        public void Feed()
        {
            int playing = (int)(_registers.Read32(LinkPosition) / BufferBytes) % Buffers;

            while (_cleared != playing)
            {
                ulong* p = (ulong*)(_data + (ulong)(_cleared * BufferBytes));
                for (int i = 0; i < BufferBytes / 8; i++) p[i] = 0;
                _cleared = (_cleared + 1) % Buffers;
            }

            int ahead = (_next - playing + Buffers) % Buffers;
            if (ahead == 0 || ahead > Lead + 1) _next = (playing + 1) % Buffers;
            while ((_next - playing + Buffers) % Buffers is > 0 and <= Lead)
            {
                AudioOut.Read((byte*)(_data + (ulong)(_next * BufferBytes)), BufferBytes);
                _next = (_next + 1) % Buffers;
            }
        }
    }
}
