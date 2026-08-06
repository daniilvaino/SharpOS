namespace OS.Hal.Usb
{
    // From an addressed device to actual keystrokes: read the configuration,
    // find the HID interface and its interrupt endpoint, configure that
    // endpoint on the controller, and poll it for reports.
    //
    // Boot protocol is used deliberately. A HID device's report layout is
    // otherwise described by a report descriptor that has to be parsed —
    // a small language of its own — whereas boot protocol fixes the layout:
    // 8 bytes for a keyboard, 3+ for a mouse. That is the whole reason the
    // BIOS can drive a USB keyboard without a HID parser, and it is enough
    // for arrow keys and DOOM.
    internal static unsafe partial class Xhci
    {
        private const uint TRB_CONFIGURE_ENDPOINT = 12;
        private const uint TRB_NORMAL = 1;

        // Descriptor types.
        private const byte DESC_CONFIGURATION = 2;
        private const byte DESC_INTERFACE = 4;
        private const byte DESC_ENDPOINT = 5;

        private const byte CLASS_HID = 3;

        public static byte HidProtocolOf(uint slotId)
        {
            int i = IndexOfSlot(slotId);
            return i < 0 ? (byte)0 : s_devices[i].HidProtocol;
        }

        public static bool IsConfigured(uint slotId)
        {
            int i = IndexOfSlot(slotId);
            return i >= 0 && s_devices[i].Configured;
        }

        /// <summary>
        /// Read the configuration, claim the HID interface and bring its
        /// interrupt endpoint up. False when the device is not a boot-protocol
        /// HID — which is not an error, just not something we drive.
        /// </summary>
        public static bool TryConfigureHid(uint slotId, out uint failStage)
        {
            failStage = 0;
            int di = IndexOfSlot(slotId);
            if (di < 0) return false;

            ulong buf = DmaMemory.AllocPages(1);
            if (buf == 0) { failStage = 1; return false; }

            // The first nine bytes carry the total length; the rest of the
            // tree only makes sense once the whole thing has been fetched.
            if (!TryControlIn(slotId, 0x80, 6, 0x0200, 0, (void*)buf, 9, out _))
            { failStage = 2; return false; }

            byte* p = (byte*)buf;
            ushort total = (ushort)(p[2] | (p[3] << 8));
            if (total < 9 || total > 4096) { failStage = 3; return false; }
            byte configValue = p[5];

            if (!TryControlIn(slotId, 0x80, 6, 0x0200, 0, (void*)buf, total, out _))
            { failStage = 4; return false; }

            if (!TryParseHidInterface(p, total, out byte interfaceNum, out byte protocol,
                                      out byte epAddress, out ushort epMaxPacket, out byte epInterval))
            { failStage = 5; return false; }

            ref Device d = ref s_devices[di];
            d.HidProtocol = protocol;
            d.EpAddress = epAddress;
            d.EpMaxPacket = epMaxPacket;
            d.EpInterval = epInterval;
            // Device context index: endpoint number doubled, plus one for IN.
            d.EpDci = (uint)((epAddress & 0x0F) * 2 + ((epAddress & 0x80) != 0 ? 1 : 0));

            if (!TryControlOut(slotId, 0x00, 9, configValue, 0))
            { failStage = 6; return false; }

            if (!TryConfigureEndpoint(ref d, slotId)) { failStage = 7; return false; }

            // Boot protocol, so reports arrive in the fixed layout above.
            // Some devices answer only after the interface is configured.
            TryControlOut(slotId, 0x21, 0x0B, 0, interfaceNum);

            d.ReportBuffer = DmaMemory.AllocPages(1);
            if (d.ReportBuffer == 0) { failStage = 8; return false; }

            d.Configured = true;
            return true;
        }

        // Walk the configuration descriptor as a flat list of
        // length/type-prefixed records, keeping the first HID interface that
        // has an interrupt IN endpoint.
        private static bool TryParseHidInterface(byte* p, ushort total,
                                                 out byte interfaceNum, out byte protocol,
                                                 out byte epAddress, out ushort epMaxPacket,
                                                 out byte epInterval)
        {
            interfaceNum = 0; protocol = 0; epAddress = 0; epMaxPacket = 0; epInterval = 0;

            bool inHid = false;
            int offset = 0;
            while (offset + 2 <= total)
            {
                byte len = p[offset];
                byte type = p[offset + 1];
                if (len == 0) return false;          // malformed: would loop forever

                if (type == DESC_INTERFACE && len >= 9)
                {
                    inHid = p[offset + 5] == CLASS_HID;
                    if (inHid)
                    {
                        interfaceNum = p[offset + 2];
                        protocol = p[offset + 7];
                    }
                }
                else if (type == DESC_ENDPOINT && len >= 7 && inHid)
                {
                    byte address = p[offset + 2];
                    byte attributes = p[offset + 3];
                    bool isInterruptIn = (attributes & 0x3) == 0x3 && (address & 0x80) != 0;
                    if (isInterruptIn)
                    {
                        epAddress = address;
                        epMaxPacket = (ushort)(p[offset + 4] | (p[offset + 5] << 8));
                        epInterval = p[offset + 6];
                        return true;
                    }
                }

                offset += len;
            }
            return false;
        }

        private static bool TryConfigureEndpoint(ref Device d, uint slotId)
        {
            d.EpRing = DmaMemory.AllocPages(1);
            if (d.EpRing == 0) return false;
            d.EpEnqueue = 0;
            d.EpCycle = 1;

            uint cs = ContextSize;
            ulong input = d.InputContext;

            // Reuse the input context, but clear it: leftovers from Address
            // Device would be re-applied as if we had asked for them.
            for (uint i = 0; i < cs * 3; i++) ((byte*)input)[i] = 0;

            uint* icc = (uint*)input;
            icc[0] = 0;
            icc[1] = 1u | (1u << (int)d.EpDci);      // slot context + this endpoint

            uint* slotCtx = (uint*)(input + cs);
            slotCtx[0] = (d.EpDci << 27) | (d.Speed << 20);
            slotCtx[1] = (d.Port + 1) << 16;

            uint* ep = (uint*)(input + cs * (d.EpDci + 1));
            ep[0] = (uint)d.EpInterval << 16;
            ep[1] = ((uint)d.EpMaxPacket << 16) | (7u << 3) | (3u << 1);  // interrupt IN
            ep[2] = (uint)(d.EpRing | 1UL);
            ep[3] = (uint)(d.EpRing >> 32);
            ep[4] = d.EpMaxPacket;

            uint* trb = (uint*)(s_cmdRing + s_cmdEnqueue * TrbSize);
            trb[0] = (uint)input;
            trb[1] = (uint)(input >> 32);
            trb[2] = 0;
            trb[3] = (TRB_CONFIGURE_ENDPOINT << 10) | (slotId << 24) | s_cmdCycle;

            AdvanceCommandRing();
            Write32(s_doorbellBase, 0);

            return TryWaitEvent(TRB_CMD_COMPLETE, 1000, out uint code, out _) && code == 1;
        }

        /// <summary>
        /// Queue one report read and wait for it. Returns the bytes the device
        /// sent into <paramref name="report"/>; false on timeout, which for an
        /// idle keyboard is the normal case.
        /// </summary>
        public static bool TryReadReport(uint slotId, byte* report, int max, uint timeoutMs)
        {
            if (!TryQueueReport(slotId)) return false;
            return TryCollectReport(slotId, report, max, timeoutMs);
        }

        /// <summary>
        /// Hand the endpoint one buffer to fill. Exactly one read may be
        /// outstanding per device: queueing again before the first completes
        /// stacks up requests that all fire at once on the next keypress.
        /// </summary>
        public static bool TryQueueReport(uint slotId)
        {
            int di = IndexOfSlot(slotId);
            if (di < 0) return false;

            ref Device d = ref s_devices[di];
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

            Write32(s_doorbellBase + slotId * 4, d.EpDci);
            d.ReadOutstanding = true;
            return true;
        }

        /// <summary>
        /// Collect a queued read. timeoutMs = 0 polls without blocking.
        ///
        /// Events from other slots are dropped here rather than routed: with
        /// one event ring shared by every device, a keyboard poll can pick up
        /// the mouse's completion. Fine while only the keyboard is driven,
        /// but a real multi-device path needs per-slot queues.
        /// </summary>
        public static bool TryCollectReport(uint slotId, byte* report, int max, uint timeoutMs)
        {
            int di = IndexOfSlot(slotId);
            if (di < 0) return false;

            ref Device d = ref s_devices[di];
            if (!d.ReadOutstanding) return false;

            if (!TryWaitEvent(TRB_TRANSFER_EVENT, timeoutMs, out uint code, out uint control))
                return false;

            d.ReadOutstanding = false;
            if (((control >> 24) & 0xFF) != slotId) return false;
            if (code != 1 && code != 13) return false;

            byte* src = (byte*)d.ReportBuffer;
            int n = d.EpMaxPacket < max ? d.EpMaxPacket : max;
            for (int i = 0; i < n; i++) report[i] = src[i];
            return true;
        }
    }
}
