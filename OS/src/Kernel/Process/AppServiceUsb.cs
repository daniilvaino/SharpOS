namespace OS.Kernel.Process
{
    using OS.Hal.Usb;

    // The USB bus, handed to an application.
    //
    // Until now everything the stack knew about the bus was printed once at
    // boot and then gone. That was enough while the questions were "did the
    // disk come up" and "how many controllers are there"; it stopped being
    // enough the moment a security key answered with 548 bytes of CBOR, which
    // no boot log is a reasonable place to read.
    //
    // Two services, not twenty. Enumerate flattens the whole tree into one
    // array in a single call, and Ctap runs one exchange. Both take a request
    // structure by address, like every other service here, so the ABI stays a
    // table of one-argument entry points.
    internal static unsafe partial class AppServiceBuilder
    {
        private const uint NodeController = 0;
        private const uint NodeDevice = 1;
        private const uint NodeInterface = 2;
        private const uint NodeEndpoint = 3;
        private const uint NoParent = 0xFFFFFFFF;

        /// <summary>
        /// Walk every controller and write the tree into the caller's array.
        /// </summary>
        private static uint UsbEnumerate(ulong requestAddress)
        {
            if (requestAddress == 0) return (uint)AppServiceStatus.InvalidParameter;

            AppUsbEnumerateRequest* request = (AppUsbEnumerateRequest*)requestAddress;
            request->Count = 0;
            request->Status = (uint)AppServiceStatus.Ok;

            if (request->BufferAddress == 0 || request->Capacity == 0)
            {
                request->Status = (uint)AppServiceStatus.InvalidParameter;
                return request->Status;
            }

            AppUsbNode* nodes = (AppUsbNode*)request->BufferAddress;
            uint capacity = request->Capacity;
            uint count = 0;

            for (int c = 0; c < Xhci.Count; c++)
            {
                XhciController hc = Xhci.Get(c);
                if (hc == null) continue;

                if (count >= capacity) break;
                uint controllerIndex = count;
                nodes[count] = default;
                nodes[count].Kind = NodeController;
                nodes[count].Parent = NoParent;
                nodes[count].Number = (uint)c;
                nodes[count].Vendor = hc.VendorId;
                nodes[count].Product = hc.DeviceId;
                count++;

                for (int d = 0; d < hc.DeviceCount; d++)
                {
                    uint slot = hc.SlotIdAt(d);
                    if (slot == 0) continue;

                    if (count >= capacity) break;
                    uint deviceIndex = count;
                    nodes[count] = default;
                    nodes[count].Kind = NodeDevice;
                    nodes[count].Parent = controllerIndex;
                    nodes[count].Number = slot;
                    nodes[count].Flags = hc.IsConfigured(slot) ? 1u : 0u;
                    nodes[count].Speed = hc.SpeedOf(slot);

                    // Who the device says it is. Fetched here, because the
                    // stack keeps none of it: enumeration reads the device
                    // descriptor, drives whatever it recognises and lets the
                    // eighteen bytes go.
                    //
                    // Left unfetched, these fields stay zero and a listing
                    // prints 0000:0000 at speed zero with class zero — and
                    // class zero has a meaning ("see the interfaces"), so the
                    // output would not look empty, it would look wrong. A
                    // control transfer on a configured device is the cheapest
                    // request there is, and it happens when someone asks for
                    // the listing, not at boot.
                    byte* device = stackalloc byte[18];
                    if (hc.TryFetchDeviceDescriptor(slot, device, 18, out int deviceLength)
                        && deviceLength >= 12)
                    {
                        nodes[count].Class = device[4];
                        nodes[count].Subclass = device[5];
                        nodes[count].Protocol = device[6];
                        nodes[count].Vendor = (ushort)(device[8] | (device[9] << 8));
                        nodes[count].Product = (ushort)(device[10] | (device[11] << 8));

                        // The three strings the descriptor points at. The
                        // language has to be asked for first: a string request
                        // carries a language id, and devices are within their
                        // rights to refuse one they do not offer.
                        //
                        // Safe to attempt on a device we are driving: a refusal
                        // halts the control endpoint, and clearing that halt is
                        // already part of the control path (XhciRecovery), so a
                        // device that declines a name is left exactly as it was.
                        if (deviceLength >= 17)
                        {
                            ushort lang = hc.FirstLanguageId(slot);
                            FillName(hc, slot, lang, device[14], nodes[count].ManufacturerName);
                            FillName(hc, slot, lang, device[15], nodes[count].ProductName);
                            FillName(hc, slot, lang, device[16], nodes[count].SerialNumber);
                        }
                    }
                    else
                    {
                        // Bit 1 says the descriptor did not come back, so a
                        // reader can tell "this device has no class" from "we
                        // failed to ask".
                        nodes[count].Flags |= 2;
                    }

                    count++;

                    // The configuration descriptor, walked here rather than
                    // remembered: the stack keeps only what it drives, and a
                    // device manager has to show what it declined too.
                    byte* descriptor = stackalloc byte[512];
                    if (!hc.TryFetchConfigDescriptor(slot, descriptor, 512, out int length))
                        continue;

                    uint interfaceIndex = NoParent;
                    int offset = 0;
                    while (offset + 2 <= length && count < capacity)
                    {
                        byte recordLength = descriptor[offset];
                        byte type = descriptor[offset + 1];
                        if (recordLength == 0) break;

                        if (type == 4 && recordLength >= 9 && descriptor[offset + 3] != 0)
                        {
                            // An alternate setting. Not listed — and, more to
                            // the point, its endpoints must not be listed
                            // either: leaving the previous interface as their
                            // parent put a Bluetooth adapter's six SCO
                            // settings under one interface as twelve
                            // endpoints, two of them claiming address 0x03
                            // six times over.
                            interfaceIndex = NoParent;
                        }
                        else if (type == 4 && recordLength >= 9)
                        {
                            byte number = descriptor[offset + 2];

                            interfaceIndex = count;
                            nodes[count] = default;
                            nodes[count].Kind = NodeInterface;
                            nodes[count].Parent = deviceIndex;
                            nodes[count].Number = number;
                            nodes[count].Class = descriptor[offset + 5];
                            nodes[count].Subclass = descriptor[offset + 6];
                            nodes[count].Protocol = descriptor[offset + 7];

                            // What the report descriptor said, if this is a HID
                            // the stack claimed. Zero otherwise — and zero is
                            // honest: it means nobody asked, not that the
                            // interface has no usage.
                            int fn = FindHidFunction(hc, slot, number);
                            if (fn >= 0)
                            {
                                nodes[count].UsagePage = hc.HidUsagePageAt(fn);
                                nodes[count].Usage = hc.HidUsageAt(fn);
                            }

                            // Claimed means any of our drivers, not just HID.
                            // Asking the HID table alone reported the disk we
                            // had booted from as nobody's.
                            if (hc.IsInterfaceClaimed(slot, number))
                                nodes[count].Flags = 1;

                            count++;
                        }
                        else if (type == 5 && recordLength >= 7 && interfaceIndex != NoParent)
                        {
                            byte address = descriptor[offset + 2];
                            byte attributes = descriptor[offset + 3];

                            nodes[count] = default;
                            nodes[count].Kind = NodeEndpoint;
                            nodes[count].Parent = interfaceIndex;
                            nodes[count].Number = address;
                            nodes[count].EndpointType = (byte)(attributes & 0x3);
                            nodes[count].MaxPacket =
                                (ushort)(descriptor[offset + 4] | (descriptor[offset + 5] << 8));
                            nodes[count].Interval = descriptor[offset + 6];
                            count++;
                        }

                        offset += recordLength;
                    }
                }
            }

            request->Count = count;
            return (uint)AppServiceStatus.Ok;
        }

        /// <summary>One string descriptor, folded into ASCII for the caller.</summary>
        /// <remarks>
        /// Index zero means the device declared no string for that field, which
        /// is not a failure and not worth a transfer. Anything outside printable
        /// ASCII becomes a question mark rather than being dropped, so a name
        /// that did not survive the fold still shows its own length.
        /// </remarks>
        private static void FillName(XhciController hc, uint slot, ushort lang,
                                     byte index, byte* destination)
        {
            if (index == 0 || lang == 0) return;

            byte* raw = stackalloc byte[256];
            if (!hc.TryFetchStringDescriptor(slot, index, lang, raw, 256, out int length))
                return;
            if (length < 4) return;                 // header only: an empty string

            int characters = (length - 2) / 2;
            int written = 0;
            for (int i = 0; i < characters && written < AppUsbNode.NameBytes - 1; i++)
            {
                ushort ch = (ushort)(raw[2 + i * 2] | (raw[3 + i * 2] << 8));
                destination[written++] = (byte)(ch >= 0x20 && ch < 0x7F ? ch : (byte)'?');
            }

            destination[written] = 0;
        }

        // Which HID function record, if any, belongs to this interface number.
        private static int FindHidFunction(XhciController hc, uint slot, byte interfaceNumber)
        {
            int n = hc.HidCountOf(slot);
            for (int i = 0; i < n; i++)
            {
                int fn = hc.HidIndexOf(slot, i);
                if (fn >= 0 && hc.HidInterfaceAt(fn) == interfaceNumber) return fn;
            }
            return -1;
        }

        /// <summary>One CTAPHID exchange, framing and all.</summary>
        private static uint UsbCtap(ulong requestAddress)
        {
            if (requestAddress == 0) return (uint)AppServiceStatus.InvalidParameter;

            AppUsbCtapRequest* request = (AppUsbCtapRequest*)requestAddress;
            request->ResponseLength = 0;
            request->KeepAlives = 0;
            request->Error = 0;

            if (!UsbCtapHid.TryAttach())
            {
                request->Status = (uint)AppServiceStatus.NotFound;
                return request->Status;
            }

            uint timeout = request->TimeoutMs == 0 ? 2000 : request->TimeoutMs;

            // Channel 0 means the caller has not been introduced yet. INIT is
            // the introduction, and it is also the only command that proves the
            // channel without a credential or a finger — so a caller that opens
            // with it learns whether anything further is worth trying.
            if (request->Channel == 0)
            {
                byte* nonce = stackalloc byte[8];
                for (int i = 0; i < 8; i++) nonce[i] = (byte)(0x41 + i);

                if (!UsbCtapHid.TryInit(nonce, out uint channel,
                                        out byte version, out byte capabilities, timeout))
                {
                    request->Status = (uint)AppServiceStatus.DeviceError;
                    return request->Status;
                }

                request->Channel = channel;
                // The two bytes a caller needs before deciding what to ask
                // next, handed back without needing a second call.
                request->Command = (uint)version | ((uint)capabilities << 8);
                request->Status = (uint)AppServiceStatus.Ok;
                return request->Status;
            }

            if (request->ResponseAddress == 0 || request->ResponseCapacity == 0)
            {
                request->Status = (uint)AppServiceStatus.InvalidParameter;
                return request->Status;
            }

            bool ok = UsbCtapHid.TryTransceive(
                request->Channel, (byte)request->Command,
                (byte*)request->PayloadAddress, (int)request->PayloadLength,
                (byte*)request->ResponseAddress, (int)request->ResponseCapacity,
                out int responseLength, timeout);

            request->KeepAlives = UsbCtapHid.LastKeepAlives;
            request->Error = UsbCtapHid.LastError;

            if (!ok)
            {
                request->Status = (uint)AppServiceStatus.DeviceError;
                return request->Status;
            }

            request->ResponseLength = (uint)responseLength;
            request->Status = (uint)AppServiceStatus.Ok;
            return request->Status;
        }
    }
}
