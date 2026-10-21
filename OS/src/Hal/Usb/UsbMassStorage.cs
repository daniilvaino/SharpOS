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

        /// <summary>The controller the disk is on, null before Init.</summary>
        internal static XhciController Controller => s_hc;
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

        // SYNCHRONIZE CACHE (10), opcode 0x35 (step208): the stick's cache to
        // its flash, all of it (LBA 0, count 0). No data phase.
        public static bool SynchronizeCache()
        {
            if (!s_present) return false;
            byte* cb = stackalloc byte[16];
            for (int i = 0; i < 16; i++) cb[i] = 0;
            cb[0] = 0x35;
            return TryCommand(cb, 10, null, 0, dataIn: false);
        }

        // A command, and once more if it failed in a way that took a reset
        // recovery: the reset is what makes a second attempt meaningful. A
        // refusal in the status block (check condition) is an answer, not a
        // lost channel, and is not repeated.
        private static bool TryCommand(byte* commandBlock, byte commandLength,
                                       void* data, uint dataLength, bool dataIn)
        {
            ulong recoveries = s_recoveries;
            if (TryCommandOnce(commandBlock, commandLength, data, dataLength, dataIn)) return true;
            if (s_recoveries == recoveries) return false;
            bool again = TryCommandOnce(commandBlock, commandLength, data, dataLength, dataIn);
            if (again) s_retriesPassed++;
            return again;
        }

        private static ulong s_failures;
        private static ulong s_recoveries;
        private static ulong s_retriesPassed;
        public static ulong Failures => s_failures;
        public static ulong Recoveries => s_recoveries;
        public static ulong RetriesPassed => s_retriesPassed;

        // Phases, for the log line.
        private const int PhaseCommand = 1, PhaseData = 2, PhaseStatus = 3, PhaseReply = 4;

        // Command / data / status, the three phases of BOT, and recovery as
        // the specification orders it (BOT 1.0, 5.3 and 6.6):
        //   a STALL on the data pipe - clear it, the status block still comes;
        //   a STALL on the status read - clear the IN pipe, read it once more;
        //   anything else - a timeout, a status block that is not one, a tag
        //   from another command, phase error - is Reset Recovery.
        // Before step208 every failure just returned false: the endpoint stayed
        // halted, an abandoned TRB stayed queued, and each later command timed
        // out or took its neighbour's reply. The disk looked empty and every
        // access waited five seconds, until reboot.
        private static bool TryCommandOnce(byte* commandBlock, byte commandLength,
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
                return Fail(commandBlock[0], PhaseCommand, s_hc.LastCompletionCode, reset: true);

            if (dataLength > 0 && data != null)
            {
                if (!s_hc.TryBulkTransfer(s_slot, dataIn, data, dataLength, out _))
                {
                    uint code = s_hc.LastCompletionCode;
                    if (code != CodeStall)
                        return Fail(commandBlock[0], PhaseData, code, reset: true);
                    Note(commandBlock[0], PhaseData, code);
                    if (!TryClearHalt(dataIn)) return Fail(commandBlock[0], PhaseData, code, reset: true);
                }
            }

            byte* csw = (byte*)(s_cmdBuffer + 64);
            if (!TryReadStatus(csw, StatusTimeout(commandBlock[0])))
            {
                uint code = s_hc.LastCompletionCode;
                if (code != CodeStall || !TryClearHalt(true) || !TryReadStatus(csw, StatusTimeout(commandBlock[0])))
                    return Fail(commandBlock[0], PhaseStatus, s_hc.LastCompletionCode, reset: true);
            }

            // Not a status block, or the reply to another command: the two
            // sides disagree about where they are.
            if (Get32(csw, 0) != CswSignature || Get32(csw, 4) != tag || csw[12] > 1)
                return Fail(commandBlock[0], PhaseReply, csw[12], reset: true);
            return csw[12] == 0;                           // 0 = passed, 1 = refused
        }

        /// <summary>
        /// A command block with a wrong signature, for the boot probe: the
        /// device must refuse it (QEMU stalls the OUT pipe), and the way back
        /// is a full reset recovery. True when the refusal came and recovery
        /// ran; whether the stick then works is for the probe's next read.
        /// </summary>
        public static bool ProvokeResetRecovery()
        {
            if (!s_present) return false;
            byte* cbw = (byte*)s_cmdBuffer;
            for (int i = 0; i < CbwLength; i++) cbw[i] = 0;
            Put32(cbw, 0, 0x0BADC0DE);
            Put32(cbw, 4, s_tag++);
            cbw[14] = 6;                                   // TEST UNIT READY

            ulong recoveries = s_recoveries;
            if (s_hc.TryBulkTransfer(s_slot, false, (void*)s_cmdBuffer, CbwLength, out _))
            {
                // Taken without complaint: the device now waits to send a
                // status block for a command it never ran. Read it, or be
                // out of step - that, too, is recovery's job.
                byte* csw = (byte*)(s_cmdBuffer + 64);
                if (!TryReadStatus(csw, 5000)) Fail(0x00, PhaseStatus, s_hc.LastCompletionCode, reset: true);
                else ResetRecovery();
            }
            else Fail(0x00, PhaseCommand, s_hc.LastCompletionCode, reset: true);
            return s_recoveries != recoveries;
        }

        // xHCI completion code of a STALL.
        private const uint CodeStall = 6;

        private static bool TryReadStatus(byte* csw, uint timeoutMs)
        {
            for (int i = 0; i < CswLength; i++) csw[i] = 0;
            return s_hc.TryBulkTransfer(s_slot, true, csw, CswLength, timeoutMs, out _);
        }

        // How long a status block may take. SYNCHRONIZE CACHE answers only once
        // the medium has everything: a stick's flash, or under QEMU the host
        // flushing the image file - measured past 2.8 s under WHPX after the
        // battery's writes, where the old flat limit gave up, lost step with
        // the device, and every later command failed until reboot (step208).
        // Linux allows a flush a minute.
        private static uint StatusTimeout(byte opcode) => opcode == 0x35 ? 60_000u : 5000u;

        // Both halves of a halted pipe: the controller's endpoint, then the
        // device's halt feature (CLEAR_FEATURE ENDPOINT_HALT, which also
        // resets its data toggle to match the controller's).
        private static bool TryClearHalt(bool directionIn)
        {
            bool host = s_hc.TryResetBulkEndpoint(s_slot, directionIn);
            bool device = s_hc.TryControlOut(s_slot, 0x02, 0x01, 0, s_hc.BulkAddress(s_slot, directionIn));
            return host && device;
        }

        // Reset Recovery (BOT 5.3.4): the class reset, which puts the device
        // back to waiting for a command block, then both pipes cleared.
        private static void ResetRecovery()
        {
            s_recoveries++;
            bool reset = s_hc.TryControlOut(s_slot, 0x21, 0xFF, 0, s_hc.MassStorageInterface(s_slot));
            bool inPipe = TryClearHalt(true);
            bool outPipe = TryClearHalt(false);
            Serial.WriteString("[usbmsd] reset recovery: class reset ");
            Serial.WriteString(reset ? "ok" : "FAILED");
            Serial.WriteString(", in pipe ");
            Serial.WriteString(inPipe ? "ok" : "FAILED");
            Serial.WriteString(", out pipe ");
            Serial.WriteString(outPipe ? "ok\n" : "FAILED\n");
        }

        private static bool Fail(byte opcode, int phase, uint code, bool reset)
        {
            Note(opcode, phase, code);
            if (reset) ResetRecovery();
            return false;
        }

        // To COM1 only: a line on the screen would land in the middle of
        // whatever the shell is drawing.
        private static void Note(byte opcode, int phase, uint code)
        {
            s_failures++;
            Serial.WriteString("[usbmsd] scsi 0x");
            Serial.WriteHex(opcode, 2);
            Serial.WriteString(phase == PhaseCommand ? " failed sending the command"
                             : phase == PhaseData ? " failed moving data"
                             : phase == PhaseStatus ? " failed reading status"
                             : " got a bad status block");
            Serial.WriteString(phase == PhaseReply ? ", status 0x" : ", xhci code 0x");
            Serial.WriteHex(code, 2);
            Serial.WriteString(code == 0 && phase != PhaseReply ? " (no event, timeout)\n" : "\n");
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
        // Holding the controller: the CPU while it is polled, its lock once it
        // interrupts (XhciController.Enter, step210).
        public override bool Read(ulong sector, uint count, byte* data)
        {
            XhciController hc = UsbMassStorage.Controller;
            bool locked = XhciController.Enter(hc);
            try { return UsbMassStorage.Read(sector, count, data); }
            finally { XhciController.Leave(hc, locked); }
        }

        public override bool Write(ulong sector, uint count, byte* data)
        {
            XhciController hc = UsbMassStorage.Controller;
            bool locked = XhciController.Enter(hc);
            try { return UsbMassStorage.Write(sector, count, data); }
            finally { XhciController.Leave(hc, locked); }
        }

        public override bool Flush()
        {
            XhciController hc = UsbMassStorage.Controller;
            bool locked = XhciController.Enter(hc);
            try { return UsbMassStorage.SynchronizeCache(); }
            finally { XhciController.Leave(hc, locked); }
        }
    }
}
