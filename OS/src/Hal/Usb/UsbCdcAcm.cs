namespace OS.Hal.Usb
{
    // A USB serial port, host side.
    //
    // CDC-ACM is the simplest class we drive: no command blocks, no status
    // phase, no tags — bytes out on one bulk endpoint, bytes in on the other.
    // What it buys is a log that leaves the machine as it is written, without
    // a filesystem underneath: on the test rig the other end is the phone,
    // reading /dev/ttyGS0 into a file the build machine can pull at any time.
    //
    // Writes are chunked to the endpoint's packet size and never wait longer
    // than a fixed timeout: a log sink that blocks forever because nobody is
    // reading is worse than one that drops a line.
    internal static unsafe class UsbCdcAcm
    {
        private const uint WriteTimeoutMs = 200;

        private static XhciController s_hc;
        private static uint s_slot;
        private static bool s_present;
        private static ulong s_txBuffer;      // DMA-visible staging
        private static uint s_txSize;
        private static ulong s_rxBuffer;      // and one for the other direction
        private static uint s_rxSize;

        public static bool IsPresent => s_present;

        /// <summary>Finds a configured CDC-ACM device and stages buffers for it.</summary>
        public static bool TryAttach()
        {
            if (s_present) return true;
            if (!Xhci.TryFindCdcAcm(out XhciController hc, out uint slot)) return false;

            s_txBuffer = DmaMemory.AllocPages(1);
            if (s_txBuffer == 0) return false;

            s_rxBuffer = DmaMemory.AllocPages(1);
            if (s_rxBuffer == 0) return false;

            s_hc = hc;
            s_slot = slot;
            s_txSize = 4096;
            s_rxSize = 4096;
            s_present = true;
            return true;
        }

        /// <summary>
        /// Write bytes to the port. Returns false on the first chunk the
        /// device would not take, leaving the rest unsent.
        /// </summary>
        public static bool Write(byte* data, int length)
        {
            if (!s_present || length <= 0) return false;

            // The rings carry one enqueue index apiece and nothing tells two
            // concurrent completions apart, so a transfer owns the controller
            // from doorbell to event.
            OS.Kernel.Threading.Preemption.Suppress();
            try
            {
                int sent = 0;
                while (sent < length)
                {
                    int chunk = length - sent;
                    if (chunk > (int)s_txSize) chunk = (int)s_txSize;

                    byte* dst = (byte*)s_txBuffer;
                    for (int i = 0; i < chunk; i++) dst[i] = data[sent + i];

                    if (!s_hc.TryCdcTransfer(s_slot, false, (void*)s_txBuffer,
                                             (uint)chunk, WriteTimeoutMs))
                        return false;

                    sent += chunk;
                }
                return true;
            }
            finally
            {
                OS.Kernel.Threading.Preemption.Allow();
            }
        }

        /// <summary>
        /// Read whatever the other end has sent, up to <paramref name="max"/>
        /// bytes. Returns 0 when nothing arrived within the timeout, which is
        /// the ordinary case for an idle port.
        /// </summary>
        /// <remarks>
        /// This used to be absent, and the reason given was that the length of
        /// a short read lives in the transfer event's residue, which
        /// TryWaitEvent threw away. It now hands it back, so `asked - residue`
        /// is the answer.
        ///
        /// It exists because HID turned out to be a dead end on the test rig:
        /// the phone's f_hid stores the report descriptor written to configfs
        /// and presents its own placeholder instead, proven against four
        /// independent readers on 2026-09-25. A serial port needs nothing from
        /// the phone's kernel beyond bytes, so the input side of the rig moved
        /// here.
        /// </remarks>
        public static int Read(byte* destination, int max, uint timeoutMs)
        {
            if (!s_present || max <= 0 || s_rxBuffer == 0) return 0;

            uint want = (uint)(max < (int)s_rxSize ? max : (int)s_rxSize);

            // Same reason the writer suppresses: one enqueue index per ring,
            // and nothing distinguishes two completions.
            OS.Kernel.Threading.Preemption.Suppress();
            try
            {
                if (!s_hc.TryCdcRead(s_slot, (void*)s_rxBuffer, want, timeoutMs, out uint got))
                    return 0;

                int n = (int)(got < want ? got : want);
                byte* src = (byte*)s_rxBuffer;
                for (int i = 0; i < n; i++) destination[i] = src[i];
                return n;
            }
            finally
            {
                OS.Kernel.Threading.Preemption.Allow();
            }
        }
    }
}
