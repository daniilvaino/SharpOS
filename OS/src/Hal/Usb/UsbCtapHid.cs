namespace OS.Hal.Usb
{
    // CTAPHID: the frame protocol FIDO keys speak over a HID interface.
    //
    // Nothing about it is HID-like beyond the wire it runs on. Reports are
    // fixed-size frames — 64 bytes on every key worth the name — and a message
    // longer than one frame is split across a first frame and numbered
    // continuations. The first four bytes of every frame are the channel, so
    // several programs can talk to one key without interleaving.
    //
    //   initialisation frame:  CID(4) CMD(1, bit 7 set) BCNTH BCNTL data(57)
    //   continuation frame:    CID(4) SEQ(1, bit 7 clear)          data(59)
    //
    // Only CTAPHID_INIT is implemented here, and deliberately: it is the one
    // command that proves the channel end to end without needing a credential,
    // a user gesture or any state on the key. It is sent to the broadcast
    // channel with eight bytes of nonce, and the key answers with the same
    // nonce and a channel of its own. If the nonce comes back, the transport
    // works; if it does not, nothing further is worth writing.
    internal static unsafe class UsbCtapHid
    {
        // The usage a FIDO interface declares, and the only way to find it:
        // its interface descriptor is byte-for-byte a keyboard's.
        public const ushort FidoUsagePage = 0xF1D0;
        public const ushort FidoUsage = 0x0001;

        public const uint BroadcastChannel = 0xFFFFFFFF;

        private const byte CmdInit = 0x86;      // 0x06 with the initialisation bit
        private const byte CmdCbor = 0x90;      // 0x10, CTAP2 commands
        private const byte CmdKeepAlive = 0xBB;
        private const byte CmdError = 0xBF;
        private const int NonceLength = 8;

        // Payload room in each kind of frame: seven bytes of header in the
        // first, five in the rest.
        private const int InitPayload = 64 - 7;
        private const int ContPayload = 64 - 5;

        /// <summary>Last CTAPHID_ERROR code, when a command came back refused.</summary>
        public static byte LastError => s_lastError;
        private static byte s_lastError;

        /// <summary>How many KEEPALIVE frames the last exchange sat through.</summary>
        public static uint LastKeepAlives => s_keepAlives;
        private static uint s_keepAlives;

        private static XhciController s_hc;
        private static int s_function = -1;
        private static ushort s_frameSize;
        private static bool s_present;

        public static bool IsPresent => s_present;
        public static ushort FrameSize => s_frameSize;

        /// <summary>
        /// Find a configured FIDO interface on any controller. False when the
        /// machine has no security key, which is the ordinary case.
        /// </summary>
        public static bool TryAttach()
        {
            if (s_present) return true;

            for (int i = 0; i < Xhci.Count; i++)
            {
                XhciController hc = Xhci.Get(i);
                int fn = hc.FindHidByUsage(FidoUsagePage, FidoUsage);
                if (fn < 0) continue;

                // A transport needs both directions. An interface that only
                // reports could never be sent a command, and saying so here is
                // cheaper than failing inside the first exchange.
                if (!hc.HidHasOutAt(fn)) continue;

                s_hc = hc;
                s_function = fn;
                s_frameSize = hc.HidMaxPacketAt(fn);
                s_present = s_frameSize >= 64;
                return s_present;
            }

            return false;
        }

        /// <summary>
        /// One command and its answer, frames assembled in both directions.
        /// </summary>
        /// <remarks>
        /// A message longer than one frame is split: a first frame carrying
        /// the command and the total length, then continuations numbered from
        /// zero. Nothing about authenticatorGetInfo fits in 57 bytes, so a
        /// transport that only ever sent and received one frame could ask the
        /// key nothing worth asking.
        ///
        /// Two kinds of frame are not the answer and must not be mistaken for
        /// one: KEEPALIVE, which a key sends while it thinks or waits for a
        /// finger, and ERROR, which is a refusal with a reason.
        /// </remarks>
        public static bool TryTransceive(uint channel, byte command,
                                         byte* payload, int payloadLength,
                                         byte* response, int maxResponse,
                                         out int responseLength, uint timeoutMs)
        {
            responseLength = 0;
            s_lastError = 0;
            s_keepAlives = 0;
            if (!s_present || payloadLength < 0) return false;

            byte* frame = stackalloc byte[64];

            OS.Kernel.Threading.Preemption.Suppress();
            try
            {
                // ---- out: the first frame, then continuations ----
                int sent = payloadLength < InitPayload ? payloadLength : InitPayload;
                ClearFrame(frame);
                WriteChannel(frame, channel);
                frame[4] = command;
                frame[5] = (byte)(payloadLength >> 8);
                frame[6] = (byte)payloadLength;
                for (int i = 0; i < sent; i++) frame[7 + i] = payload[i];
                if (!s_hc.TryWriteReportOn(s_function, frame, 64, timeoutMs)) return false;

                byte sequence = 0;
                while (sent < payloadLength)
                {
                    int chunk = payloadLength - sent;
                    if (chunk > ContPayload) chunk = ContPayload;

                    ClearFrame(frame);
                    WriteChannel(frame, channel);
                    frame[4] = sequence++;
                    for (int i = 0; i < chunk; i++) frame[5 + i] = payload[sent + i];
                    if (!s_hc.TryWriteReportOn(s_function, frame, 64, timeoutMs)) return false;
                    sent += chunk;
                }

                // ---- in: skip what is not the answer, then assemble it ----
                int total = -1;
                int taken = 0;
                byte expected = 0;

                for (int frames = 0; frames < 128; frames++)
                {
                    if (!s_hc.TryReadReportOn(s_function, frame, 64, timeoutMs)) return false;
                    if (!ChannelIs(frame, channel)) continue;

                    if (total < 0)
                    {
                        if ((frame[4] & 0x80) == 0) continue;        // a stray continuation
                        if (frame[4] == CmdKeepAlive) { s_keepAlives++; continue; }
                        if (frame[4] == CmdError)
                        {
                            s_lastError = frame[7];
                            return false;
                        }
                        if (frame[4] != command) continue;

                        total = (frame[5] << 8) | frame[6];
                        if (total > maxResponse) return false;

                        int n = total < InitPayload ? total : InitPayload;
                        for (int i = 0; i < n; i++) response[i] = frame[7 + i];
                        taken = n;
                    }
                    else
                    {
                        if ((frame[4] & 0x80) != 0) continue;        // a new message, not ours
                        if (frame[4] != expected) return false;      // a gap: the message is broken
                        expected++;

                        int left = total - taken;
                        int n = left < ContPayload ? left : ContPayload;
                        for (int i = 0; i < n; i++) response[taken + i] = frame[5 + i];
                        taken += n;
                    }

                    if (taken >= total)
                    {
                        responseLength = total;
                        return true;
                    }
                }

                return false;
            }
            finally
            {
                OS.Kernel.Threading.Preemption.Allow();
            }
        }

        /// <summary>
        /// authenticatorGetInfo (CTAP2 command 0x04): what this key supports.
        /// The answer is a CBOR map, returned raw — nothing here pretends to
        /// decode it.
        /// </summary>
        /// <param name="status">CTAP status byte; 0 is success.</param>
        public static bool TryGetInfo(uint channel, byte* buffer, int max,
                                      out int length, out byte status, uint timeoutMs)
        {
            length = 0;
            status = 0xFF;

            byte* command = stackalloc byte[1];
            command[0] = 0x04;                  // authenticatorGetInfo

            if (!TryTransceive(channel, CmdCbor, command, 1, buffer, max,
                               out int got, timeoutMs))
                return false;
            if (got < 1) return false;

            // The first byte of a CBOR response is the status, and the CBOR
            // itself starts after it. Shifted down so the caller is handed the
            // map alone and not a map with a byte glued to its front.
            status = buffer[0];
            length = got - 1;
            for (int i = 0; i < length; i++) buffer[i] = buffer[i + 1];
            return true;
        }

        private static void ClearFrame(byte* frame)
        {
            for (int i = 0; i < 64; i++) frame[i] = 0;
        }

        private static void WriteChannel(byte* frame, uint channel)
        {
            frame[0] = (byte)(channel >> 24);
            frame[1] = (byte)(channel >> 16);
            frame[2] = (byte)(channel >> 8);
            frame[3] = (byte)channel;
        }

        private static bool ChannelIs(byte* frame, uint channel)
            => frame[0] == (byte)(channel >> 24) && frame[1] == (byte)(channel >> 16)
            && frame[2] == (byte)(channel >> 8) && frame[3] == (byte)channel;

        /// <summary>
        /// CTAPHID_INIT on the broadcast channel: hands the key a nonce and
        /// takes back a channel of our own.
        /// </summary>
        /// <param name="nonce">Eight bytes; the key must echo them exactly.</param>
        public static bool TryInit(byte* nonce, out uint channel,
                                   out byte protocolVersion, out byte capabilities,
                                   uint timeoutMs)
        {
            channel = 0;
            protocolVersion = 0;
            capabilities = 0;
            if (!s_present) return false;

            byte* frame = stackalloc byte[64];
            for (int i = 0; i < 64; i++) frame[i] = 0;

            // Channel first, big-endian, as everything in this protocol is.
            frame[0] = 0xFF; frame[1] = 0xFF; frame[2] = 0xFF; frame[3] = 0xFF;
            frame[4] = CmdInit;
            frame[5] = 0;                 // BCNTH
            frame[6] = NonceLength;       // BCNTL
            for (int i = 0; i < NonceLength; i++) frame[7 + i] = nonce[i];

            // One transfer owns the controller: the rings carry one enqueue
            // index apiece and nothing tells two completions apart.
            OS.Kernel.Threading.Preemption.Suppress();
            try
            {
                if (!s_hc.TryWriteReportOn(s_function, frame, 64, timeoutMs)) return false;

                // The answer may not be the first frame back — a key that was
                // mid-conversation with someone else can be finishing it — so
                // frames that are not ours are skipped rather than failing the
                // exchange.
                byte* reply = stackalloc byte[64];
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    if (!s_hc.TryReadReportOn(s_function, reply, 64, timeoutMs)) return false;

                    bool broadcast = reply[0] == 0xFF && reply[1] == 0xFF
                                  && reply[2] == 0xFF && reply[3] == 0xFF;
                    if (!broadcast || reply[4] != CmdInit) continue;

                    // The nonce is the whole point: it is what says this answer
                    // belongs to this request and not to a stale one.
                    bool echoed = true;
                    for (int i = 0; i < NonceLength; i++)
                        if (reply[7 + i] != nonce[i]) { echoed = false; break; }
                    if (!echoed) continue;

                    channel = (uint)((reply[15] << 24) | (reply[16] << 16)
                                   | (reply[17] << 8) | reply[18]);
                    protocolVersion = reply[19];
                    capabilities = reply[23];
                    return true;
                }

                return false;
            }
            finally
            {
                OS.Kernel.Threading.Preemption.Allow();
            }
        }
    }
}
