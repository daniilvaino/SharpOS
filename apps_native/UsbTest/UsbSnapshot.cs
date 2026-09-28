using SharpOS.AppSdk;
using System.Collections.Generic;

namespace UsbTest
{
    internal enum UsbKind
    {
        Controller = 0,
        Device = 1,
        Interface = 2,
        Endpoint = 3,
    }

    // One node of the bus, as a managed object.
    //
    // The service hands back a flat array with parent indices, which is the
    // right shape to cross a service boundary and the wrong one to hand a tree
    // view. Rebuilt here once per refresh, so the views work with children
    // rather than with indices.
    internal sealed class UsbNode
    {
        public UsbKind Kind;
        public uint Number;
        public uint Class;
        public uint Subclass;
        public uint Protocol;
        public ushort Vendor;
        public ushort Product;
        public uint Speed;
        public ushort UsagePage;
        public ushort Usage;
        public ushort MaxPacket;
        public byte EndpointType;
        public byte Interval;
        public uint Flags;

        public string ManufacturerName = "";
        public string ProductName = "";
        public string SerialNumber = "";

        public List<UsbNode> Children = new List<UsbNode>();

        /// <summary>Bit 0 on a device. Means something else on an interface.</summary>
        public bool Configured => (Flags & 1) != 0;

        /// <summary>Bit 0 on an interface: one of our drivers took it.</summary>
        public bool Claimed => (Flags & 1) != 0;

        /// <summary>Bit 1 on a device: we could not read its descriptor.</summary>
        public bool Unnamed => (Flags & 2) != 0;

        /// <summary>The row in the tree. TreeView renders nodes by this.</summary>
        public override string ToString()
        {
            switch (Kind)
            {
                case UsbKind.Controller:
                    return "controller " + Number.ToString()
                         + "  pci " + Format.Hex4(Vendor) + ":" + Format.Hex4(Product);

                case UsbKind.Device:
                    return DeviceRow();

                case UsbKind.Interface:
                    return InterfaceRow();

                default:
                    return EndpointRow();
            }
        }

        private string DeviceRow()
        {
            // The name first when there is one: it is what a person is looking
            // for, and the numbers are what they fall back to.
            string head = ProductName.Length != 0 ? ProductName : "device slot " + Number.ToString();

            string identity = Unnamed
                ? "????:????"
                : Format.Hex4(Vendor) + ":" + Format.Hex4(Product);

            string row = head + "  " + identity + "  " + Format.Speed(Speed);

            if (ManufacturerName.Length != 0) row += "  (" + ManufacturerName + ")";
            if (!Configured) row += "  [not configured]";
            if (Unnamed) row += "  [descriptor unread]";

            return row;
        }

        private string InterfaceRow()
        {
            string row = "interface " + Number.ToString()
                       + "  " + Format.Class(Class, Subclass, Protocol);

            if (UsagePage != 0 || Usage != 0)
                row += "  usage " + Format.Hex4(UsagePage) + ":" + Format.Hex4(Usage);

            // Free is the normal state for most interfaces, and the only way to
            // tell "we have no driver for this" from "our driver refused it".
            return row + (Claimed ? "  [claimed]" : "  [free]");
        }

        private string EndpointRow()
        {
            string row = "endpoint 0x" + Format.Hex2((byte)Number)
                       + " " + ((Number & 0x80) != 0 ? "in " : "out")
                       + "  " + Format.EndpointType(EndpointType)
                       + "  max " + MaxPacket.ToString();

            if (Interval != 0) row += "  interval " + Interval.ToString();
            return row;
        }
    }

    // Reading the bus into that shape.
    internal static unsafe class UsbSnapshot
    {
        /// <summary>Room for more than any machine here has, and no more.</summary>
        private const int MaxNodes = 192;

        // The usage a FIDO authenticator's HID interface declares.
        public const ushort FidoUsagePage = 0xF1D0;
        public const ushort FidoUsage = 0x0001;

        /// <summary>The controllers, each with its devices beneath it.</summary>
        /// <param name="note">What to tell the operator, empty when all is well.</param>
        public static List<UsbNode> Take(out string note)
        {
            var roots = new List<UsbNode>();
            note = "";

            if (!AppHost.HasUsbServices)
            {
                note = "This kernel does not hand over the USB bus.";
                return roots;
            }

            AppUsbNode[] flat = new AppUsbNode[MaxNodes];

            fixed (AppUsbNode* buffer = flat)
            {
                AppServiceStatus status = AppHost.TryUsbEnumerate(buffer, MaxNodes, out uint count);
                if (status != AppServiceStatus.Ok)
                {
                    note = "The bus could not be read: " + Format.Status(status);
                    return roots;
                }

                if (count == 0)
                {
                    note = "No controllers: either there is no xHCI here, or the kernel never took it over.";
                    return roots;
                }

                // Index in the flat array to the object built from it, so a
                // node's parent can be found by the index it names.
                var built = new UsbNode[count];

                for (uint i = 0; i < count; i++)
                {
                    UsbNode node = Convert(buffer + i);
                    built[i] = node;

                    uint parent = buffer[i].Parent;
                    if (parent < i && built[parent] != null)
                        built[parent].Children.Add(node);
                    else
                        roots.Add(node);
                }

                if (count == MaxNodes)
                    note = "The listing filled its buffer; there may be more than is shown.";
            }

            return roots;
        }

        /// <summary>The FIDO interfaces on the bus, if any.</summary>
        public static List<UsbNode> FidoInterfaces(List<UsbNode> roots)
        {
            var found = new List<UsbNode>();
            Collect(roots, found);
            return found;
        }

        private static void Collect(List<UsbNode> nodes, List<UsbNode> found)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                UsbNode node = nodes[i];
                if (node.Kind == UsbKind.Interface
                    && node.UsagePage == FidoUsagePage && node.Usage == FidoUsage)
                    found.Add(node);

                Collect(node.Children, found);
            }
        }

        private static UsbNode Convert(AppUsbNode* source)
        {
            var node = new UsbNode
            {
                Kind = (UsbKind)source->Kind,
                Number = source->Number,
                Class = source->Class,
                Subclass = source->Subclass,
                Protocol = source->Protocol,
                Vendor = source->Vendor,
                Product = source->Product,
                Speed = source->Speed,
                UsagePage = source->UsagePage,
                Usage = source->Usage,
                MaxPacket = source->MaxPacket,
                EndpointType = source->EndpointType,
                Interval = source->Interval,
                Flags = source->Flags,
            };

            node.ManufacturerName = Text(source->ManufacturerName);
            node.ProductName = Text(source->ProductName);
            node.SerialNumber = Text(source->SerialNumber);
            return node;
        }

        /// <summary>A NUL-terminated ASCII field as a string.</summary>
        private static string Text(byte* field)
        {
            char[] chars = new char[AppUsbNode.NameBytes];

            int n = 0;
            while (n < AppUsbNode.NameBytes && field[n] != 0)
            {
                chars[n] = (char)field[n];
                n++;
            }

            return n == 0 ? "" : new string(chars, 0, n);
        }
    }
}
