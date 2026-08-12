namespace OS.Hal.Usb
{
    // A USB stick as a Disk, so FAT32 mounts it with no idea it is USB.
    //
    // Bulk-Only Transport wraps SCSI: a 31-byte command block goes out, data
    // moves, a 13-byte status block comes back. The tag written into the
    // command must come back in the status — that is the only thing tying a
    // reply to its request, and mismatches mean the two sides have lost sync.
    internal static unsafe class UsbMassStorage
    {
        private const uint CbwSignature = 0x43425355;
        private const uint CswSignature = 0x53425355;

        private const int CbwLength = 31;
        private const int CswLength = 13;

        private static XhciController s_hc;
        private static uint s_slot;
        private static bool s_present;
        private static uint s_tag = 1;
        private static ulong s_cmdBuffer;     // CBW / CSW staging, DMA-visible
        private static ulong s_dataBuffer;    // bounce buffer for one transfer
        private static uint s_blockSize = 512;
        private static ulong s_blockCount;

        public static bool IsPresent => s_present;
        public static uint BlockSize => s_blockSize;
        public static ulong BlockCount => s_blockCount;

        /// <summary>Finds a configured mass-storage device and sizes it up.</summary>
        public static bool TryAttach()
        {
            if (s_present) return true;

            s_failStage = 1;                 // nothing that calls itself storage
            if (Xhci.TryFindMassStorage(out XhciController hc, out uint slot))
            {
                s_hc = hc;
                s_slot = slot;
                s_cmdBuffer = DmaMemory.AllocPages(1);
                s_dataBuffer = DmaMemory.AllocPages(16);   // 64 KiB of transfer
                if (s_cmdBuffer == 0 || s_dataBuffer == 0) return false;

                s_present = true;

                // A freshly attached device answers the first commands with a
                // check condition ("unit attention" — the medium just
                // appeared) and refuses to move on until asked WHY. Issuing
                // REQUEST SENSE is what clears it; without that, every later
                // command keeps failing and the device looks broken.
                Inquiry();
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    if (TestUnitReady()) break;
                    RequestSense();
                }

                for (int attempt = 0; attempt < 4; attempt++)
                {
                    if (TryReadCapacity()) return true;
                    RequestSense();
                }

                s_failStage = 2;
                s_present = false;
                return false;
            }
            return false;
        }

        // Which step gave up, so "no storage" can be told apart from "storage
        // that never became ready".
        private static uint s_failStage;
        public static uint FailStage => s_failStage;

        /// <summary>True when a mass storage device exists, ready or not.</summary>
        public static bool DeviceSeen => s_failStage != 1 && s_slot != 0;

        // Ask the device to explain its last refusal. The answer is discarded:
        // the point is the asking, which is what clears the condition.
        private static void RequestSense()
        {
            byte* cb = stackalloc byte[16];
            for (int i = 0; i < 16; i++) cb[i] = 0;
            cb[0] = 0x03;                    // REQUEST SENSE
            cb[4] = 18;
            TryCommand(cb, 6, (void*)s_dataBuffer, 18, dataIn: true);
        }

        // Some devices expect to be identified before anything else is asked
        // of them; the data itself is not needed here.
        private static void Inquiry()
        {
            byte* cb = stackalloc byte[16];
            for (int i = 0; i < 16; i++) cb[i] = 0;
            cb[0] = 0x12;                    // INQUIRY
            cb[4] = 36;
            TryCommand(cb, 6, (void*)s_dataBuffer, 36, dataIn: true);
        }

        private static bool TestUnitReady()
        {
            byte* cb = stackalloc byte[16];
            for (int i = 0; i < 16; i++) cb[i] = 0;
            return TryCommand(cb, 6, null, 0, dataIn: false);
        }

        private static bool TryReadCapacity()
        {
            byte* cb = stackalloc byte[16];
            for (int i = 0; i < 16; i++) cb[i] = 0;
            cb[0] = 0x25;                    // READ CAPACITY (10)

            if (!TryCommand(cb, 10, (void*)s_dataBuffer, 8, dataIn: true))
                return false;

            byte* r = (byte*)s_dataBuffer;
            // Both fields are big-endian, unlike everything else we touch.
            ulong lastLba = ((ulong)r[0] << 24) | ((ulong)r[1] << 16)
                          | ((ulong)r[2] << 8) | r[3];
            uint blockSize = ((uint)r[4] << 24) | ((uint)r[5] << 16)
                           | ((uint)r[6] << 8) | r[7];

            if (blockSize == 0 || blockSize > 4096) return false;
            s_blockSize = blockSize;
            s_blockCount = lastLba + 1;
            return true;
        }

        public static bool Read(ulong lba, uint count, byte* destination)
        {
            if (!s_present || count == 0) return false;

            uint bytes = count * s_blockSize;
            if (bytes > 64 * 1024) return false;

            byte* cb = stackalloc byte[16];
            for (int i = 0; i < 16; i++) cb[i] = 0;
            cb[0] = 0x28;                                  // READ (10)
            cb[2] = (byte)(lba >> 24); cb[3] = (byte)(lba >> 16);
            cb[4] = (byte)(lba >> 8); cb[5] = (byte)lba;
            cb[7] = (byte)(count >> 8); cb[8] = (byte)count;

            if (!TryCommand(cb, 10, (void*)s_dataBuffer, bytes, dataIn: true))
                return false;

            byte* src = (byte*)s_dataBuffer;
            for (uint i = 0; i < bytes; i++) destination[i] = src[i];
            return true;
        }

        public static bool Write(ulong lba, uint count, byte* source)
        {
            if (!s_present || count == 0) return false;

            uint bytes = count * s_blockSize;
            if (bytes > 64 * 1024) return false;

            byte* dst = (byte*)s_dataBuffer;
            for (uint i = 0; i < bytes; i++) dst[i] = source[i];

            byte* cb = stackalloc byte[16];
            for (int i = 0; i < 16; i++) cb[i] = 0;
            cb[0] = 0x2A;                                  // WRITE (10)
            cb[2] = (byte)(lba >> 24); cb[3] = (byte)(lba >> 16);
            cb[4] = (byte)(lba >> 8); cb[5] = (byte)lba;
            cb[7] = (byte)(count >> 8); cb[8] = (byte)count;

            return TryCommand(cb, 10, (void*)s_dataBuffer, bytes, dataIn: false);
        }

        // Command / data / status, the three phases of BOT.
        private static bool TryCommand(byte* commandBlock, byte commandLength,
                                       void* data, uint dataLength, bool dataIn)
        {
            uint tag = s_tag++;

            byte* cbw = (byte*)s_cmdBuffer;
            for (int i = 0; i < CbwLength; i++) cbw[i] = 0;
            Put32(cbw, 0, CbwSignature);
            Put32(cbw, 4, tag);
            Put32(cbw, 8, dataLength);
            cbw[12] = (byte)(dataIn ? 0x80 : 0x00);
            cbw[13] = 0;                                   // LUN 0
            cbw[14] = commandLength;
            for (int i = 0; i < commandLength && i < 16; i++) cbw[15 + i] = commandBlock[i];

            if (!s_hc.TryBulkTransfer(s_slot, false, (void*)s_cmdBuffer, CbwLength, out _))
                return false;

            if (dataLength > 0 && data != null)
            {
                if (!s_hc.TryBulkTransfer(s_slot, dataIn, data, dataLength, out _))
                    return false;
            }

            byte* csw = (byte*)(s_cmdBuffer + 64);
            for (int i = 0; i < CswLength; i++) csw[i] = 0;
            if (!s_hc.TryBulkTransfer(s_slot, true, (void*)(s_cmdBuffer + 64), CswLength, out _))
                return false;

            if (Get32(csw, 0) != CswSignature) return false;
            if (Get32(csw, 4) != tag) return false;        // reply to another request
            return csw[12] == 0;                           // 0 = command passed
        }

        private static void Put32(byte* p, int offset, uint value)
        {
            p[offset] = (byte)value;
            p[offset + 1] = (byte)(value >> 8);
            p[offset + 2] = (byte)(value >> 16);
            p[offset + 3] = (byte)(value >> 24);
        }

        private static uint Get32(byte* p, int offset)
            => (uint)(p[offset] | (p[offset + 1] << 8)
                    | (p[offset + 2] << 16) | (p[offset + 3] << 24));
    }

    // The Disk face of the same device, which is all FAT32 ever sees.
    internal sealed unsafe class UsbDisk : Disk
    {
        // One transfer at a time, waits included — the same bargain the AHCI
        // driver makes, for the same reason.
        //
        // A USB transfer is a conversation: write a TRB into the ring at the
        // enqueue index, advance it, ring the doorbell, then wait for the
        // event that belongs to it. Interleaving two of those corrupts the
        // ring (both write the same slot) and, even with that fixed, there is
        // nothing here that tells one completion event from another.
        //
        // Guarding at this boundary rather than around each of the eight TRB
        // submissions inside the stack: they all have the same shape in five
        // files, and a guard that is missing from one of them is worse than no
        // guard at all, because it is trusted.
        public override bool Read(ulong sector, uint count, byte* data)
        {
            OS.Kernel.Threading.Preemption.Suppress();
            try { return UsbMassStorage.Read(sector, count, data); }
            finally { OS.Kernel.Threading.Preemption.Allow(); }
        }

        public override bool Write(ulong sector, uint count, byte* data)
        {
            OS.Kernel.Threading.Preemption.Suppress();
            try { return UsbMassStorage.Write(sector, count, data); }
            finally { OS.Kernel.Threading.Preemption.Allow(); }
        }
    }
}
