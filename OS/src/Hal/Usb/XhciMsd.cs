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
            return directionIn
                ? TryBulkOn(slotId, ref d.BulkIn, buffer, length, 5000)
                : TryBulkOn(slotId, ref d.BulkOut, buffer, length, 5000);
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

        private bool TryBulkOn(uint slotId, ref BulkEp ep,
                                      void* buffer, uint length, uint timeoutMs)
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

            // Filtered by slot: an unfiltered wait here would be completed by
            // a keypress and return with a half-filled buffer.
            if (!TryWaitEvent(TRB_TRANSFER_EVENT, slotId, timeoutMs, out uint code, out _))
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
