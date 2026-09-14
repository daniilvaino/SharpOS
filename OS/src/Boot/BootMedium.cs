namespace OS.Boot
{
    // Ask the firmware what it booted us from, while it is still alive.
    //
    // The device path of our own loaded image spells out the whole route:
    // which PCI function the controller is, which port the device sits on,
    // which partition the file came from. That is authoritative — the
    // firmware did the enumeration and does not have to guess — and it is far
    // cheaper than driving every controller on the machine to find out.
    //
    // Must run BEFORE ExitBootServices. Afterwards there is nobody to ask,
    // which is the whole reason the answer is captured rather than queried.
    internal static unsafe class BootMedium
    {
        private static bool s_captured;
        private static bool s_valid;
        private static bool s_isUsb;
        private static bool s_isSata;
        private static bool s_isNvme;
        private static ushort s_sataPort;
        private static byte s_pciDevice, s_pciFunction;
        private static byte s_usbPort, s_usbInterface;
        private static int s_pciNodes;

        // Raw node types, kept so an unexpected path can be read off a photo
        // of the screen. A machine that boots from something we do not decode
        // is indistinguishable from a firmware that refused to answer, unless
        // the nodes themselves are visible.
        private const int MaxNodeTrace = 12;
        private static readonly byte[] s_nodeTypes = new byte[MaxNodeTrace];
        private static readonly byte[] s_nodeSubTypes = new byte[MaxNodeTrace];
        private static int s_nodeCount;

        // Every PCI node on the way, not just the last: the device path names
        // device/function per node but never the bus, so the chain of bridges
        // is the only way to tell two identically-numbered functions apart.
        private const int MaxPciNodes = 6;
        private static readonly byte[] s_pciChainDev = new byte[MaxPciNodes];
        private static readonly byte[] s_pciChainFunc = new byte[MaxPciNodes];

        public static int PciChainLength => s_pciNodes < MaxPciNodes ? s_pciNodes : MaxPciNodes;
        public static byte PciChainDevice(int i) => s_pciChainDev[i];
        public static byte PciChainFunction(int i) => s_pciChainFunc[i];

        public static bool Valid => s_valid;
        /// <summary>True when the firmware booted us off a USB device.</summary>
        public static bool IsUsb => s_isUsb;
        /// <summary>True when the firmware booted us off a SATA disk.</summary>
        public static bool IsSata => s_isSata;
        /// <summary>True when the firmware booted us off an NVMe disk.</summary>
        public static bool IsNvme => s_isNvme;
        /// <summary>The AHCI port (HBA port number) of the SATA boot disk.</summary>
        public static ushort SataPort => s_sataPort;
        public static byte PciDevice => s_pciDevice;
        public static byte PciFunction => s_pciFunction;
        public static byte UsbPort => s_usbPort;
        public static byte UsbInterface => s_usbInterface;

        public static bool TryCapture(BootInfo context)
        {
            if (s_captured) return s_valid;
            s_captured = true;

            if (OS.Hal.Platform.BootServicesGone) return false;

            EFI_SYSTEM_TABLE* systemTable = context.SystemTable;
            if (systemTable == null || systemTable->BootServices == null) return false;

            EFI_BOOT_SERVICES* bootServices = systemTable->BootServices;
            if (bootServices->HandleProtocol == null) return false;

            EFI_GUID loadedImageGuid = LoadedImageGuid();
            EFI_LOADED_IMAGE_PROTOCOL* loadedImage = null;
            if (bootServices->HandleProtocol(context.ImageHandle, &loadedImageGuid,
                                             (void**)&loadedImage) != 0 || loadedImage == null)
                return false;

            EFI_GUID devicePathGuid = DevicePathGuid();
            byte* node = null;
            if (bootServices->HandleProtocol(loadedImage->DeviceHandle, &devicePathGuid,
                                             (void**)&node) != 0 || node == null)
                return false;

            Walk(node);
            s_valid = true;
            return true;
        }

        // A device path is a packed list of type/subtype/length records ending
        // with an end-of-path node. Only the two kinds that answer our
        // question are decoded; the rest are stepped over by their length.
        private static void Walk(byte* node)
        {
            for (int guard = 0; guard < 64; guard++)
            {
                byte type = node[0];
                byte subType = node[1];
                ushort length = (ushort)(node[2] | (node[3] << 8));
                if (length < 4) return;                 // malformed: would not advance
                if (type == 0x7F) return;               // end of path

                if (s_nodeCount < MaxNodeTrace)
                {
                    s_nodeTypes[s_nodeCount] = type;
                    s_nodeSubTypes[s_nodeCount] = subType;
                    s_nodeCount++;
                }

                if (type == 0x01 && subType == 0x01 && length >= 6)
                {
                    // Hardware/PCI. The last one before the device is the
                    // function we want; earlier ones are bridges on the way.
                    s_pciFunction = node[4];
                    s_pciDevice = node[5];
                    if (s_pciNodes < MaxPciNodes)
                    {
                        s_pciChainFunc[s_pciNodes] = node[4];
                        s_pciChainDev[s_pciNodes] = node[5];
                    }
                    s_pciNodes++;
                }
                else if (type == 0x03 && subType == 0x05 && length >= 6)
                {
                    // Messaging/USB.
                    s_usbPort = node[4];
                    s_usbInterface = node[5];
                    s_isUsb = true;
                }
                else if (type == 0x03 && subType == 0x12 && length >= 6)
                {
                    // Messaging/SATA: HBA port, port-multiplier port, LUN.
                    s_sataPort = (ushort)(node[4] | (node[5] << 8));
                    s_isSata = true;
                }
                else if (type == 0x03 && subType == 0x17)
                {
                    s_isNvme = true;                    // Messaging/NVMe namespace
                }

                node += length;
            }
        }

        public static void Report()
        {
            if (!s_valid)
            {
                OS.Hal.Console.WriteLine("[bootmedium] unavailable (firmware did not answer)");
                return;
            }

            OS.Hal.Console.Write("[bootmedium] ");
            OS.Hal.Console.Write(s_isUsb ? "usb" : s_isSata ? "sata" : s_isNvme ? "nvme" : "not-usb");
            OS.Hal.Console.Write(" pci=");
            OS.Hal.Console.WriteUInt(s_pciDevice);
            OS.Hal.Console.Write(".");
            OS.Hal.Console.WriteUInt(s_pciFunction);
            OS.Hal.Console.Write(" (nodes=");
            OS.Hal.Console.WriteUInt((uint)s_pciNodes);
            OS.Hal.Console.Write(")");
            if (s_isSata)
            {
                OS.Hal.Console.Write(" sata-port=");
                OS.Hal.Console.WriteUInt(s_sataPort);
            }
            if (s_isUsb)
            {
                OS.Hal.Console.Write(" port=");
                OS.Hal.Console.WriteUInt(s_usbPort);
                OS.Hal.Console.Write(" iface=");
                OS.Hal.Console.WriteUInt(s_usbInterface);
            }
            OS.Hal.Console.WriteLine("");

            // The decoded fields above are an interpretation; these are the
            // raw records the firmware handed over. When the interpretation is
            // wrong this is the line that says so.
            OS.Hal.Console.Write("[bootmedium] path:");
            for (int i = 0; i < s_nodeCount; i++)
            {
                OS.Hal.Console.Write(" ");
                OS.Hal.Console.WriteHex(s_nodeTypes[i]);
                OS.Hal.Console.Write("/");
                OS.Hal.Console.WriteHex(s_nodeSubTypes[i]);
            }
            OS.Hal.Console.Write("  pci-chain:");
            for (int i = 0; i < PciChainLength; i++)
            {
                OS.Hal.Console.Write(" ");
                OS.Hal.Console.WriteUInt(s_pciChainDev[i]);
                OS.Hal.Console.Write(".");
                OS.Hal.Console.WriteUInt(s_pciChainFunc[i]);
            }
            OS.Hal.Console.WriteLine("");
        }

        private static EFI_GUID LoadedImageGuid()
        {
            EFI_GUID guid = default;
            guid.Data1 = 0x5B1B31A1; guid.Data2 = 0x9562; guid.Data3 = 0x11D2;
            guid.Data4_0 = 0x8E; guid.Data4_1 = 0x3F; guid.Data4_2 = 0x00;
            guid.Data4_3 = 0xA0; guid.Data4_4 = 0xC9; guid.Data4_5 = 0x69;
            guid.Data4_6 = 0x72; guid.Data4_7 = 0x3B;
            return guid;
        }

        private static EFI_GUID DevicePathGuid()
        {
            EFI_GUID guid = default;
            guid.Data1 = 0x09576E91; guid.Data2 = 0x6D3F; guid.Data3 = 0x11D2;
            guid.Data4_0 = 0x8E; guid.Data4_1 = 0x39; guid.Data4_2 = 0x00;
            guid.Data4_3 = 0xA0; guid.Data4_4 = 0xC9; guid.Data4_5 = 0x69;
            guid.Data4_6 = 0x72; guid.Data4_7 = 0x3B;
            return guid;
        }
    }
}
