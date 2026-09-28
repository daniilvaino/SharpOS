namespace OS.Hal.Usb
{
    // USB mass storage over bulk endpoints: the transport half.
    //
    // Bulk-Only Transport is three phases — a 31-byte command block out, an
    // optional data phase, a 13-byte status block in. The SCSI commands that
    // ride inside it live in UsbMassStorage; this file only moves bytes.
    //
    // Which endpoints belong to storage is decided in XhciInterfaces, which
    // walks the configuration descriptor once for every function on the slot.
    internal sealed unsafe partial class XhciController
    {
        private const byte CLASS_MSD = 8;
        private const byte SUBCLASS_SCSI = 6;
        private const byte PROTOCOL_BOT = 0x50;

        /// <summary>One bulk transfer in either direction, start to finish.</summary>
        public bool TryBulkTransfer(uint slotId, bool directionIn,
                                           void* buffer, uint length, out uint residue)
        {
            residue = 0;
            int di = IndexOfSlot(slotId);
            if (di < 0) return false;

            ref Device d = ref _devices[di];
            // Actually filled now. It was declared from the start, set to zero
            // and never written, because the event's residue was discarded
            // before it reached here — so every caller that asked how much of
            // its buffer was real was told "all of it" whatever happened.
            return directionIn
                ? TryBulkOn(slotId, ref d.BulkIn, buffer, length, 5000, out residue)
                : TryBulkOn(slotId, ref d.BulkOut, buffer, length, 5000, out residue);
        }

        /// <summary>
        /// The same, on the CDC-ACM data endpoints. Separate because a slot
        /// can be both a stick and a serial port, and the two must not share
        /// a ring: one enqueue index cannot serve two endpoints.
        /// </summary>
        public bool TryCdcTransfer(uint slotId, bool directionIn,
                                          void* buffer, uint length, uint timeoutMs)
        {
            int di = IndexOfSlot(slotId);
            if (di < 0) return false;

            ref Device d = ref _devices[di];
            if (!d.HasCdc) return false;
            return directionIn
                ? TryBulkOn(slotId, ref d.CdcIn, buffer, length, timeoutMs)
                : TryBulkOn(slotId, ref d.CdcOut, buffer, length, timeoutMs);
        }

        /// <summary>
        /// Bulk IN that reports how much arrived, and leaves the transfer in
        /// flight when nothing has yet.
        /// </summary>
        /// <remarks>
        /// The length comes from the event's residue: one TRB per TD here, so
        /// `asked - residue` is exactly what the device sent.
        ///
        /// One read is outstanding at a time and it survives between calls, so
        /// a poll that finds the port silent costs a look at the event ring and
        /// nothing else. It used to withdraw the TRB instead, which is two
        /// command-ring round trips per empty poll; see the timeout branch.
        /// </remarks>
        public bool TryCdcRead(uint slotId, void* buffer, uint length,
                               uint timeoutMs, out uint received)
        {
            received = 0;
            int di = IndexOfSlot(slotId);
            if (di < 0) return false;

            ref Device d = ref _devices[di];
            if (!d.HasCdc || d.CdcIn.Ring == 0 || d.CdcIn.ReadBuffer == 0) return false;

            // A completion somebody else's wait absorbed: the data is already
            // in our page, and waiting again would be waiting for a second
            // packet that may never come.
            if (d.CdcIn.ReadPending)
            {
                d.CdcIn.ReadPending = false;
                received = CopyFromReadBuffer(ref d.CdcIn, buffer, length);
                return received != 0;
            }

            if (!d.CdcIn.ReadOutstanding)
            {
                uint want = d.CdcIn.MaxPacket;
                if (!TryQueueBulk(slotId, ref d.CdcIn, (void*)d.CdcIn.ReadBuffer, want))
                    return false;

                d.CdcIn.ReadOutstanding = true;
                d.CdcIn.ReadLength = want;
            }

            if (!TryWaitEvent(TRB_TRANSFER_EVENT, slotId, timeoutMs,
                              out uint code, out _, out uint residue))
            {
                // Left queued on purpose, and this is the point of the rewrite.
                // Cancelling here cost a Stop Endpoint and a Set TR Dequeue -
                // two command-ring round trips, each with its own wait - on
                // every poll that found nothing. With the serial pump asking
                // twenty-five times a second and the port usually silent, that
                // was the machine's whole idle time: [idlewait] read
                // usb=1.07e9 ticks a window with screen and progwrite at zero,
                // and a Terminal.Gui program looked like it was repainting in
                // slow motion because it only got the processor in between.
                //
                // A queued interrupt-in transfer nobody is waiting for costs
                // nothing. It is how the keyboard has always worked.
                _lastCode = 0;
                return false;
            }

            d.CdcIn.ReadOutstanding = false;
            _lastCode = code;
            if (code != 1 && code != 13) return false;

            uint queued = d.CdcIn.ReadLength;
            d.CdcIn.ReadLength = residue <= queued ? queued - residue : 0;
            received = CopyFromReadBuffer(ref d.CdcIn, buffer, length);
            return received != 0;
        }

        /// <summary>What the last completion left in the endpoint's page.</summary>
        private static uint CopyFromReadBuffer(ref BulkEp ep, void* destination, uint capacity)
        {
            uint n = ep.ReadLength < capacity ? ep.ReadLength : capacity;
            byte* src = (byte*)ep.ReadBuffer;
            byte* dst = (byte*)destination;
            for (uint i = 0; i < n; i++) dst[i] = src[i];
            return n;
        }

        // One Normal TRB on a bulk ring, and the doorbell. Split out of
        // TryBulkOn so a read can queue without committing to wait for it
        // forever: the reader needs to know where the ring stood beforehand,
        // in case it has to take the transfer back.
        private bool TryQueueBulk(uint slotId, ref BulkEp ep, void* buffer, uint length)
        {
            if (ep.Ring == 0) return false;

            ulong phys = (ulong)buffer;

            uint* trb = (uint*)(ep.Ring + ep.Enqueue * TrbSize);
            trb[0] = (uint)phys;
            trb[1] = (uint)(phys >> 32);
            trb[2] = length;
            trb[3] = (TRB_NORMAL << 10) | TRB_IOC | ep.Cycle;

            ep.Enqueue++;
            if (ep.Enqueue >= RingTrbs - 1)
            {
                uint* link = (uint*)(ep.Ring + (RingTrbs - 1) * TrbSize);
                link[0] = (uint)ep.Ring;
                link[1] = (uint)(ep.Ring >> 32);
                link[2] = 0;
                link[3] = (TRB_LINK << 10) | TRB_TOGGLE_CYCLE | ep.Cycle;
                ep.Enqueue = 0;
                ep.Cycle ^= 1;
            }

            Write32(_doorbellBase + slotId * 4, ep.Dci);
            return true;
        }

        private bool TryBulkOn(uint slotId, ref BulkEp ep,
                                      void* buffer, uint length, uint timeoutMs)
            => TryBulkOn(slotId, ref ep, buffer, length, timeoutMs, out _);

        private bool TryBulkOn(uint slotId, ref BulkEp ep,
                                      void* buffer, uint length, uint timeoutMs,
                                      out uint residue)
        {
            residue = 0;
            if (!TryQueueBulk(slotId, ref ep, buffer, length)) return false;

            // Filtered by slot: an unfiltered wait here would be completed by
            // a keypress and return with a half-filled buffer.
            if (!TryWaitEvent(TRB_TRANSFER_EVENT, slotId, timeoutMs,
                              out uint code, out _, out residue))
            {
                // No event at all. Recorded as 0 so a timeout is told apart
                // from a device that answered with a refusal.
                _lastCode = 0;
                return false;
            }

            _lastCode = code;

            // 13 is a short packet: the device sent less than asked, which for
            // a status block or a partial read is normal, not an error.
            return code == 1 || code == 13;
        }
    }
}
