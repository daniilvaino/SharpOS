namespace OS.Hal.Usb
{
    // Reading reports, from one HID interface at a time.
    //
    // Boot protocol is still what the keyboard path relies on: it fixes the
    // report layout at 8 bytes, which is why a BIOS can drive a USB keyboard
    // without a HID parser, and it is enough for arrow keys and DOOM. What has
    // changed is that a slot no longer has "a" HID. RS-Key presents two
    // interfaces with byte-identical descriptors and no boot protocol at all,
    // so reading is addressed to a function — found by usage, see
    // FindHidByUsage — and the slot-shaped calls below are a thin shim over
    // the first one for the callers that have not needed to care yet.
    internal sealed unsafe partial class XhciController
    {
        private const uint TRB_CONFIGURE_ENDPOINT = 12;
        private const uint TRB_NORMAL = 1;

        /// <summary>
        /// Queue one report read and wait for it. Returns the bytes the device
        /// sent into <paramref name="report"/>; false on timeout, which for an
        /// idle keyboard is the normal case.
        /// </summary>
        public bool TryReadReportOn(int function, byte* report, int max, uint timeoutMs)
        {
            if (!TryQueueReportOn(function)) return false;
            return TryCollectReportOn(function, report, max, timeoutMs);
        }

        /// <summary>
        /// Hand this interface's endpoint one buffer to fill. Exactly one read
        /// may be outstanding per function: queueing again before the first
        /// completes stacks up requests that all fire at once on the next
        /// keypress.
        /// </summary>
        public bool TryQueueReportOn(int function)
        {
            if ((uint)function >= MaxHidFunctions || !_hid[function].InUse) return false;
            if (_hid[function].ReadOutstanding) return false;
            if (_hid[function].EpRing == 0) return false;

            uint slotId = _hid[function].SlotId;

            uint* trb = (uint*)(_hid[function].EpRing + _hid[function].EpEnqueue * TrbSize);
            trb[0] = (uint)_hid[function].ReportBuffer;
            trb[1] = (uint)(_hid[function].ReportBuffer >> 32);
            trb[2] = _hid[function].EpMaxPacket;
            trb[3] = (TRB_NORMAL << 10) | TRB_IOC | _hid[function].EpCycle;

            _hid[function].EpEnqueue++;
            if (_hid[function].EpEnqueue >= RingTrbs - 1)
            {
                uint* link = (uint*)(_hid[function].EpRing + (RingTrbs - 1) * TrbSize);
                link[0] = (uint)_hid[function].EpRing;
                link[1] = (uint)(_hid[function].EpRing >> 32);
                link[2] = 0;
                link[3] = (TRB_LINK << 10) | TRB_TOGGLE_CYCLE | _hid[function].EpCycle;
                _hid[function].EpEnqueue = 0;
                _hid[function].EpCycle ^= 1;
            }

            Write32(_doorbellBase + slotId * 4, _hid[function].EpDci);
            _hid[function].ReadOutstanding = true;
            return true;
        }

        /// <summary>
        /// Collect a queued read. timeoutMs = 0 polls without blocking.
        /// </summary>
        public bool TryCollectReportOn(int function, byte* report, int max, uint timeoutMs)
        {
            if ((uint)function >= MaxHidFunctions || !_hid[function].InUse) return false;

            uint slotId = _hid[function].SlotId;

            // A report someone else's wait absorbed is already sitting in our
            // buffer — take it rather than waiting for another one.
            if (_hid[function].ReportPending)
            {
                _hid[function].ReportPending = false;
            }
            else
            {
                if (!_hid[function].ReadOutstanding) return false;
                if (!TryWaitEvent(TRB_TRANSFER_EVENT, slotId, timeoutMs, out uint code, out _))
                    return false;
                _hid[function].ReadOutstanding = false;
                if (code != 1 && code != 13) return false;
            }

            byte* src = (byte*)_hid[function].ReportBuffer;
            int n = _hid[function].EpMaxPacket < max ? _hid[function].EpMaxPacket : max;
            for (int i = 0; i < n; i++) report[i] = src[i];
            return true;
        }

        /// <summary>
        /// Send one report to this interface's interrupt OUT endpoint.
        /// </summary>
        /// <remarks>
        /// The direction a keyboard never needed. CTAPHID is a conversation —
        /// the host writes a command frame and the key answers on the IN
        /// endpoint — so a transport that can only listen cannot start one.
        /// </remarks>
        public bool TryWriteReportOn(int function, byte* data, int length, uint timeoutMs)
        {
            if ((uint)function >= MaxHidFunctions || !_hid[function].InUse) return false;
            if (_hid[function].EpOutRing == 0 || _hid[function].OutBuffer == 0) return false;
            if (length <= 0 || length > _hid[function].EpOutMaxPacket) return false;

            uint slotId = _hid[function].SlotId;

            byte* dst = (byte*)_hid[function].OutBuffer;
            for (int i = 0; i < length; i++) dst[i] = data[i];

            uint* trb = (uint*)(_hid[function].EpOutRing + _hid[function].EpOutEnqueue * TrbSize);
            trb[0] = (uint)_hid[function].OutBuffer;
            trb[1] = (uint)(_hid[function].OutBuffer >> 32);
            trb[2] = (uint)length;
            trb[3] = (TRB_NORMAL << 10) | TRB_IOC | _hid[function].EpOutCycle;

            _hid[function].EpOutEnqueue++;
            if (_hid[function].EpOutEnqueue >= RingTrbs - 1)
            {
                uint* link = (uint*)(_hid[function].EpOutRing + (RingTrbs - 1) * TrbSize);
                link[0] = (uint)_hid[function].EpOutRing;
                link[1] = (uint)(_hid[function].EpOutRing >> 32);
                link[2] = 0;
                link[3] = (TRB_LINK << 10) | TRB_TOGGLE_CYCLE | _hid[function].EpOutCycle;
                _hid[function].EpOutEnqueue = 0;
                _hid[function].EpOutCycle ^= 1;
            }

            Write32(_doorbellBase + slotId * 4, _hid[function].EpOutDci);

            if (!TryWaitEvent(TRB_TRANSFER_EVENT, slotId, timeoutMs, out uint code, out _))
            {
                _lastCode = 0;
                return false;
            }
            _lastCode = code;
            return code == 1 || code == 13;
        }

        // The slot-shaped calls, for callers that want whatever HID the device
        // has and have only ever met devices with one.
        public bool TryReadReport(uint slotId, byte* report, int max, uint timeoutMs)
            => TryReadReportOn(HidIndexOf(slotId, 0), report, max, timeoutMs);

        public bool TryQueueReport(uint slotId)
            => TryQueueReportOn(HidIndexOf(slotId, 0));

        public bool TryCollectReport(uint slotId, byte* report, int max, uint timeoutMs)
            => TryCollectReportOn(HidIndexOf(slotId, 0), report, max, timeoutMs);
    }
}
