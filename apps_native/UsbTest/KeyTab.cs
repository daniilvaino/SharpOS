using System;
using SharpOS.AppSdk;
using Terminal.Gui;

namespace UsbTest
{
    // The security key tab: one button per thing worth asking a key.
    //
    // The kernel owns CTAPHID's framing — 64-byte frames, an initial one
    // carrying the length and continuations carrying a sequence number — and
    // hands a program one exchange at a time. So everything here is about what
    // to ask and how to read the answer.
    internal static unsafe class KeyTab
    {
        // CTAPHID commands.
        private const uint CmdPing = 0x81;
        private const uint CmdMsg = 0x83;      // a U2F APDU rather than CBOR
        private const uint CmdWink = 0x88;
        private const uint CmdCbor = 0x90;

        // CTAP2 command bytes, which travel as the first byte of a CBOR payload.
        private const byte AuthenticatorGetInfo = 0x04;

        // What a key says it can do, in the capabilities byte INIT returns.
        private const byte CapabilityWink = 0x01;
        private const byte CapabilityLock = 0x02;
        private const byte CapabilityCbor = 0x04;
        private const byte CapabilityNoMsg = 0x08;

        /// <summary>Generous: a key that wants a finger will take its time.</summary>
        private const uint TimeoutMs = 5000;

        private const int ResponseCapacity = 2048;

        private static TextView s_log = null!;
        private static Label s_state = null!;

        // Zero until a key introduces itself. Channels are the key's to hand
        // out, and one from a previous run of the program is not allowed to
        // work.
        private static uint s_channel;

        private static System.Text.StringBuilder s_text = null!;

        public static View Build()
        {
            s_text = new System.Text.StringBuilder();

            var page = new View
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
            };

            // Laid out one after another rather than at fixed columns: the
            // labels carry hotkeys, so their widths are the library's business.
            var init = MakeButton("_INIT", Introduce, null);
            var info = MakeButton("get_Info", GetInfo, init);
            var ping = MakeButton("_PING", Ping, info);
            var wink = MakeButton("_WINK", Wink, ping);
            var register = MakeButton("_Register", Register, wink);
            var clear = MakeButton("C_lear", Clear, register);

            s_state = new Label("No channel yet. INIT asks the key for one.")
            {
                X = 1,
                Y = 2,
                Width = Dim.Fill(1),
            };

            var output = new FrameView("Conversation")
            {
                X = 0,
                Y = 4,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
            };

            s_log = new TextView
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                ReadOnly = true,
            };

            output.Add(s_log);
            page.Add(init, info, ping, wink, register, clear, s_state, output);
            return page;
        }

        private static Button MakeButton(string text, Action clicked, View after)
        {
            var button = new Button(text)
            {
                X = after == null ? Pos.At(1) : Pos.Right(after) + 1,
                Y = 0,
            };

            button.Clicked += clicked;
            return button;
        }

        /// <summary>Told by the bus tab, so this one can say so before being used.</summary>
        public static void NoteKeyPresence(int fidoInterfaces)
        {
            if (s_state == null || s_channel != 0) return;

            s_state.Text = fidoInterfaces == 0
                ? "No FIDO interface on the bus. Plug a key in and refresh the bus tab."
                : "A FIDO interface is present. INIT asks the key for a channel.";
        }

        private static void Clear()
        {
            s_text.Clear();
            s_log.Text = "";
            s_log.SetNeedsDisplay();
        }

        private static void Say(string line)
        {
            // Into the log as well as onto the screen, and this is not a
            // convenience. The conversation pane is a TextView: it exists on the
            // framebuffer and nowhere else, so when the key stopped answering
            // there was nothing to read afterwards, and "no reaction in the log"
            // was taken as evidence when the log could never have had any.
            //
            // WriteDiagnostic lands on the measurement channel - the serial port
            // and the disk log, never the screen - so mirroring cannot disturb
            // the interface it is mirroring.
            AppHost.WriteDiagnostic("[usbtest] " + line + "\n");

            s_text.Append(line);
            s_text.Append('\n');
            s_log.Text = s_text.ToString();

            // The interesting line is the newest one, and a log that has to be
            // scrolled to be read is a log nobody reads.
            s_log.MoveEnd();
            s_log.SetNeedsDisplay();
        }

        /// <summary>CTAPHID_INIT: ask the key for a channel of our own.</summary>
        private static void Introduce()
        {
            Say("INIT");

            AppUsbCtapRequest request = default;
            request.Channel = 0;
            request.TimeoutMs = TimeoutMs;

            AppServiceStatus status = AppHost.TryUsbCtap(ref request);
            if (status != AppServiceStatus.Ok)
            {
                Say("INIT failed: " + Failure(status, ref request));
                return;
            }

            s_channel = request.Channel;

            // The service packs the two bytes a caller needs next into Command,
            // so deciding what to ask does not cost a second exchange.
            byte version = (byte)(request.Command & 0xFF);
            byte capabilities = (byte)((request.Command >> 8) & 0xFF);

            string what = "channel 0x" + Format.Hex8(s_channel)
                        + "  CTAPHID version " + version.ToString()
                        + "  capabilities 0x" + Format.Hex2(capabilities);

            if ((capabilities & CapabilityWink) != 0) what += " wink";
            if ((capabilities & CapabilityLock) != 0) what += " lock";
            if ((capabilities & CapabilityCbor) != 0) what += " cbor";
            if ((capabilities & CapabilityNoMsg) != 0) what += " no-msg";

            Say(what);
            s_state.Text = what;
            s_state.SetNeedsDisplay();

            if ((capabilities & CapabilityCbor) == 0)
                Say("No CBOR capability: this key speaks U2F only, so getInfo will fail.");
        }

        /// <summary>authenticatorGetInfo - everything the key will say unasked.</summary>
        private static void GetInfo()
        {
            if (!HaveChannel()) return;

            byte[] payload = new byte[1];
            byte[] response = new byte[ResponseCapacity];
            payload[0] = AuthenticatorGetInfo;

            fixed (byte* payloadPin = payload)
            fixed (byte* responsePin = response)
            {
                AppUsbCtapRequest request = default;
                request.Channel = s_channel;
                request.Command = CmdCbor;
                request.PayloadAddress = (ulong)payloadPin;
                request.PayloadLength = 1;
                request.ResponseAddress = (ulong)responsePin;
                request.ResponseCapacity = ResponseCapacity;
                request.TimeoutMs = TimeoutMs;

                AppServiceStatus status = AppHost.TryUsbCtap(ref request);
                if (status != AppServiceStatus.Ok)
                {
                    Say("getInfo failed: " + Failure(status, ref request));
                    return;
                }

                if (request.ResponseLength == 0)
                {
                    Say("getInfo returned nothing");
                    return;
                }

                // The first byte of a CBOR reply is CTAP2's own status, not part
                // of the map. Non-zero means the key understood the frame and
                // refused the request, which is a different failure from a
                // transport one and worth not burying in the dump.
                byte ctapStatus = responsePin[0];
                if (ctapStatus != 0)
                {
                    Say("CTAP2 status 0x" + Format.Hex2(ctapStatus) + " - the key refused");
                    return;
                }

                string head = "CTAP2 ok, " + (request.ResponseLength - 1).ToString() + " bytes of CBOR";
                if (request.KeepAlives != 0)
                    head += ", " + request.KeepAlives.ToString() + " keepalives";
                Say(head);

                // Raw, because there is no decoder yet. Printing the bytes is
                // what makes writing one possible: a parser built against a
                // specification and no sample is a guess with tests.
                Say(Format.Dump(responsePin + 1, (int)request.ResponseLength - 1));
            }
        }

        /// <summary>CTAPHID_PING: the round trip, with nothing to interpret.</summary>
        private static void Ping()
        {
            if (!HaveChannel()) return;

            const int Length = 100;      // longer than one frame, so framing is tested too
            byte[] payload = new byte[Length];
            byte[] response = new byte[ResponseCapacity];

            for (int i = 0; i < Length; i++)
                payload[i] = (byte)(i * 7 + 1);

            fixed (byte* payloadPin = payload)
            fixed (byte* responsePin = response)
            {
                AppUsbCtapRequest request = default;
                request.Channel = s_channel;
                request.Command = CmdPing;
                request.PayloadAddress = (ulong)payloadPin;
                request.PayloadLength = Length;
                request.ResponseAddress = (ulong)responsePin;
                request.ResponseCapacity = ResponseCapacity;
                request.TimeoutMs = TimeoutMs;

                AppServiceStatus status = AppHost.TryUsbCtap(ref request);
                if (status != AppServiceStatus.Ok)
                {
                    Say("PING failed: " + Failure(status, ref request));
                    return;
                }

                if (request.ResponseLength != Length)
                {
                    Say("PING came back " + request.ResponseLength.ToString()
                        + " bytes instead of " + Length.ToString());
                    return;
                }

                // A payload longer than 57 bytes goes out in an initial frame
                // plus continuations, so an echo that matches byte for byte
                // checks the sequence numbering in both directions at once.
                for (int i = 0; i < Length; i++)
                {
                    if (responsePin[i] == payload[i]) continue;

                    Say("PING differs at byte " + i.ToString()
                        + ": sent 0x" + Format.Hex2(payload[i])
                        + ", got 0x" + Format.Hex2(responsePin[i]));
                    return;
                }

                Say("PING echoed " + Length.ToString() + " bytes unchanged");
            }
        }

        /// <summary>CTAPHID_WINK: the one command whose result is meant to be visible.</summary>
        private static void Wink()
        {
            if (!HaveChannel()) return;

            byte[] response = new byte[64];

            fixed (byte* responsePin = response)
            {
                AppUsbCtapRequest request = default;
                request.Channel = s_channel;
                request.Command = CmdWink;
                request.ResponseAddress = (ulong)responsePin;
                request.ResponseCapacity = 64;
                request.TimeoutMs = TimeoutMs;

                AppServiceStatus status = AppHost.TryUsbCtap(ref request);
                if (status != AppServiceStatus.Ok)
                {
                    Say("WINK failed: " + Failure(status, ref request));
                    return;
                }

                // The reply's length, because "it answered" and "it did
                // something" are different claims and only the first is proven
                // here. A key may report the wink capability, answer the
                // command and have no indicator to light.
                Say("WINK answered, " + request.ResponseLength.ToString() + " bytes back.");
                Say("If nothing lit up, use Register: that has to ask for a finger.");
            }
        }

        // ---- user presence -----------------------------------------------

        private static object s_touchToken;
        private static int s_touchTicks;

        // The last status word the key answered with, and how many times in a
        // row. Kept because a wait that prints nothing is indistinguishable
        // from a wait that is not happening - which is exactly how this looked
        // the first time it was tried: press Register, press the button,
        // nothing on screen either way.
        private static uint s_lastStatus;
        private static uint s_lastKeepAlives;
        private static byte[] s_apdu = null!;
        private static byte[] s_apduResponse = null!;

        /// <summary>A U2F registration: the shortest path to a demand for a finger.</summary>
        /// <remarks>
        /// Here because a key answered WINK and lit nothing, and WINK cannot
        /// settle that on its own - a device is allowed to acknowledge it
        /// without having an indicator. Registration is different: the standard
        /// requires the key to refuse until a human touches it, and to signal
        /// that it is waiting. So this either blinks or the key has no light.
        ///
        /// A U2F message, not CBOR, deliberately: the APDU is seven bytes of
        /// header around two thirty-two byte blocks, which can be built by hand
        /// today. The CBOR equivalent needs an encoder we have not ported yet.
        ///
        /// Driven from the main loop rather than a wait inside the click, so the
        /// program stays usable while the key waits for its finger - a UI frozen
        /// for a minute is indistinguishable from one that has crashed.
        /// </remarks>
        private static void Register()
        {
            if (s_touchToken != null)
            {
                Application.MainLoop.RemoveTimeout(s_touchToken);
                s_touchToken = null;
                Say("gave up waiting for a touch");
                return;
            }

            Say("Register");
            if (!HaveChannel()) return;

            BuildApdu();
            s_touchTicks = 0;
            s_lastStatus = 0;
            s_lastKeepAlives = 0;
            Say("Touch the key. Register again gives up.");

            s_touchToken = Application.MainLoop.AddTimeout(
                TimeSpan.FromMilliseconds(200), TouchTick);
        }

        private static void BuildApdu()
        {
            const int ApduLength = 4 + 3 + 64 + 2;
            if (s_apdu != null) return;

            s_apdu = new byte[ApduLength];
            s_apduResponse = new byte[ResponseCapacity];

            s_apdu[0] = 0x00;          // CLA
            s_apdu[1] = 0x01;          // INS: register
            s_apdu[2] = 0x03;          // P1: enforce user presence and sign
            s_apdu[3] = 0x00;          // P2
            s_apdu[4] = 0x00;          // Lc, three bytes, extended length
            s_apdu[5] = 0x00;
            s_apdu[6] = 0x40;          // 64 bytes of data

            // A challenge and an application id. Both are hashes as far as the
            // key cares, and nothing here verifies the signature, so their
            // content only has to be fixed rather than meaningful.
            for (int i = 0; i < 32; i++) s_apdu[7 + i] = (byte)(0xA0 + i);
            for (int i = 0; i < 32; i++) s_apdu[39 + i] = (byte)(0x50 + i);

            s_apdu[71] = 0x00;         // Le
            s_apdu[72] = 0x00;
        }

        /// <summary>One registration attempt. False stops the wait.</summary>
        private static bool TouchTick(MainLoop loop)
        {
            // Five minutes of asking is long past the point where the key is
            // going to be touched.
            if (++s_touchTicks > 300)
            {
                s_touchToken = null;
                Say("no touch; stopped asking");
                return false;
            }

            fixed (byte* apduPin = s_apdu)
            fixed (byte* responsePin = s_apduResponse)
            {
                AppUsbCtapRequest request = default;
                request.Channel = s_channel;
                request.Command = CmdMsg;
                request.PayloadAddress = (ulong)apduPin;
                request.PayloadLength = (uint)s_apdu.Length;
                request.ResponseAddress = (ulong)responsePin;
                request.ResponseCapacity = ResponseCapacity;
                request.TimeoutMs = TimeoutMs;

                AppServiceStatus status = AppHost.TryUsbCtap(ref request);
                if (status != AppServiceStatus.Ok)
                {
                    s_touchToken = null;
                    Say("register failed: " + Failure(status, ref request));
                    return false;
                }

                if (request.ResponseLength < 2)
                {
                    s_touchToken = null;
                    Say("register: a reply too short to hold a status word");
                    return false;
                }

                // The status word is the last two bytes of a U2F reply.
                int end = (int)request.ResponseLength;
                uint sw = (uint)((responsePin[end - 2] << 8) | responsePin[end - 1]);

                // Every answer is news the first time, and a changed answer is
                // news again. A key that has been saying "not yet" for ten
                // seconds and one that is not being asked at all look the same
                // on screen otherwise.
                if (sw != s_lastStatus || request.KeepAlives != s_lastKeepAlives)
                {
                    s_lastStatus = sw;
                    s_lastKeepAlives = request.KeepAlives;
                    Say("  answer 0x" + Format.Hex4((ushort)sw)
                        + Explain(sw)
                        + (request.KeepAlives != 0
                            ? ", " + request.KeepAlives.ToString() + " keepalives"
                            : ""));
                }

                // A second a tick, so the wait has a pulse without filling the
                // log with it.
                if ((s_touchTicks % 5) == 0)
                {
                    s_state.Text = "waiting for a touch, " + (s_touchTicks / 5).ToString()
                                 + "s, last answer 0x" + Format.Hex4((ushort)s_lastStatus);
                    s_state.SetNeedsDisplay();
                }

                // Waiting for the finger. The key is signalling now, so this is
                // the moment its light should be on.
                if (sw == 0x6985) return true;

                s_touchToken = null;

                if (sw == 0x9000)
                {
                    Say("registered: " + (request.ResponseLength - 2).ToString()
                        + " bytes of key handle and attestation");
                    Say(Format.Dump(responsePin, end - 2));
                    return false;
                }

                Say("register: status 0x" + Format.Hex4((ushort)sw) + Explain(sw));
                return false;
            }
        }

        // ---- shared ------------------------------------------------------

        /// <summary>The few status words this program can say something about.</summary>
        /// <remarks>
        /// Not a table of the standard: these are the four a registration
        /// actually produces here, and the last one is the reason to have the
        /// list at all. A freshly flashed key has no device seed yet, and the
        /// firmware answers that with a plain execution error - which reads as
        /// a bug in the host until somebody says out loud that the key is
        /// simply not provisioned.
        /// </remarks>
        private static string Explain(uint sw)
        {
            switch (sw)
            {
                case 0x9000: return " (done)";
                case 0x6985: return " (not yet - waiting for a touch)";
                case 0x6700: return " (wrong length: the request is malformed)";
                case 0x6A86: return " (bad P1/P2)";
                case 0x6F00: return " (execution error - an unprovisioned key answers this)";
                default: return "";
            }
        }

        private static bool HaveChannel()
        {
            if (s_channel != 0) return true;

            // Introducing ourselves is free and has no side effect on the key,
            // so doing it here beats telling the operator to press INIT first.
            Introduce();
            return s_channel != 0;
        }

        private static string Failure(AppServiceStatus status, ref AppUsbCtapRequest request)
        {
            string what;
            switch (status)
            {
                case AppServiceStatus.NotFound: what = "no key on the bus"; break;
                case AppServiceStatus.Unsupported: what = "this kernel has no CTAP service"; break;
                case AppServiceStatus.DeviceError: what = "the key did not answer"; break;
                default: what = Format.Status(status); break;
            }

            // The key's own error byte, when it sent one. A transport that gave
            // up and a key that said no look the same from the status alone.
            if (request.Error != 0)
                what += ", CTAPHID error 0x" + Format.Hex2((byte)request.Error);

            return what;
        }
    }
}
