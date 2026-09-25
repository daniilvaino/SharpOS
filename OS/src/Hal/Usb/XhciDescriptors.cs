namespace OS.Hal.Usb
{
    // Reading what a device says about itself. Every method here is a
    // GET_DESCRIPTOR and a copy out: nothing is claimed, configured or bound.
    //
    // Kept apart from XhciDevice/XhciInterfaces because the question is a
    // different one. Those decide what to drive, and they answer it with a
    // fixed set of slots — one HID, one storage, one serial. This file answers
    // "what is actually on the bus", which is where a census, a device manager
    // and every RS-Key decision have to start, and which no amount of driver
    // binding will tell you: a device we decline looks exactly like one whose
    // descriptor we misread.
    internal sealed unsafe partial class XhciController
    {
        private const byte DESC_DEVICE = 1;
        private const byte DESC_STRING = 3;

        // Belongs to the HID class, not to the standard set, and it is fetched
        // from the INTERFACE rather than from the device — see
        // TryFetchReportDescriptor.
        private const byte DESC_REPORT = 0x22;

        // One page, reused by every read here.
        //
        // DmaMemory has no free, so a page taken per call is a page gone for
        // good. The enumeration path does exactly that and this is not the
        // step to rework it, but nothing new should add to the tally — and a
        // census asks four or five questions per device rather than one.
        //
        // Single-threaded by construction: these run from the boot probe or
        // from a device-manager query, never from an interrupt, and never
        // while another descriptor read is in flight on the same controller.
        private ulong _descScratch;

        private ulong DescriptorScratch()
        {
            if (_descScratch == 0) _descScratch = DmaMemory.AllocPages(1);
            return _descScratch;
        }

        // Why the last read here returned false. Without it a failure prints
        // as "unreadable", which covers a device that refused, a transfer that
        // timed out and a page we could not allocate -- three different
        // problems that need three different answers. The first run of the
        // census hit one of them and said nothing about which.
        //   1 no DMA scratch page   2 transfer failed
        //   3 wrong descriptor type 4 length out of range
        private uint _descFail;
        public uint LastDescriptorFailure => _descFail;

        private static string DescriptorFailureName(uint code)
            => code switch
            {
                1 => "no DMA page",
                2 => "transfer failed",
                3 => "wrong descriptor type",
                4 => "bad length",
                _ => "none",
            };

        /// <summary>Reads back the last failure as text, for the log line.</summary>
        public string LastDescriptorFailureName => DescriptorFailureName(_descFail);

        /// <summary>
        /// The state of the default control endpoint, read straight out of the
        /// device context, plus where our own ring pointer stands.
        /// </summary>
        /// <remarks>
        /// Pure reads: no transfer, no command, nothing a device can refuse.
        ///
        /// Here to answer one question the census raised and could not settle.
        /// Control transfers work before Configure Endpoint and time out after
        /// it — not a stall, no completion event at all — so either the
        /// controller stopped the endpoint, or its dequeue pointer and our
        /// enqueue pointer have drifted apart and the doorbell rings over work
        /// it is not looking at. Those two have different fixes, and the
        /// numbers below tell them apart in one line.
        ///
        /// EP state: 0 disabled, 1 running, 2 halted, 3 stopped, 4 error.
        /// </remarks>
        public bool TryReadControlEndpointState(uint slotId,
                                                out uint slotState, out uint epState,
                                                out ulong epDequeue, out bool epCycle,
                                                out ulong ringBase, out uint enqueue,
                                                out uint ourCycle)
        {
            slotState = 0; epState = 0; epDequeue = 0; epCycle = false;
            ringBase = 0; enqueue = 0; ourCycle = 0;

            int di = IndexOfSlot(slotId);
            if (di < 0) return false;

            ref Device d = ref _devices[di];
            if (d.DeviceContext == 0) return false;

            uint cs = ContextSize;
            uint* slot = (uint*)d.DeviceContext;
            slotState = (slot[3] >> 27) & 0x1F;

            // DCI 1 is the default control endpoint, one context past the slot.
            uint* ep0 = (uint*)(d.DeviceContext + cs);
            epState = ep0[0] & 0x7;
            ulong dq = ((ulong)ep0[3] << 32) | ep0[2];
            epCycle = (dq & 1) != 0;
            epDequeue = dq & ~0xFUL;

            ringBase = d.TransferRing;
            enqueue = d.TrEnqueue;
            ourCycle = d.TrCycle;
            return true;
        }

        /// <summary>
        /// The 18-byte device descriptor, verbatim. Unlike
        /// TryGetDeviceDescriptor this renegotiates nothing and decodes
        /// nothing — the caller reads the bytes it cares about.
        /// </summary>
        public bool TryFetchDeviceDescriptor(uint slotId, byte* buffer, int max, out int length)
        {
            length = 0;
            _descFail = 0;
            if (IndexOfSlot(slotId) < 0) return false;

            ulong buf = DescriptorScratch();
            if (buf == 0) { _descFail = 1; return false; }

            if (!TryControlIn(slotId, 0x80, 6, DESC_DEVICE << 8, 0, (void*)buf, 18, out _))
            { _descFail = 2; return false; }

            byte* p = (byte*)buf;
            if (p[1] != DESC_DEVICE) { _descFail = 3; return false; }

            int n = 18 < max ? 18 : max;
            for (int i = 0; i < n; i++) buffer[i] = p[i];
            length = n;
            return true;
        }

        /// <summary>
        /// A string descriptor, as the device stores it: a two-byte header
        /// followed by UTF-16LE. Index 0 is not a string but the list of
        /// language ids, which is how a caller finds a langId to pass here.
        /// </summary>
        public bool TryFetchStringDescriptor(uint slotId, byte index, ushort langId,
                                             byte* buffer, int max, out int length)
        {
            length = 0;
            _descFail = 0;
            if (IndexOfSlot(slotId) < 0) return false;

            // Index 0 means "no string", and asking for it as if it were one
            // gets the language list back — a caller that printed that as text
            // would show two bytes of mojibake and look like a decoding bug.
            if (index == 0 && langId != 0) { _descFail = 4; return false; }

            ulong buf = DescriptorScratch();
            if (buf == 0) { _descFail = 1; return false; }

            // Two bytes first for the length. Descriptors are self-describing
            // and a device is entitled to stall an over-long request; asking
            // for 255 up front works on an emulator and fails on hardware,
            // which is the same trap TryGetDeviceDescriptor documents.
            if (!TryControlIn(slotId, 0x80, 6, (ushort)((DESC_STRING << 8) | index),
                              langId, (void*)buf, 2, out _))
            { _descFail = 2; return false; }

            byte* p = (byte*)buf;
            byte total = p[0];
            if (total < 2) { _descFail = 4; return false; }
            if (p[1] != DESC_STRING) { _descFail = 3; return false; }

            if (!TryControlIn(slotId, 0x80, 6, (ushort)((DESC_STRING << 8) | index),
                              langId, (void*)buf, total, out _))
            { _descFail = 2; return false; }

            int n = total < max ? total : max;
            for (int i = 0; i < n; i++) buffer[i] = p[i];
            length = n;
            return true;
        }

        /// <summary>
        /// The first language id the device offers, or 0 when it offers none.
        /// </summary>
        public ushort FirstLanguageId(uint slotId)
        {
            byte* langs = stackalloc byte[8];
            if (!TryFetchStringDescriptor(slotId, 0, 0, langs, 8, out int len) || len < 4)
                return 0;
            return (ushort)(langs[2] | (langs[3] << 8));
        }

        /// <summary>
        /// A HID interface's report descriptor.
        /// </summary>
        /// <remarks>
        /// The one request here that is not shaped like the others: recipient
        /// is INTERFACE (bmRequestType 0x81), and wIndex carries which one.
        /// A report descriptor belongs to an interface, so a composite device
        /// with two HID functions has two of them, and on a device without a
        /// boot protocol they are the only thing that tells the functions
        /// apart — the interface descriptors can be byte-for-byte identical.
        ///
        /// <paramref name="want"/> comes from the HID descriptor (type 0x21)
        /// that sits between the interface and its endpoints; asking for more
        /// than the device holds is a stall on some hardware.
        /// </remarks>
        public bool TryFetchReportDescriptor(uint slotId, byte interfaceNumber, ushort want,
                                             byte* buffer, int max, out int length)
        {
            length = 0;
            _descFail = 0;
            if (IndexOfSlot(slotId) < 0) return false;
            if (want == 0 || want > 4096) { _descFail = 4; return false; }

            ulong buf = DescriptorScratch();
            if (buf == 0) { _descFail = 1; return false; }

            if (!TryControlIn(slotId, 0x81, 6, DESC_REPORT << 8, interfaceNumber,
                              (void*)buf, want, out uint transferred))
            { _descFail = 2; return false; }

            byte* p = (byte*)buf;
            int got = transferred != 0 && transferred < want ? (int)transferred : want;
            int n = got < max ? got : max;
            for (int i = 0; i < n; i++) buffer[i] = p[i];
            length = n;
            return true;
        }
    }
}
