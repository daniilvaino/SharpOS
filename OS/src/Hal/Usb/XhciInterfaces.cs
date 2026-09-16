namespace OS.Hal.Usb
{
    // One pass over the configuration descriptor, every function it declares.
    //
    // A slot used to get one driver: the enumerator tried HID, and if that
    // failed, storage. A composite device broke that — the test rig phone
    // presents a stick, a keyboard and a serial port on one connector, and
    // whichever function was probed first was the only one that worked.
    //
    // So the descriptor is walked once, all endpoints are collected, and all
    // of them go up in a single Configure Endpoint command. The controller
    // wants that anyway: each command re-applies the input context, so two
    // commands in a row would drop the endpoints the first one added.
    internal sealed unsafe partial class XhciController
    {
        // Descriptor types.
        private const byte DESC_INTERFACE = 4;
        private const byte DESC_ENDPOINT = 5;

        private const byte CLASS_HID = 3;
        private const byte CLASS_CDC_COMM = 0x02;
        private const byte SUBCLASS_ACM = 0x02;
        private const byte CLASS_CDC_DATA = 0x0A;

        // Endpoint context types, xHCI 6.2.3.
        private const uint EPTYPE_BULK_OUT = 2;
        private const uint EPTYPE_BULK_IN = 6;
        private const uint EPTYPE_INTERRUPT_IN = 7;

        private struct Functions
        {
            public bool HasHid;
            public byte HidInterface;
            public byte HidProtocol;
            public byte HidEpAddress;
            public ushort HidEpMaxPacket;
            public byte HidEpInterval;

            public bool HasMsd;
            public byte MsdInterface;
            public byte MsdInAddress;
            public ushort MsdInMaxPacket;
            public byte MsdOutAddress;
            public ushort MsdOutMaxPacket;

            public bool HasCdc;
            public byte CdcCommInterface;
            public byte CdcDataInterface;
            public byte CdcInAddress;
            public ushort CdcInMaxPacket;
            public byte CdcOutAddress;
            public ushort CdcOutMaxPacket;
        }

        public bool IsMassStorage(uint slotId)
        {
            int i = IndexOfSlot(slotId);
            return i >= 0 && _devices[i].HasMsd;
        }

        public bool IsCdcAcm(uint slotId)
        {
            int i = IndexOfSlot(slotId);
            return i >= 0 && _devices[i].HasCdc;
        }

        public byte HidProtocolOf(uint slotId)
        {
            int i = IndexOfSlot(slotId);
            return i < 0 || !_devices[i].HasHid ? (byte)0 : _devices[i].HidProtocol;
        }

        public bool IsConfigured(uint slotId)
        {
            int i = IndexOfSlot(slotId);
            return i >= 0 && _devices[i].Configured;
        }

        /// <summary>
        /// Fetch the whole configuration descriptor into a caller's buffer.
        /// For diagnosis: a device we decline is otherwise indistinguishable
        /// from one whose descriptor we misread.
        /// </summary>
        public bool TryFetchConfigDescriptor(uint slotId, byte* buffer, int max, out int length)
        {
            length = 0;
            if (IndexOfSlot(slotId) < 0) return false;

            ulong buf = DmaMemory.AllocPages(1);
            if (buf == 0) return false;

            if (!TryControlIn(slotId, 0x80, 6, 0x0200, 0, (void*)buf, 9, out _)) return false;

            byte* p = (byte*)buf;
            ushort total = (ushort)(p[2] | (p[3] << 8));
            if (total < 9 || total > 4096) return false;

            if (!TryControlIn(slotId, 0x80, 6, 0x0200, 0, (void*)buf, total, out _)) return false;

            int n = total < max ? total : max;
            for (int i = 0; i < n; i++) buffer[i] = p[i];
            length = n;
            return true;
        }

        /// <summary>
        /// Read the configuration, claim every function we drive, and bring
        /// all of their endpoints up at once. False when the device declares
        /// nothing we understand — not an error, just not ours.
        /// </summary>
        public bool TryConfigureFunctions(uint slotId, out uint failStage)
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

            Functions f = ParseFunctions(p, total);
            if (!f.HasHid && !f.HasMsd && !f.HasCdc) { failStage = 5; return false; }

            ref Device d = ref _devices[di];

            d.HasHid = f.HasHid;
            d.HidInterface = f.HidInterface;
            d.HidProtocol = f.HidProtocol;
            d.EpAddress = f.HidEpAddress;
            d.EpMaxPacket = f.HidEpMaxPacket;
            d.EpInterval = f.HidEpInterval;
            d.EpDci = DciOf(f.HidEpAddress);

            d.HasMsd = f.HasMsd;
            d.MsdInterface = f.MsdInterface;
            d.BulkIn.Address = f.MsdInAddress;
            d.BulkIn.MaxPacket = f.MsdInMaxPacket;
            d.BulkIn.Dci = DciOf(f.MsdInAddress);
            d.BulkOut.Address = f.MsdOutAddress;
            d.BulkOut.MaxPacket = f.MsdOutMaxPacket;
            d.BulkOut.Dci = DciOf(f.MsdOutAddress);

            d.HasCdc = f.HasCdc;
            d.CdcCommInterface = f.CdcCommInterface;
            d.CdcDataInterface = f.CdcDataInterface;
            d.CdcIn.Address = f.CdcInAddress;
            d.CdcIn.MaxPacket = f.CdcInMaxPacket;
            d.CdcIn.Dci = DciOf(f.CdcInAddress);
            d.CdcOut.Address = f.CdcOutAddress;
            d.CdcOut.MaxPacket = f.CdcOutMaxPacket;
            d.CdcOut.Dci = DciOf(f.CdcOutAddress);

            if (!TryControlOut(slotId, 0x00, 9, configValue, 0))
            { failStage = 6; return false; }

            if (!TryConfigureEndpoints(ref d, slotId)) { failStage = 7; return false; }

            if (d.HasHid)
            {
                // Boot protocol, so reports arrive in the fixed 8-byte layout.
                // Some devices answer only after the interface is configured.
                TryControlOut(slotId, 0x21, 0x0B, 0, d.HidInterface);

                d.ReportBuffer = DmaMemory.AllocPages(1);
                if (d.ReportBuffer == 0) { failStage = 8; return false; }
            }

            if (d.HasCdc)
            {
                // SET_CONTROL_LINE_STATE with DTR and RTS asserted. A real
                // modem would raise the lines; a Linux gadget uses it to tell
                // userspace the port has a reader, so without it the other end
                // may sit waiting for us.
                TryControlOut(slotId, 0x21, 0x22, 0x0003, d.CdcCommInterface);
            }

            d.Configured = true;
            return true;
        }

        // Device context index: endpoint number doubled, plus one for IN.
        private static uint DciOf(byte address)
            => address == 0 ? 0u
             : (uint)((address & 0x0F) * 2 + ((address & 0x80) != 0 ? 1 : 0));

        // The configuration descriptor is a flat list of length/type-prefixed
        // records: each interface owns the endpoints that follow it until the
        // next interface. Alternate settings other than 0 are skipped — they
        // would need SET_INTERFACE, which nothing here issues.
        private Functions ParseFunctions(byte* p, ushort total)
        {
            Functions f = default;

            int current = 0;            // 1 HID, 2 storage, 3 CDC data
            int offset = 0;
            while (offset + 2 <= total)
            {
                byte len = p[offset];
                byte type = p[offset + 1];
                if (len == 0) break;               // malformed: would loop forever

                if (type == DESC_INTERFACE && len >= 9)
                {
                    byte number = p[offset + 2];
                    byte alternate = p[offset + 3];
                    byte cls = p[offset + 5];
                    byte subclass = p[offset + 6];
                    byte protocol = p[offset + 7];

                    current = 0;
                    if (alternate == 0)
                    {
                        if (cls == CLASS_HID && !f.HasHid)
                        {
                            current = 1;
                            f.HasHid = true;
                            f.HidInterface = number;
                            f.HidProtocol = protocol;
                        }
                        else if (cls == CLASS_MSD && subclass == SUBCLASS_SCSI
                                 && protocol == PROTOCOL_BOT && !f.HasMsd)
                        {
                            // Only SCSI-transparent over BOT: other command
                            // sets and transports would need different code.
                            current = 2;
                            f.HasMsd = true;
                            f.MsdInterface = number;
                        }
                        else if (cls == CLASS_CDC_COMM && subclass == SUBCLASS_ACM)
                        {
                            f.CdcCommInterface = number;
                        }
                        else if (cls == CLASS_CDC_DATA && f.CdcInAddress == 0)
                        {
                            current = 3;
                            f.CdcDataInterface = number;
                        }
                    }
                }
                else if (type == DESC_ENDPOINT && len >= 7 && current != 0)
                {
                    byte address = p[offset + 2];
                    byte attributes = p[offset + 3];
                    ushort max = (ushort)(p[offset + 4] | (p[offset + 5] << 8));
                    bool bulk = (attributes & 0x3) == 0x2;
                    bool interrupt = (attributes & 0x3) == 0x3;
                    bool directionIn = (address & 0x80) != 0;

                    if (current == 1 && interrupt && directionIn && f.HidEpAddress == 0)
                    {
                        f.HidEpAddress = address;
                        f.HidEpMaxPacket = max;
                        f.HidEpInterval = p[offset + 6];
                    }
                    else if (current == 2 && bulk)
                    {
                        if (directionIn) { f.MsdInAddress = address; f.MsdInMaxPacket = max; }
                        else { f.MsdOutAddress = address; f.MsdOutMaxPacket = max; }
                    }
                    else if (current == 3 && bulk)
                    {
                        if (directionIn) { f.CdcInAddress = address; f.CdcInMaxPacket = max; }
                        else { f.CdcOutAddress = address; f.CdcOutMaxPacket = max; }
                    }
                }

                offset += len;
            }

            // A function without its endpoints is not a function.
            f.HasHid = f.HasHid && f.HidEpAddress != 0;
            f.HasMsd = f.HasMsd && f.MsdInAddress != 0 && f.MsdOutAddress != 0;
            f.HasCdc = f.CdcInAddress != 0 && f.CdcOutAddress != 0;
            return f;
        }

        private bool TryConfigureEndpoints(ref Device d, uint slotId)
        {
            uint addFlags = 1;            // slot context always
            uint highest = 0;

            if (d.HasHid)
            {
                d.EpRing = DmaMemory.AllocPages(1);
                if (d.EpRing == 0) return false;
                d.EpEnqueue = 0;
                d.EpCycle = 1;
                addFlags |= 1u << (int)d.EpDci;
                if (d.EpDci > highest) highest = d.EpDci;
            }
            if (d.HasMsd)
            {
                if (!TryOpenBulk(ref d.BulkIn) || !TryOpenBulk(ref d.BulkOut)) return false;
                addFlags |= (1u << (int)d.BulkIn.Dci) | (1u << (int)d.BulkOut.Dci);
                if (d.BulkIn.Dci > highest) highest = d.BulkIn.Dci;
                if (d.BulkOut.Dci > highest) highest = d.BulkOut.Dci;
            }
            if (d.HasCdc)
            {
                if (!TryOpenBulk(ref d.CdcIn) || !TryOpenBulk(ref d.CdcOut)) return false;
                addFlags |= (1u << (int)d.CdcIn.Dci) | (1u << (int)d.CdcOut.Dci);
                if (d.CdcIn.Dci > highest) highest = d.CdcIn.Dci;
                if (d.CdcOut.Dci > highest) highest = d.CdcOut.Dci;
            }
            if (highest == 0) return false;

            uint cs = ContextSize;
            ulong input = d.InputContext;

            // Clear the whole span the command will read: leftovers from
            // Address Device would be re-applied as if we had asked for them.
            for (uint i = 0; i < cs * (highest + 2); i++) ((byte*)input)[i] = 0;

            uint* icc = (uint*)input;
            icc[0] = 0;                   // drop flags
            icc[1] = addFlags;

            uint* slotCtx = (uint*)(input + cs);
            slotCtx[0] = (highest << 27) | (d.Speed << 20);
            slotCtx[1] = (d.Port + 1) << 16;

            if (d.HasHid)
            {
                uint* ep = (uint*)(input + cs * (d.EpDci + 1));
                ep[0] = (uint)d.EpInterval << 16;
                ep[1] = ((uint)d.EpMaxPacket << 16) | (EPTYPE_INTERRUPT_IN << 3) | (3u << 1);
                ep[2] = (uint)(d.EpRing | 1UL);
                ep[3] = (uint)(d.EpRing >> 32);
                ep[4] = d.EpMaxPacket;
            }
            if (d.HasMsd)
            {
                WriteEpContext(input + cs * (d.BulkIn.Dci + 1), EPTYPE_BULK_IN,
                               d.BulkIn.MaxPacket, d.BulkIn.Ring);
                WriteEpContext(input + cs * (d.BulkOut.Dci + 1), EPTYPE_BULK_OUT,
                               d.BulkOut.MaxPacket, d.BulkOut.Ring);
            }
            if (d.HasCdc)
            {
                WriteEpContext(input + cs * (d.CdcIn.Dci + 1), EPTYPE_BULK_IN,
                               d.CdcIn.MaxPacket, d.CdcIn.Ring);
                WriteEpContext(input + cs * (d.CdcOut.Dci + 1), EPTYPE_BULK_OUT,
                               d.CdcOut.MaxPacket, d.CdcOut.Ring);
            }

            uint* trb = (uint*)(_cmdRing + _cmdEnqueue * TrbSize);
            trb[0] = (uint)input;
            trb[1] = (uint)(input >> 32);
            trb[2] = 0;
            trb[3] = (TRB_CONFIGURE_ENDPOINT << 10) | (slotId << 24) | _cmdCycle;

            AdvanceCommandRing();
            Write32(_doorbellBase, 0);

            return TryWaitEvent(TRB_CMD_COMPLETE, 1000, out uint code, out _) && code == 1;
        }

        private bool TryOpenBulk(ref BulkEp ep)
        {
            ep.Ring = DmaMemory.AllocPages(1);
            if (ep.Ring == 0) return false;
            ep.Enqueue = 0;
            ep.Cycle = 1;
            return true;
        }

        private void WriteEpContext(ulong ctx, uint epType, ushort maxPacket, ulong ring)
        {
            uint* ep = (uint*)ctx;
            ep[0] = 0;
            ep[1] = ((uint)maxPacket << 16) | (epType << 3) | (3u << 1);
            ep[2] = (uint)(ring | 1UL);
            ep[3] = (uint)(ring >> 32);
            ep[4] = maxPacket;
        }
    }
}
