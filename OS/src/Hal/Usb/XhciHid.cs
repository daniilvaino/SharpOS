namespace OS.Hal.Usb
{
    // Keystrokes: poll the interrupt endpoint XhciInterfaces claimed for the
    // HID function and hand back the reports it delivers.
    //
    // Boot protocol is used deliberately. A HID device's report layout is
    // otherwise described by a report descriptor that has to be parsed —
    // a small language of its own — whereas boot protocol fixes the layout:
    // 8 bytes for a keyboard, 3+ for a mouse. That is the whole reason the
    // BIOS can drive a USB keyboard without a HID parser, and it is enough
    // for arrow keys and DOOM.
    internal sealed unsafe partial class XhciController
    {
        private const uint TRB_CONFIGURE_ENDPOINT = 12;
        private const uint TRB_NORMAL = 1;

        /// <summary>
        /// Queue one report read and wait for it. Returns the bytes the device
        /// sent into <paramref name="report"/>; false on timeout, which for an
        /// idle keyboard is the normal case.
        /// </summary>
        public bool TryReadReport(uint slotId, byte* report, int max, uint timeoutMs)
        {
            if (!TryQueueReport(slotId)) return false;
            return TryCollectReport(slotId, report, max, timeoutMs);
        }

        /// <summary>
        /// Hand the endpoint one buffer to fill. Exactly one read may be
        /// outstanding per device: queueing again before the first completes
        /// stacks up requests that all fire at once on the next keypress.
        /// </summary>
        public bool TryQueueReport(uint slotId)
        {
            int di = IndexOfSlot(slotId);
            if (di < 0) return false;

            ref Device d = ref _devices[di];
            if (!d.Configured || d.ReadOutstanding) return false;

            uint* trb = (uint*)(d.EpRing + d.EpEnqueue * TrbSize);
            trb[0] = (uint)d.ReportBuffer;
            trb[1] = (uint)(d.ReportBuffer >> 32);
            trb[2] = d.EpMaxPacket;
            trb[3] = (TRB_NORMAL << 10) | TRB_IOC | d.EpCycle;

            d.EpEnqueue++;
            if (d.EpEnqueue >= RingTrbs - 1)
            {
                uint* link = (uint*)(d.EpRing + (RingTrbs - 1) * TrbSize);
                link[0] = (uint)d.EpRing;
                link[1] = (uint)(d.EpRing >> 32);
                link[2] = 0;
                link[3] = (TRB_LINK << 10) | TRB_TOGGLE_CYCLE | d.EpCycle;
                d.EpEnqueue = 0;
                d.EpCycle ^= 1;
            }

            Write32(_doorbellBase + slotId * 4, d.EpDci);
            d.ReadOutstanding = true;
            return true;
        }

        /// <summary>
        /// Collect a queued read. timeoutMs = 0 polls without blocking.
        /// </summary>
        public bool TryCollectReport(uint slotId, byte* report, int max, uint timeoutMs)
        {
            int di = IndexOfSlot(slotId);
            if (di < 0) return false;

            ref Device d = ref _devices[di];

            // A report someone else's wait absorbed is already sitting in our
            // buffer — take it rather than waiting for another one.
            if (d.ReportPending)
            {
                d.ReportPending = false;
            }
            else
            {
                if (!d.ReadOutstanding) return false;
                if (!TryWaitEvent(TRB_TRANSFER_EVENT, slotId, timeoutMs, out uint code, out _))
                    return false;
                d.ReadOutstanding = false;
                if (code != 1 && code != 13) return false;
            }

            byte* src = (byte*)d.ReportBuffer;
            int n = d.EpMaxPacket < max ? d.EpMaxPacket : max;
            for (int i = 0; i < n; i++) report[i] = src[i];
            return true;
        }
    }
}
