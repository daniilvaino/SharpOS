namespace OS.Hal.Usb
{
    // HID as a per-interface thing, which is what it actually is.
    //
    // Until now a slot got one HID: the descriptor walk took the first it saw
    // and dropped the rest (`cls == CLASS_HID && !f.HasHid`). That was enough
    // for a keyboard and for the rig phone, and it is not enough for RS-Key,
    // which presents two — keyboard/OTP and FIDO — with byte-for-byte
    // identical interface descriptors. Neither has a boot protocol, so the
    // only thing that tells them apart is the report descriptor, which is why
    // fetching and reading it belongs here rather than in a diagnostic.
    //
    // Kept beside the device table rather than inside it: a device has one
    // disk and one serial port, and those stayed where they were, but the
    // number of HID interfaces is a property of the configuration and not of
    // the slot.
    internal sealed unsafe partial class XhciController
    {
        private const int MaxHidFunctions = 8;

        internal struct HidFunction
        {
            public bool InUse;
            public uint SlotId;
            public byte Interface;
            public byte Protocol;        // bInterfaceProtocol: 1 keyboard, 2 mouse, 0 none
            public byte Subclass;        // 1 = boot interface subclass
            public ushort ReportDescLength;

            // What the report descriptor says this interface is for. Zero
            // until it has been read, which happens after configuration.
            public ushort UsagePage;
            public ushort Usage;
            public bool UsageKnown;

            public byte EpAddress;
            public ushort EpMaxPacket;
            public byte EpInterval;
            public uint EpDci;
            public ulong EpRing;
            public uint EpEnqueue;
            public uint EpCycle;
            public ulong ReportBuffer;
            public bool ReadOutstanding;
            public bool ReportPending;

            // The other direction, when the interface declares one. A keyboard
            // does not; CTAPHID does, because it is a conversation — the host
            // sends a command frame and the key answers on the IN endpoint.
            public byte EpOutAddress;
            public ushort EpOutMaxPacket;
            public byte EpOutInterval;
            public uint EpOutDci;
            public ulong EpOutRing;
            public uint EpOutEnqueue;
            public uint EpOutCycle;
            public ulong OutBuffer;
        }

        private readonly HidFunction[] _hid = new HidFunction[MaxHidFunctions];

        /// <summary>How many HID interfaces this slot declared and we claimed.</summary>
        public int HidCountOf(uint slotId)
        {
            int n = 0;
            for (int i = 0; i < MaxHidFunctions; i++)
                if (_hid[i].InUse && _hid[i].SlotId == slotId) n++;
            return n;
        }

        /// <summary>
        /// Index into the whole table of the n-th HID function of a slot, or
        /// -1. Callers hold this index, not the ordinal, because the table is
        /// shared between slots.
        /// </summary>
        public int HidIndexOf(uint slotId, int ordinal)
        {
            for (int i = 0; i < MaxHidFunctions; i++)
            {
                if (!_hid[i].InUse || _hid[i].SlotId != slotId) continue;
                if (ordinal-- == 0) return i;
            }
            return -1;
        }

        public byte HidInterfaceAt(int index)
            => (uint)index < MaxHidFunctions && _hid[index].InUse ? _hid[index].Interface : (byte)0;

        public byte HidProtocolAt(int index)
            => (uint)index < MaxHidFunctions && _hid[index].InUse ? _hid[index].Protocol : (byte)0;

        public ushort HidUsagePageAt(int index)
            => (uint)index < MaxHidFunctions && _hid[index].InUse ? _hid[index].UsagePage : (ushort)0;

        public ushort HidUsageAt(int index)
            => (uint)index < MaxHidFunctions && _hid[index].InUse ? _hid[index].Usage : (ushort)0;

        public ushort HidReportDescLengthAt(int index)
            => (uint)index < MaxHidFunctions && _hid[index].InUse ? _hid[index].ReportDescLength : (ushort)0;

        /// <summary>Packet size of the interface's IN endpoint — the frame size.</summary>
        public ushort HidMaxPacketAt(int index)
            => (uint)index < MaxHidFunctions && _hid[index].InUse ? _hid[index].EpMaxPacket : (ushort)0;

        /// <summary>Can the host send to this interface, or only listen?</summary>
        public bool HidHasOutAt(int index)
            => (uint)index < MaxHidFunctions && _hid[index].InUse && _hid[index].EpOutRing != 0;

        /// <summary>
        /// Find a HID interface by what its report descriptor says it is.
        /// Returns the table index, or -1.
        /// </summary>
        /// <remarks>
        /// The question every caller past a plain keyboard actually has.
        /// bInterfaceProtocol cannot answer it: it is 0 for anything without a
        /// boot protocol, which includes both of RS-Key's interfaces and the
        /// rig phone's.
        /// </remarks>
        public int FindHidByUsage(ushort usagePage, ushort usage)
        {
            for (int i = 0; i < MaxHidFunctions; i++)
            {
                if (!_hid[i].InUse || !_hid[i].UsageKnown) continue;
                if (_hid[i].UsagePage == usagePage && _hid[i].Usage == usage) return i;
            }
            return -1;
        }

        private int AllocateHidFunction(uint slotId, byte interfaceNumber,
                                        byte subclass, byte protocol)
        {
            for (int i = 0; i < MaxHidFunctions; i++)
            {
                if (_hid[i].InUse) continue;
                _hid[i] = default;
                _hid[i].InUse = true;
                _hid[i].SlotId = slotId;
                _hid[i].Interface = interfaceNumber;
                _hid[i].Subclass = subclass;
                _hid[i].Protocol = protocol;
                return i;
            }
            return -1;
        }

        private void ReleaseHidFunctions(uint slotId)
        {
            for (int i = 0; i < MaxHidFunctions; i++)
                if (_hid[i].InUse && _hid[i].SlotId == slotId) _hid[i] = default;
        }

        /// <summary>
        /// Read each HID interface's report descriptor and remember what it
        /// says the interface is for.
        /// </summary>
        /// <remarks>
        /// After configuration, necessarily: a report descriptor is fetched
        /// from an INTERFACE, and an unconfigured device has none, so asking
        /// early gets a refusal that halts the control endpoint. That cost the
        /// rig its boot disk once already.
        /// </remarks>
        private void ReadHidUsages(uint slotId)
        {
            byte* descriptor = stackalloc byte[512];

            for (int i = 0; i < MaxHidFunctions; i++)
            {
                if (!_hid[i].InUse || _hid[i].SlotId != slotId) continue;
                if (_hid[i].ReportDescLength == 0) continue;

                if (!TryFetchReportDescriptor(slotId, _hid[i].Interface,
                                              _hid[i].ReportDescLength,
                                              descriptor, 512, out int length))
                    continue;

                if (TryFindTopLevelUsage(descriptor, length,
                                         out ushort page, out ushort usage))
                {
                    _hid[i].UsagePage = page;
                    _hid[i].Usage = usage;
                    _hid[i].UsageKnown = true;
                }
            }
        }

        /// <summary>
        /// The first Usage Page and Usage of a report descriptor.
        /// </summary>
        /// <remarks>
        /// Items are length-prefixed, so this steps over them properly rather
        /// than matching the two byte patterns that happen to be common — a
        /// descriptor opening any other way would otherwise be read as a
        /// keyboard. Enough to tell a keyboard (page 0x01, usage 0x06) from a
        /// FIDO transport (page 0xF1D0); a driver that needs field offsets
        /// will need a real parser, and this is not pretending to be one.
        /// </remarks>
        private static bool TryFindTopLevelUsage(byte* r, int length,
                                                 out ushort page, out ushort usage)
        {
            page = 0;
            usage = 0;
            bool havePage = false;
            bool haveUsage = false;

            int offset = 0;
            while (offset < length)
            {
                byte prefix = r[offset];
                if (prefix == 0xFE) break;          // long item

                int size = prefix & 0x03;
                if (size == 3) size = 4;
                int type = (prefix >> 2) & 0x03;    // 0 main, 1 global, 2 local
                int tag = (prefix >> 4) & 0x0F;

                if (offset + 1 + size > length) break;

                uint value = 0;
                for (int i = 0; i < size; i++) value |= (uint)r[offset + 1 + i] << (8 * i);

                if (type == 1 && tag == 0 && !havePage) { page = (ushort)value; havePage = true; }
                else if (type == 2 && tag == 0 && !haveUsage) { usage = (ushort)value; haveUsage = true; }

                if (havePage && haveUsage) return true;
                offset += 1 + size;
            }

            return havePage || haveUsage;
        }
    }
}
