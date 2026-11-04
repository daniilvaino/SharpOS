// Ported from SerenityOS Kernel/Devices/Audio/IntelHDA/RingBuffer.h
// (commit e5966bd, BSD-2-Clause, (c) 2023 Jelle Raaijmakers; see
// LICENSE-SerenityOS.txt). Cuts (step247): the template over (u32, Output) /
// (u64, Input) becomes two classes sharing a base; ErrorOr -> bool; DMA page
// from DmaMemory (identity-mapped, 128-byte aligned as a page is); debug lines.

namespace OS.Hal.Audio.Hda
{
    // 4.4.1, 4.4.2: CORB and RIRB.
    internal abstract unsafe class HdaRingBuffer
    {
        // 3.3: CORB/RIRB register set, relative to the buffer's window.
        protected const ulong LowerBaseAddress = 0x0;
        protected const ulong UpperBaseAddress = 0x4;
        protected const ulong WritePointer = 0x8;
        protected const ulong ReadPointer = 0xa;
        protected const ulong ResponseInterruptCount = 0xa;
        protected const ulong Control = 0xc;
        protected const ulong Size = 0xe;

        private const ushort PointerReset = 1 << 15;          // 3.3.21, 3.3.27
        private const byte DmaEnable = 1 << 1;                // 3.3.22, 3.3.29
        private const byte Supports2 = 1 << 0, Supports16 = 1 << 1, Supports256 = 1 << 2;   // 3.3.24, 3.3.31

        protected readonly HdaRegisters Registers;
        protected readonly ulong Buffer;
        public readonly int Capacity;
        protected byte CurrentPointer;
        private bool _running;

        protected HdaRingBuffer(HdaRegisters registers, ulong buffer, int capacity)
        {
            Registers = registers;
            Buffer = buffer;
            Capacity = capacity;
            _running = (registers.Read8(Control) & DmaEnable) != 0;
        }

        protected abstract bool IsInput { get; }

        // 3.3.24, 3.3.31: the largest size the controller offers; 0 for none.
        protected static int ReadCapacity(HdaRegisters registers)
        {
            byte sizeCapability = (byte)(registers.Read8(Size) >> 4);
            int capacity = (sizeCapability & Supports2) != 0 ? 2 : 0;
            if ((sizeCapability & Supports16) != 0) capacity = 16;
            if ((sizeCapability & Supports256) != 0) capacity = 256;
            return capacity;
        }

        // 4.4.1.3, 4.4.2.2: Initializing the CORB/RIRB.
        public bool RegisterWithController()
        {
            if (!SetDmaEngineRunning(false)) return false;

            Registers.Write32(LowerBaseAddress, (uint)(Buffer & 0xffffff80u));
            Registers.Write32(UpperBaseAddress, (uint)(Buffer >> 32));

            byte bufferSize = (byte)(Registers.Read8(Size) & ~0b11);
            byte sizeCapability = (byte)(bufferSize >> 4);
            if ((sizeCapability & (sizeCapability - 1)) != 0)        // more than one size offered
            {
                if (Capacity == 16) bufferSize |= 0b01;
                else if (Capacity == 256) bufferSize |= 0b10;
                Registers.Write8(Size, bufferSize);
            }

            if (!ResetControllerPointer()) return false;
            if (!IsInput) SetWritePointer(0);

            // SerenityOS: QEMU's HDA compares RINTCNT with the responses sent
            // even with interrupts off; 0xff lets 255 through.
            if (IsInput) Registers.Write16(ResponseInterruptCount, 0xff);

            return SetDmaEngineRunning(true);
        }

        protected byte ControllerPointer()
            => (byte)(Registers.Read16(IsInput ? WritePointer : ReadPointer) & 0xffu);

        private bool ResetControllerPointer()
        {
            ulong offset = IsInput ? WritePointer : ReadPointer;
            Registers.Write16(offset, PointerReset);
            if (!IsInput)
            {
                // 3.3.21: hardware reports the reset as 1, then software clears it and reads 0.
                if (!WaitPointer(offset, set: true)) return false;
                Registers.Write16(offset, 0);
                if (!WaitPointer(offset, set: false)) return false;
            }
            return true;
        }

        private bool WaitPointer(ulong offset, bool set)
        {
            ulong deadline = HdaTiming.Deadline(HdaTiming.ControllerTimeoutMicroseconds);
            uint spins = 0;
            while (((Registers.Read16(offset) & PointerReset) != 0) != set)
                if (HdaTiming.Expired(deadline, ref spins)) return false;
            return true;
        }

        private bool SetDmaEngineRunning(bool running)
        {
            if (_running == running) return true;
            byte control = Registers.Read8(Control);
            control = running ? (byte)(control | DmaEnable) : (byte)(control & ~DmaEnable);
            Registers.Write8(Control, control);

            ulong deadline = HdaTiming.Deadline(HdaTiming.ControllerTimeoutMicroseconds);
            uint spins = 0;
            while (((Registers.Read8(Control) & DmaEnable) != 0) != running)
                if (HdaTiming.Expired(deadline, ref spins)) return false;
            _running = running;
            return true;
        }

        protected void SetWritePointer(byte pointer)
        {
            // 3.3.20: CORBWP.
            Registers.Write16(WritePointer, pointer);
            CurrentPointer = pointer;
        }

        protected static ulong AllocateBuffer() => DmaMemory.AllocPages(1);
    }

    /// <summary>CORB: commands to the codecs, 32 bits each.</summary>
    internal sealed unsafe class CommandOutboundRingBuffer : HdaRingBuffer
    {
        private CommandOutboundRingBuffer(HdaRegisters r, ulong buffer, int capacity) : base(r, buffer, capacity) { }
        protected override bool IsInput => false;

        public static CommandOutboundRingBuffer Create(HdaRegisters registers)
        {
            int capacity = ReadCapacity(registers);
            ulong buffer = capacity == 0 ? 0 : AllocateBuffer();
            return buffer == 0 ? null : new CommandOutboundRingBuffer(registers, buffer, capacity);
        }

        // 4.4.1.4: Transmitting Commands via the CORB. False when full.
        public bool WriteValue(uint value)
        {
            byte readPointer = ControllerPointer();
            byte writePointer = (byte)((CurrentPointer + 1) % Capacity);
            if (writePointer == readPointer) return false;
            ((uint*)Buffer)[writePointer] = value;
            SetWritePointer(writePointer);
            return true;
        }
    }

    /// <summary>RIRB: the codecs' responses, 64 bits each (response, then codec and flags).</summary>
    internal sealed unsafe class ResponseInboundRingBuffer : HdaRingBuffer
    {
        private ResponseInboundRingBuffer(HdaRegisters r, ulong buffer, int capacity) : base(r, buffer, capacity) { }
        protected override bool IsInput => true;

        public static ResponseInboundRingBuffer Create(HdaRegisters registers)
        {
            int capacity = ReadCapacity(registers);
            ulong buffer = capacity == 0 ? 0 : AllocateBuffer();
            return buffer == 0 ? null : new ResponseInboundRingBuffer(registers, buffer, capacity);
        }

        // 4.4.2: the next response, if the controller has written one.
        public bool TryReadValue(out ulong value)
        {
            value = 0;
            byte writePointer = ControllerPointer();
            if (CurrentPointer == writePointer) return false;
            CurrentPointer = (byte)((CurrentPointer + 1) % Capacity);
            value = ((ulong*)Buffer)[CurrentPointer];
            return true;
        }
    }
}
