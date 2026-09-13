namespace OS.Hal.Usb
{
    // xHCI DMA structures and controller start: device context array, command
    // ring, event ring, scratchpad, then run.
    //
    // The milestone this aims at is a No-Op command that completes. It touches
    // every part of the path — our TRB reaches the controller, the controller
    // executes it and writes an event back into our memory — so a success here
    // means the DMA plumbing is real, not that a register accepted a write.
    internal sealed unsafe partial class XhciController
    {
        // Operational registers beyond the ones the reset path needs.
        private const uint OP_DNCTRL = 0x14;
        private const uint OP_CRCR = 0x18;
        private const uint OP_DCBAAP = 0x30;
        private const uint OP_CONFIG = 0x38;
        private const uint OP_PORTSC = 0x400;   // + 0x10 per port

        // Interrupter 0 lives at runtime + 0x20.
        private const uint IR0 = 0x20;
        private const uint IR_IMAN = 0x00;
        private const uint IR_IMOD = 0x04;
        private const uint IR_ERSTSZ = 0x08;
        private const uint IR_ERSTBA = 0x10;
        private const uint IR_ERDP = 0x18;

        private const uint HCSPARAMS2 = 0x08;

        private const int TrbSize = 16;
        private const int RingTrbs = 256;       // one 4 KiB page per ring

        // TRB types we use.
        private const uint TRB_LINK = 6;
        private const uint TRB_NOOP_CMD = 23;
        private const uint TRB_CMD_COMPLETE = 33;

        private const uint TRB_ENABLE_SLOT = 9;

        // PORTSC bits. The change bits are write-1-to-clear, which makes a
        // naive read-modify-write destructive: it would acknowledge changes
        // nobody has looked at yet.
        private const uint PORTSC_CCS = 1u << 0;    // current connect status
        private const uint PORTSC_PED = 1u << 1;    // port enabled
        private const uint PORTSC_PR = 1u << 4;     // port reset
        private const uint PORTSC_PP = 1u << 9;     // port power
        private const uint PORTSC_CSC = 1u << 17;   // connect status change
        private const uint PORTSC_PRC = 1u << 21;   // port reset change
        // Keep power and speed/link fields, drop every change and command bit.
        private const uint PORTSC_PRESERVE = PORTSC_PP | (0xFu << 10) | (0xFu << 5);

        private const uint TRB_CYCLE = 1u << 0;
        private const uint TRB_TOGGLE_CYCLE = 1u << 1;

        private ulong _dcbaa;
        private ulong _cmdRing;
        private ulong _eventRing;
        private ulong _erst;
        private ulong _scratchpadArray;

        private uint _cmdEnqueue;       // index into the command ring
        private uint _cmdCycle = 1;
        private uint _eventDequeue;
        private uint _eventCycle = 1;
        private uint _scratchpadCount;
        private bool _running;

        public bool IsRunning => _running;
        public uint ScratchpadCount => _scratchpadCount;

        /// <summary>
        /// Allocate the rings, point the controller at them and set it running.
        /// Requires Init() (ownership + reset) to have succeeded.
        /// </summary>
        public bool Start()
        {
            if (!_initialized) return Fail("Start before Init");
            if (_running) return true;

            // Bit 0 of PAGESIZE means 4 KiB pages are supported. Every pointer
            // below assumes that; a controller wanting something larger would
            // silently misread all of them.
            if ((_pageSize & 1) == 0)
                return Fail("controller does not support 4 KiB pages");

            _dcbaa = DmaMemory.AllocPages(1);
            _cmdRing = DmaMemory.AllocPages(1);
            _eventRing = DmaMemory.AllocPages(1);
            _erst = DmaMemory.AllocPages(1);
            if (_dcbaa == 0 || _cmdRing == 0 || _eventRing == 0 || _erst == 0)
                return Fail("DMA allocation failed");

            if (!TryAllocScratchpad())
                return Fail("scratchpad allocation failed");

            // Command ring: a Link TRB in the last slot points back to the
            // start with Toggle Cycle set, which is what makes it a ring
            // rather than a buffer that runs off its end.
            uint* link = (uint*)(_cmdRing + (RingTrbs - 1) * TrbSize);
            link[0] = (uint)_cmdRing;
            link[1] = (uint)(_cmdRing >> 32);
            link[2] = 0;
            link[3] = (TRB_LINK << 10) | TRB_TOGGLE_CYCLE | _cmdCycle;

            // Event ring segment table: one segment, our single page.
            uint* erst = (uint*)_erst;
            erst[0] = (uint)_eventRing;
            erst[1] = (uint)(_eventRing >> 32);
            erst[2] = RingTrbs;
            erst[3] = 0;

            // Slots must be enabled before the device context array is used.
            Write32(_opBase + OP_CONFIG, _maxSlots);
            Write64(_opBase + OP_DCBAAP, _dcbaa);

            // RCS = 1: the controller's consumer cycle state must match the
            // cycle bit we write into TRBs, or it sees the ring as empty.
            Write64(_opBase + OP_CRCR, _cmdRing | 1UL);

            ulong ir = _runtimeBase + IR0;
            Write32(ir + IR_ERSTSZ, 1);
            Write64(ir + IR_ERDP, _eventRing);
            Write64(ir + IR_ERSTBA, _erst);   // last: writing this arms the interrupter
            Write32(ir + IR_IMOD, 0);
            Write32(ir + IR_IMAN, 0);          // polled, no interrupts yet

            Write32(_opBase + OP_DNCTRL, 0);

            uint cmd = Read32(_opBase + OP_USBCMD);
            Write32(_opBase + OP_USBCMD, cmd | USBCMD_RS);
            if (!WaitUntil(1000, _opBase + OP_USBSTS, USBSTS_HCH, expectSet: false))
                return Fail("controller would not leave the halted state");

            _running = true;
            return true;
        }

        // Some controllers demand a block of scratch pages for their own use;
        // the count comes from HCSPARAMS2 and is often zero on emulators and
        // non-zero on real hardware. Skipping it there corrupts the controller's
        // private state rather than failing cleanly.
        private bool TryAllocScratchpad()
        {
            uint hcs2 = Read32(_mmio + HCSPARAMS2);
            uint hi = (hcs2 >> 21) & 0x1F;
            uint lo = (hcs2 >> 27) & 0x1F;
            _scratchpadCount = (hi << 5) | lo;

            ulong* dcbaa = (ulong*)_dcbaa;
            if (_scratchpadCount == 0)
            {
                dcbaa[0] = 0;
                return true;
            }

            // The array of pointers is itself DMA memory, and entry 0 of the
            // device context array points at it.
            uint arrayPages = (_scratchpadCount * 8u + 4095u) / 4096u;
            _scratchpadArray = DmaMemory.AllocPages(arrayPages);
            if (_scratchpadArray == 0) return false;

            ulong* slots = (ulong*)_scratchpadArray;
            for (uint i = 0; i < _scratchpadCount; i++)
            {
                ulong page = DmaMemory.AllocPages(1);
                if (page == 0) return false;
                slots[i] = page;
            }

            dcbaa[0] = _scratchpadArray;
            return true;
        }

        /// <summary>
        /// Post a No-Op command and wait for its completion event. Proves the
        /// full round trip: our TRB out, the controller's event back.
        /// </summary>
        public bool TryNoOpCommand(out uint completionCode)
        {
            completionCode = 0;
            if (!_running) return false;

            uint* trb = (uint*)(_cmdRing + _cmdEnqueue * TrbSize);
            trb[0] = 0;
            trb[1] = 0;
            trb[2] = 0;
            // The cycle bit goes last: it is what hands the TRB over, and the
            // controller may read the rest the instant it flips.
            trb[3] = (TRB_NOOP_CMD << 10) | _cmdCycle;

            AdvanceCommandRing();

            // Doorbell 0 is the command ring's.
            Write32(_doorbellBase, 0);

            return TryWaitEvent(TRB_CMD_COMPLETE, 1000, out completionCode, out _);
        }

        /// <summary>
        /// Ask the controller for a device slot. The slot id it returns is the
        /// handle every later command for that device is addressed by.
        /// </summary>
        public bool TryEnableSlot(out uint slotId, out uint completionCode)
        {
            slotId = 0;
            completionCode = 0;
            if (!_running) return false;

            uint* trb = (uint*)(_cmdRing + _cmdEnqueue * TrbSize);
            trb[0] = 0;
            trb[1] = 0;
            trb[2] = 0;
            trb[3] = (TRB_ENABLE_SLOT << 10) | _cmdCycle;

            AdvanceCommandRing();
            Write32(_doorbellBase, 0);

            if (!TryWaitEvent(TRB_CMD_COMPLETE, 1000, out completionCode, out uint control))
                return false;

            slotId = (control >> 24) & 0xFF;
            return true;
        }

        /// <summary>
        /// Reset a port and wait for it to come up enabled.
        ///
        /// USB 2.0 ports arrive connected but disabled — nothing can be talked
        /// to until the reset completes, and the change bits must be written
        /// back to clear them (they are write-1-to-clear, so a read-modify-write
        /// that ignores them would clear ones we never looked at).
        /// </summary>
        public bool TryResetPort(uint port)
        {
            if (!_running || port >= _maxPorts) return false;

            ulong sc = _opBase + OP_PORTSC + port * 0x10;
            uint value = Read32(sc);
            if ((value & PORTSC_CCS) == 0) return false;

            // Preserve everything except the change bits and the write-1
            // control bits we are not asking for.
            Write32(sc, (value & PORTSC_PRESERVE) | PORTSC_PR);

            if (!WaitUntil(1000, sc, PORTSC_PRC, expectSet: true))
                return false;

            value = Read32(sc);
            Write32(sc, (value & PORTSC_PRESERVE) | PORTSC_PRC | PORTSC_CSC);

            return (Read32(sc) & PORTSC_PED) != 0;
        }

        private void AdvanceCommandRing()
        {
            _cmdEnqueue++;
            // The last slot holds the Link TRB, so wrapping happens one early
            // and flips the cycle bit we produce.
            if (_cmdEnqueue >= RingTrbs - 1)
            {
                uint* link = (uint*)(_cmdRing + (RingTrbs - 1) * TrbSize);
                link[3] = (TRB_LINK << 10) | TRB_TOGGLE_CYCLE | _cmdCycle;
                _cmdEnqueue = 0;
                _cmdCycle ^= 1;
            }
        }

        private bool TryWaitEvent(uint wantType, uint timeoutMs,
                                         out uint completionCode, out uint eventControl)
            => TryWaitEvent(wantType, 0, timeoutMs, out completionCode, out eventControl);

        /// <summary>
        /// Wait for an event, optionally only for one slot (0 = any).
        ///
        /// The slot filter is load-bearing, not a refinement. Every device
        /// posts into the same event ring and a keyboard report carries the
        /// same TRB type as a disk transfer, so an unfiltered wait lets a
        /// keypress complete a storage read — the read then returns with the
        /// wrong data, and the two sides silently lose sync. Events for other
        /// slots are handed back to their owner rather than dropped.
        /// </summary>
        private bool TryWaitEvent(uint wantType, uint wantSlot, uint timeoutMs,
                                         out uint completionCode, out uint eventControl)
        {
            completionCode = 0;
            eventControl = 0;
            ulong deadline = Deadline(timeoutMs);
            ulong started = timeoutMs != 0 ? OS.Kernel.Diagnostics.PerfCounters.Now() : 0;
            int spins = 0;

            for (; spins < 50_000_000; spins++)
            {
                ulong slot = _eventRing + _eventDequeue * (ulong)TrbSize;
                uint control = Read32(slot + 12);

                // An event belongs to us only once its cycle bit matches ours;
                // until then we are looking at a stale entry from the previous
                // lap around the ring.
                if ((control & TRB_CYCLE) == _eventCycle)
                {
                    uint type = (control >> 10) & 0x3F;
                    uint status = Read32(slot + 8);

                    _eventDequeue++;
                    if (_eventDequeue >= RingTrbs)
                    {
                        _eventDequeue = 0;
                        _eventCycle ^= 1;
                    }
                    Write64(_runtimeBase + IR0 + IR_ERDP,
                            _eventRing + _eventDequeue * (ulong)TrbSize);

                    uint eventSlotId = (control >> 24) & 0xFF;
                    if (type == wantType && (wantSlot == 0 || eventSlotId == wantSlot))
                    {
                        completionCode = status >> 24;
                        eventControl = control;
                        CountWait(started, spins);
                        return true;
                    }

                    // Not ours: give it back to whoever queued it, so the
                    // report is not lost and its endpoint can be re-armed.
                    if (type == TRB_TRANSFER_EVENT)
                        StashTransferEvent(eventSlotId);

                    continue;   // port change and friends fall through here
                }

                // The clock every 256 turns, not every turn. The event ring is
                // ordinary memory and cheap to look at; the HPET is a device,
                // and under QEMU every read of one is an exit into the
                // emulator. A wait lasts about a hundred turns (step170), so
                // this is one or two reads instead of a hundred; the deadline
                // is seconds away and does not need more. A non-blocking poll
                // still reads it once, on its first turn.
                if ((timeoutMs == 0 || (spins & 255) == 255) && Expired(deadline))
                {
                    CountWait(started, spins);
                    return false;
                }
            }
            CountWait(started, spins);
            return false;
        }

        private static void CountWait(ulong started, int spins)
        {
            if (started == 0) return;
            OS.Kernel.Diagnostics.PerfCounters.CountTimed(
                OS.Kernel.Diagnostics.PerfCounter.UsbWaits, OS.Kernel.Diagnostics.PerfCounter.UsbWaitTicks, started);
            OS.Kernel.Diagnostics.PerfCounters.Add(OS.Kernel.Diagnostics.PerfCounter.UsbWaitSpins, spins);
        }

        /// <summary>Port status word, or 0 when the port index is out of range.</summary>
        public uint PortStatus(uint port)
        {
            if (!_initialized || port >= _maxPorts) return 0;
            return Read32(_opBase + OP_PORTSC + port * 0x10);
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void Write32(ulong address, uint value) => *(uint*)address = value;

        // 64-bit registers are written as two dwords, low half first: some
        // controllers latch on the high write, and a single qword store is not
        // guaranteed to be decoded as one access.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private void Write64(ulong address, ulong value)
        {
            *(uint*)address = (uint)value;
            *(uint*)(address + 4) = (uint)(value >> 32);
        }
    }
}
