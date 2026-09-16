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

        public static bool IsPresent => s_present;

        /// <summary>Finds a configured CDC-ACM device and stages buffers for it.</summary>
        public static bool TryAttach()
        {
            if (s_present) return true;
            if (!Xhci.TryFindCdcAcm(out XhciController hc, out uint slot)) return false;

            s_txBuffer = DmaMemory.AllocPages(1);
            if (s_txBuffer == 0) return false;

            s_hc = hc;
            s_slot = slot;
            s_txSize = 4096;
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

        // Reading is deliberately absent. A bulk IN returns fewer bytes than
        // asked whenever the other end sent less, and the length lives in the
        // transfer event's residue field, which TryWaitEvent does not hand
        // back — so a read could only guess how much of the buffer is real.
        // Input from the rig arrives as keystrokes over HID instead; when a
        // command channel is wanted, plumb the residue first.
    }
}
