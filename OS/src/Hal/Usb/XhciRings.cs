namespace OS.Hal.Usb
{
    // xHCI DMA structures and controller start: device context array, command
    // ring, event ring, scratchpad, then run.
    //
    // The milestone this aims at is a No-Op command that completes. It touches
    // every part of the path — our TRB reaches the controller, the controller
    // executes it and writes an event back into our memory — so a success here
    // means the DMA plumbing is real, not that a register accepted a write.
    internal static unsafe partial class Xhci
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

        private static ulong s_dcbaa;
        private static ulong s_cmdRing;
        private static ulong s_eventRing;
        private static ulong s_erst;
        private static ulong s_scratchpadArray;

        private static uint s_cmdEnqueue;       // index into the command ring
        private static uint s_cmdCycle = 1;
        private static uint s_eventDequeue;
        private static uint s_eventCycle = 1;
        private static uint s_scratchpadCount;
        private static bool s_running;

        public static bool IsRunning => s_running;
        public static uint ScratchpadCount => s_scratchpadCount;

        /// <summary>
        /// Allocate the rings, point the controller at them and set it running.
        /// Requires Init() (ownership + reset) to have succeeded.
        /// </summary>
        public static bool Start()
        {
            if (!s_initialized) return Fail("Start before Init");
            if (s_running) return true;

            // Bit 0 of PAGESIZE means 4 KiB pages are supported. Every pointer
            // below assumes that; a controller wanting something larger would
            // silently misread all of them.
            if ((s_pageSize & 1) == 0)
                return Fail("controller does not support 4 KiB pages");

            s_dcbaa = DmaMemory.AllocPages(1);
            s_cmdRing = DmaMemory.AllocPages(1);
            s_eventRing = DmaMemory.AllocPages(1);
            s_erst = DmaMemory.AllocPages(1);
            if (s_dcbaa == 0 || s_cmdRing == 0 || s_eventRing == 0 || s_erst == 0)
                return Fail("DMA allocation failed");

            if (!TryAllocScratchpad())
                return Fail("scratchpad allocation failed");

            // Command ring: a Link TRB in the last slot points back to the
            // start with Toggle Cycle set, which is what makes it a ring
            // rather than a buffer that runs off its end.
            uint* link = (uint*)(s_cmdRing + (RingTrbs - 1) * TrbSize);
            link[0] = (uint)s_cmdRing;
            link[1] = (uint)(s_cmdRing >> 32);
            link[2] = 0;
            link[3] = (TRB_LINK << 10) | TRB_TOGGLE_CYCLE | s_cmdCycle;

            // Event ring segment table: one segment, our single page.
            uint* erst = (uint*)s_erst;
            erst[0] = (uint)s_eventRing;
            erst[1] = (uint)(s_eventRing >> 32);
            erst[2] = RingTrbs;
            erst[3] = 0;

            // Slots must be enabled before the device context array is used.
            Write32(s_opBase + OP_CONFIG, s_maxSlots);
            Write64(s_opBase + OP_DCBAAP, s_dcbaa);

            // RCS = 1: the controller's consumer cycle state must match the
            // cycle bit we write into TRBs, or it sees the ring as empty.
            Write64(s_opBase + OP_CRCR, s_cmdRing | 1UL);

            ulong ir = s_runtimeBase + IR0;
            Write32(ir + IR_ERSTSZ, 1);
            Write64(ir + IR_ERDP, s_eventRing);
            Write64(ir + IR_ERSTBA, s_erst);   // last: writing this arms the interrupter
            Write32(ir + IR_IMOD, 0);
            Write32(ir + IR_IMAN, 0);          // polled, no interrupts yet

            Write32(s_opBase + OP_DNCTRL, 0);

            uint cmd = Read32(s_opBase + OP_USBCMD);
            Write32(s_opBase + OP_USBCMD, cmd | USBCMD_RS);
            if (!WaitUntil(1000, s_opBase + OP_USBSTS, USBSTS_HCH, expectSet: false))
                return Fail("controller would not leave the halted state");

            s_running = true;
            return true;
        }

        // Some controllers demand a block of scratch pages for their own use;
        // the count comes from HCSPARAMS2 and is often zero on emulators and
        // non-zero on real hardware. Skipping it there corrupts the controller's
        // private state rather than failing cleanly.
        private static bool TryAllocScratchpad()
        {
            uint hcs2 = Read32(s_mmio + HCSPARAMS2);
            uint hi = (hcs2 >> 21) & 0x1F;
            uint lo = (hcs2 >> 27) & 0x1F;
            s_scratchpadCount = (hi << 5) | lo;

            ulong* dcbaa = (ulong*)s_dcbaa;
            if (s_scratchpadCount == 0)
            {
                dcbaa[0] = 0;
                return true;
            }

            // The array of pointers is itself DMA memory, and entry 0 of the
            // device context array points at it.
            uint arrayPages = (s_scratchpadCount * 8u + 4095u) / 4096u;
            s_scratchpadArray = DmaMemory.AllocPages(arrayPages);
            if (s_scratchpadArray == 0) return false;

            ulong* slots = (ulong*)s_scratchpadArray;
            for (uint i = 0; i < s_scratchpadCount; i++)
            {
                ulong page = DmaMemory.AllocPages(1);
                if (page == 0) return false;
                slots[i] = page;
            }

            dcbaa[0] = s_scratchpadArray;
            return true;
        }

        /// <summary>
        /// Post a No-Op command and wait for its completion event. Proves the
        /// full round trip: our TRB out, the controller's event back.
        /// </summary>
        public static bool TryNoOpCommand(out uint completionCode)
        {
            completionCode = 0;
            if (!s_running) return false;

            uint* trb = (uint*)(s_cmdRing + s_cmdEnqueue * TrbSize);
            trb[0] = 0;
            trb[1] = 0;
            trb[2] = 0;
            // The cycle bit goes last: it is what hands the TRB over, and the
            // controller may read the rest the instant it flips.
            trb[3] = (TRB_NOOP_CMD << 10) | s_cmdCycle;

            AdvanceCommandRing();

            // Doorbell 0 is the command ring's.
            Write32(s_doorbellBase, 0);

            return TryWaitEvent(TRB_CMD_COMPLETE, 1000, out completionCode, out _);
        }

        /// <summary>
        /// Ask the controller for a device slot. The slot id it returns is the
        /// handle every later command for that device is addressed by.
        /// </summary>
        public static bool TryEnableSlot(out uint slotId, out uint completionCode)
        {
            slotId = 0;
            completionCode = 0;
            if (!s_running) return false;

            uint* trb = (uint*)(s_cmdRing + s_cmdEnqueue * TrbSize);
            trb[0] = 0;
            trb[1] = 0;
            trb[2] = 0;
            trb[3] = (TRB_ENABLE_SLOT << 10) | s_cmdCycle;

            AdvanceCommandRing();
            Write32(s_doorbellBase, 0);

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
        public static bool TryResetPort(uint port)
        {
            if (!s_running || port >= s_maxPorts) return false;

            ulong sc = s_opBase + OP_PORTSC + port * 0x10;
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

        private static void AdvanceCommandRing()
        {
            s_cmdEnqueue++;
            // The last slot holds the Link TRB, so wrapping happens one early
            // and flips the cycle bit we produce.
            if (s_cmdEnqueue >= RingTrbs - 1)
            {
                uint* link = (uint*)(s_cmdRing + (RingTrbs - 1) * TrbSize);
                link[3] = (TRB_LINK << 10) | TRB_TOGGLE_CYCLE | s_cmdCycle;
                s_cmdEnqueue = 0;
                s_cmdCycle ^= 1;
            }
        }

        private static bool TryWaitEvent(uint wantType, uint timeoutMs,
                                         out uint completionCode, out uint eventControl)
        {
            completionCode = 0;
            eventControl = 0;
            ulong deadline = Deadline(timeoutMs);

            for (int spins = 0; spins < 50_000_000; spins++)
            {
                ulong slot = s_eventRing + s_eventDequeue * (ulong)TrbSize;
                uint control = Read32(slot + 12);

                // An event belongs to us only once its cycle bit matches ours;
                // until then we are looking at a stale entry from the previous
                // lap around the ring.
                if ((control & TRB_CYCLE) == s_eventCycle)
                {
                    uint type = (control >> 10) & 0x3F;
                    uint status = Read32(slot + 8);

                    s_eventDequeue++;
                    if (s_eventDequeue >= RingTrbs)
                    {
                        s_eventDequeue = 0;
                        s_eventCycle ^= 1;
                    }
                    Write64(s_runtimeBase + IR0 + IR_ERDP,
                            s_eventRing + s_eventDequeue * (ulong)TrbSize);

                    if (type == wantType)
                    {
                        completionCode = status >> 24;
                        eventControl = control;
                        return true;
                    }
                    continue;   // some other event (port change) — keep looking
                }

                if (Expired(deadline)) return false;
            }
            return false;
        }

        /// <summary>Port status word, or 0 when the port index is out of range.</summary>
        public static uint PortStatus(uint port)
        {
            if (!s_initialized || port >= s_maxPorts) return 0;
            return Read32(s_opBase + OP_PORTSC + port * 0x10);
        }

        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void Write32(ulong address, uint value) => *(uint*)address = value;

        // 64-bit registers are written as two dwords, low half first: some
        // controllers latch on the high write, and a single qword store is not
        // guaranteed to be decoded as one access.
        [System.Runtime.CompilerServices.MethodImpl(
            System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static void Write64(ulong address, ulong value)
        {
            *(uint*)address = (uint)value;
            *(uint*)(address + 4) = (uint)(value >> 32);
        }
    }
}
