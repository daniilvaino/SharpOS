namespace OS.Hal.Usb
{
    // USB mass storage over bulk endpoints: the transport half.
    //
    // Bulk-Only Transport is three phases — a 31-byte command block out, an
    // optional data phase, a 13-byte status block in. The SCSI commands that
    // ride inside it live in UsbMassStorage; this file only moves bytes.
    internal sealed unsafe partial class XhciController
    {
        private const byte CLASS_MSD = 8;
        private const byte SUBCLASS_SCSI = 6;
        private const byte PROTOCOL_BOT = 0x50;

        public bool IsMassStorage(uint slotId)
        {
            int i = IndexOfSlot(slotId);
            return i >= 0 && _devices[i].InterfaceClass == CLASS_MSD;
        }

        /// <summary>
        /// Claim a bulk-only mass storage interface and bring both of its
        /// endpoints up in a single Configure Endpoint command.
        /// </summary>
        public bool TryConfigureMsd(uint slotId, out uint failStage)
        {
            failStage = 0;
            int di = IndexOfSlot(slotId);
            if (di < 0) return false;

            ulong buf = DmaMemory.AllocPages(1);
            if (buf == 0) { failStage = 1; return false; }

            if (!TryControlIn(slotId, 0x80, 6, 0x0200, 0, (void*)buf, 9, out _))
            { failStage = 2; return false; }

            byte* p = (byte*)buf;
            ushort total = (ushort)(p[2] | (p[3] << 8));
            if (total < 9 || total > 4096) { failStage = 3; return false; }
            byte configValue = p[5];

            if (!TryControlIn(slotId, 0x80, 6, 0x0200, 0, (void*)buf, total, out _))
            { failStage = 4; return false; }

            if (!TryParseMsdInterface(p, total, out byte interfaceNum,
                                      out byte inAddr, out ushort inMax,
                                      out byte outAddr, out ushort outMax))
            { failStage = 5; return false; }

            ref Device d = ref _devices[di];
            d.InterfaceClass = CLASS_MSD;
            d.MsdInterface = interfaceNum;
            d.BulkIn.Address = inAddr;
            d.BulkIn.MaxPacket = inMax;
            d.BulkIn.Dci = (uint)((inAddr & 0x0F) * 2 + 1);
            d.BulkOut.Address = outAddr;
            d.BulkOut.MaxPacket = outMax;
            d.BulkOut.Dci = (uint)((outAddr & 0x0F) * 2);

            if (!TryControlOut(slotId, 0x00, 9, configValue, 0))
            { failStage = 6; return false; }

            if (!TryConfigureBulkPair(ref d, slotId)) { failStage = 7; return false; }

            d.Configured = true;
            return true;
        }

        private bool TryParseMsdInterface(byte* p, ushort total,
                                                 out byte interfaceNum,
                                                 out byte inAddr, out ushort inMax,
                                                 out byte outAddr, out ushort outMax)
        {
            interfaceNum = 0; inAddr = 0; inMax = 0; outAddr = 0; outMax = 0;

            bool inMsd = false;
            int offset = 0;
            while (offset + 2 <= total)
            {
                byte len = p[offset];
                byte type = p[offset + 1];
                if (len == 0) return false;

                if (type == DESC_INTERFACE && len >= 9)
                {
                    // Only SCSI-transparent over BOT: other command sets and
                    // transports exist and would need different code entirely,
                    // so refuse rather than half-drive them.
                    inMsd = p[offset + 5] == CLASS_MSD
                            && p[offset + 6] == SUBCLASS_SCSI
                            && p[offset + 7] == PROTOCOL_BOT;
                    if (inMsd) interfaceNum = p[offset + 2];
                }
                else if (type == DESC_ENDPOINT && len >= 7 && inMsd)
                {
                    byte address = p[offset + 2];
                    byte attributes = p[offset + 3];
                    if ((attributes & 0x3) == 0x2)          // bulk
                    {
                        ushort max = (ushort)(p[offset + 4] | (p[offset + 5] << 8));
                        if ((address & 0x80) != 0) { inAddr = address; inMax = max; }
                        else { outAddr = address; outMax = max; }
                    }
                }

                offset += len;
            }
            return inAddr != 0 && outAddr != 0;
        }

        private bool TryConfigureBulkPair(ref Device d, uint slotId)
        {
            d.BulkIn.Ring = DmaMemory.AllocPages(1);
            d.BulkOut.Ring = DmaMemory.AllocPages(1);
            if (d.BulkIn.Ring == 0 || d.BulkOut.Ring == 0) return false;
            d.BulkIn.Cycle = 1; d.BulkIn.Enqueue = 0;
            d.BulkOut.Cycle = 1; d.BulkOut.Enqueue = 0;

            uint cs = ContextSize;
            ulong input = d.InputContext;
            uint highest = d.BulkIn.Dci > d.BulkOut.Dci ? d.BulkIn.Dci : d.BulkOut.Dci;

            for (uint i = 0; i < cs * (highest + 2); i++) ((byte*)input)[i] = 0;

            uint* icc = (uint*)input;
            icc[0] = 0;
            icc[1] = 1u | (1u << (int)d.BulkIn.Dci) | (1u << (int)d.BulkOut.Dci);

            uint* slotCtx = (uint*)(input + cs);
            slotCtx[0] = (highest << 27) | (d.Speed << 20);
            slotCtx[1] = (d.Port + 1) << 16;

            // Endpoint type 6 = bulk IN, 2 = bulk OUT.
            WriteEpContext(input + cs * (d.BulkIn.Dci + 1), 6, d.BulkIn.MaxPacket, d.BulkIn.Ring);
            WriteEpContext(input + cs * (d.BulkOut.Dci + 1), 2, d.BulkOut.MaxPacket, d.BulkOut.Ring);

            uint* trb = (uint*)(_cmdRing + _cmdEnqueue * TrbSize);
            trb[0] = (uint)input;
            trb[1] = (uint)(input >> 32);
            trb[2] = 0;
            trb[3] = (TRB_CONFIGURE_ENDPOINT << 10) | (slotId << 24) | _cmdCycle;

            AdvanceCommandRing();
            Write32(_doorbellBase, 0);

            return TryWaitEvent(TRB_CMD_COMPLETE, 1000, out uint code, out _) && code == 1;
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

        /// <summary>One bulk transfer in either direction, start to finish.</summary>
        public bool TryBulkTransfer(uint slotId, bool directionIn,
                                           void* buffer, uint length, out uint residue)
        {
            residue = 0;
            int di = IndexOfSlot(slotId);
            if (di < 0) return false;

            ref Device d = ref _devices[di];
            ref BulkEp ep = ref (directionIn ? ref d.BulkIn : ref d.BulkOut);
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
            if (!TryWaitEvent(TRB_TRANSFER_EVENT, slotId, 5000, out uint code, out _))
                return false;

            // 13 is a short packet: the device sent less than asked, which for
            // a status block or a partial read is normal, not an error.
            return code == 1 || code == 13;
        }
    }
}
