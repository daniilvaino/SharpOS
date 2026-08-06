namespace OS.Hal.Usb
{
    // Addressing a device and talking to its default control endpoint.
    //
    // This is where the controller stops being a black box and the device
    // starts answering: Address Device hands the slot its contexts, after
    // which a standard GET_DESCRIPTOR over endpoint 0 says what the thing
    // actually is. A wrong context layout shows up immediately as a non-success
    // completion code rather than as silence, which is why this is the slice
    // that validates the structure work.
    internal static unsafe partial class Xhci
    {
        private const uint TRB_ADDRESS_DEVICE = 11;
        private const uint TRB_SETUP = 2;
        private const uint TRB_DATA = 3;
        private const uint TRB_STATUS = 4;
        private const uint TRB_TRANSFER_EVENT = 32;

        private const uint TRB_IOC = 1u << 5;    // interrupt on completion
        private const uint TRB_IDT = 1u << 6;    // immediate data (setup stage)

        private const int MaxDevices = 8;

        private struct Device
        {
            public uint SlotId;
            public uint Port;
            public uint Speed;
            public ulong InputContext;
            public ulong DeviceContext;
            public ulong TransferRing;
            public uint TrEnqueue;
            public uint TrCycle;
            public bool Addressed;

            // Filled in once the interface is understood (see XhciHid).
            public byte HidProtocol;      // 1 keyboard, 2 mouse, 0 neither
            public byte EpAddress;
            public ushort EpMaxPacket;
            public byte EpInterval;
            public uint EpDci;
            public ulong EpRing;
            public uint EpEnqueue;
            public uint EpCycle;
            public ulong ReportBuffer;
            public bool Configured;
            public bool ReadOutstanding;

            // Mass storage: two bulk endpoints instead of one interrupt one.
            public byte InterfaceClass;
            public byte MsdInterface;
            public BulkEp BulkIn;
            public BulkEp BulkOut;
        }

        internal struct BulkEp
        {
            public uint Dci;
            public ulong Ring;
            public uint Enqueue;
            public uint Cycle;
            public ushort MaxPacket;
            public byte Address;
        }

        private static readonly Device[] s_devices = new Device[MaxDevices];
        private static int s_deviceCount;

        public static int DeviceCount => s_deviceCount;

        public static uint SlotIdAt(int index)
            => index >= 0 && index < s_deviceCount ? s_devices[index].SlotId : 0;

        private static uint ContextSize => s_contextSize64 ? 64u : 32u;

        /// <summary>
        /// Give a slot its contexts and let the controller assign it a USB
        /// address. Must follow a successful port reset and Enable Slot.
        /// </summary>
        public static bool TryAddressDevice(uint slotId, uint port, out uint completionCode)
        {
            completionCode = 0;
            if (!s_running || s_deviceCount >= MaxDevices) return false;

            uint speed = (PortStatus(port) >> 10) & 0xF;

            ulong input = DmaMemory.AllocPages(1);
            ulong output = DmaMemory.AllocPages(1);
            ulong ring = DmaMemory.AllocPages(1);
            if (input == 0 || output == 0 || ring == 0) return false;

            uint cs = ContextSize;

            // Input control context: A0 marks the slot context as present, A1
            // the default endpoint. The controller ignores anything not flagged.
            uint* icc = (uint*)input;
            icc[0] = 0;                    // drop flags
            icc[1] = 0x3;                  // add flags: slot + EP0

            // Slot context. One context entry (EP0 only), the port it lives on,
            // and the speed the port reported.
            uint* slotCtx = (uint*)(input + cs);
            slotCtx[0] = (1u << 27) | (speed << 20);
            slotCtx[1] = (port + 1) << 16;   // ports are 1-based here

            // Default control endpoint. Max packet size is speed-dependent and
            // getting it wrong makes the first transfer fail rather than the
            // command, so it is worth being explicit.
            uint* ep0 = (uint*)(input + cs * 2);
            ep0[1] = (MaxPacketForSpeed(speed) << 16) | (4u << 3) | (3u << 1);
            ep0[2] = (uint)(ring | 1UL);     // DCS = 1
            ep0[3] = (uint)(ring >> 32);
            ep0[4] = 8;                      // average TRB length

            // The controller finds a slot's output context through this array.
            ((ulong*)s_dcbaa)[slotId] = output;

            uint* trb = (uint*)(s_cmdRing + s_cmdEnqueue * TrbSize);
            trb[0] = (uint)input;
            trb[1] = (uint)(input >> 32);
            trb[2] = 0;
            trb[3] = (TRB_ADDRESS_DEVICE << 10) | (slotId << 24) | s_cmdCycle;

            AdvanceCommandRing();
            Write32(s_doorbellBase, 0);

            if (!TryWaitEvent(TRB_CMD_COMPLETE, 1000, out completionCode, out _))
                return false;
            if (completionCode != 1)
                return false;

            ref Device d = ref s_devices[s_deviceCount++];
            d.SlotId = slotId;
            d.Port = port;
            d.Speed = speed;
            d.InputContext = input;
            d.DeviceContext = output;
            d.TransferRing = ring;
            d.TrEnqueue = 0;
            d.TrCycle = 1;
            d.Addressed = true;
            return true;
        }

        // Speed codes: 1 full, 2 low, 3 high, 4 super. Full speed actually
        // varies (8/16/32/64) and is meant to be re-read from the descriptor;
        // 8 is the safe opening bid every device must accept.
        private static uint MaxPacketForSpeed(uint speed)
        {
            switch (speed)
            {
                case 4: return 512;
                case 3: return 64;
                case 2: return 8;
                default: return 8;
            }
        }

        /// <summary>
        /// Standard control IN request on endpoint 0 (setup / data / status).
        /// Returns bytes the device actually sent.
        /// </summary>
        public static bool TryControlIn(uint slotId, byte requestType, byte request,
                                        ushort value, ushort index,
                                        void* buffer, ushort length, out uint transferred)
        {
            transferred = 0;
            int di = IndexOfSlot(slotId);
            if (di < 0) return false;

            ref Device d = ref s_devices[di];
            ulong dataPhys = (ulong)buffer;

            // Setup stage. IDT means the eight setup bytes live in the TRB
            // itself rather than being fetched from memory.
            uint* setup = (uint*)(d.TransferRing + d.TrEnqueue * TrbSize);
            setup[0] = (uint)(requestType | (request << 8) | (value << 16));
            setup[1] = (uint)(index | (length << 16));
            setup[2] = 8;
            setup[3] = (TRB_SETUP << 10) | (3u << 16) | TRB_IDT | d.TrCycle;
            AdvanceTransferRing(ref d);

            // Data stage, device to host.
            uint* data = (uint*)(d.TransferRing + d.TrEnqueue * TrbSize);
            data[0] = (uint)dataPhys;
            data[1] = (uint)(dataPhys >> 32);
            data[2] = length;
            data[3] = (TRB_DATA << 10) | (1u << 16) | d.TrCycle;
            AdvanceTransferRing(ref d);

            // Status stage runs opposite to the data direction, and carries the
            // interrupt so exactly one event ends the whole transfer.
            uint* status = (uint*)(d.TransferRing + d.TrEnqueue * TrbSize);
            status[0] = 0;
            status[1] = 0;
            status[2] = 0;
            status[3] = (TRB_STATUS << 10) | TRB_IOC | d.TrCycle;
            AdvanceTransferRing(ref d);

            // Doorbell for this slot, target 1 = the default endpoint.
            Write32(s_doorbellBase + slotId * 4, 1);

            if (!TryWaitEvent(TRB_TRANSFER_EVENT, 1000, out uint code, out _))
                return false;
            if (code != 1 && code != 13)     // 13 = short packet, still data
                return false;

            transferred = length;
            return true;
        }

        /// <summary>
        /// Control request with no data stage (SET_CONFIGURATION, SET_PROTOCOL
        /// and friends). The status stage runs IN when there is no data.
        /// </summary>
        public static bool TryControlOut(uint slotId, byte requestType, byte request,
                                         ushort value, ushort index)
        {
            int di = IndexOfSlot(slotId);
            if (di < 0) return false;

            ref Device d = ref s_devices[di];

            uint* setup = (uint*)(d.TransferRing + d.TrEnqueue * TrbSize);
            setup[0] = (uint)(requestType | (request << 8) | (value << 16));
            setup[1] = index;
            setup[2] = 8;
            setup[3] = (TRB_SETUP << 10) | TRB_IDT | d.TrCycle;   // TRT = 0, no data
            AdvanceTransferRing(ref d);

            uint* status = (uint*)(d.TransferRing + d.TrEnqueue * TrbSize);
            status[0] = 0;
            status[1] = 0;
            status[2] = 0;
            status[3] = (TRB_STATUS << 10) | (1u << 16) | TRB_IOC | d.TrCycle;
            AdvanceTransferRing(ref d);

            Write32(s_doorbellBase + slotId * 4, 1);

            if (!TryWaitEvent(TRB_TRANSFER_EVENT, 1000, out uint code, out _))
                return false;
            return code == 1 || code == 13;
        }

        private static void AdvanceTransferRing(ref Device d)
        {
            d.TrEnqueue++;
            if (d.TrEnqueue >= RingTrbs - 1)
            {
                uint* link = (uint*)(d.TransferRing + (RingTrbs - 1) * TrbSize);
                link[0] = (uint)d.TransferRing;
                link[1] = (uint)(d.TransferRing >> 32);
                link[2] = 0;
                link[3] = (TRB_LINK << 10) | TRB_TOGGLE_CYCLE | d.TrCycle;
                d.TrEnqueue = 0;
                d.TrCycle ^= 1;
            }
        }

        private static int IndexOfSlot(uint slotId)
        {
            for (int i = 0; i < s_deviceCount; i++)
                if (s_devices[i].SlotId == slotId && s_devices[i].Addressed) return i;
            return -1;
        }

        /// <summary>Fetches the 18-byte device descriptor into DMA memory.</summary>
        public static bool TryGetDeviceDescriptor(uint slotId, out ushort vendor, out ushort product,
                                                  out byte deviceClass, out byte maxPacket)
        {
            vendor = 0; product = 0; deviceClass = 0; maxPacket = 0;

            ulong buf = DmaMemory.AllocPages(1);
            if (buf == 0) return false;

            if (!TryControlIn(slotId, 0x80, 6, 0x0100, 0, (void*)buf, 18, out _))
                return false;

            byte* b = (byte*)buf;
            if (b[1] != 1) return false;      // descriptor type must be DEVICE

            maxPacket = b[7];
            deviceClass = b[4];
            vendor = (ushort)(b[8] | (b[9] << 8));
            product = (ushort)(b[10] | (b[11] << 8));
            return true;
        }
    }
}
