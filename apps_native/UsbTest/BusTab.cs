using System.Collections.Generic;
using Terminal.Gui;
using Terminal.Gui.Trees;

namespace UsbTest
{
    // The navigator: what is on the bus, and everything known about one node.
    //
    // A tree rather than a printed listing because the interesting question is
    // usually about one device, and a listing of every endpoint on every
    // interface buries it. Controllers come expanded, so the shape of the
    // machine is visible without a keystroke.
    internal static class BusTab
    {
        private static TreeView<UsbNode> s_tree = null!;
        private static TextView s_details = null!;
        private static FrameView s_frame = null!;
        private static Label s_note = null!;

        public static View Build()
        {
            var page = new View
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
            };

            s_frame = new FrameView("Bus")
            {
                X = 0,
                Y = 0,
                Width = Dim.Percent(58),
                Height = Dim.Fill(1),
            };

            // The whole tree is already in memory after a refresh, so children
            // come from the node rather than from a fetch.
            s_tree = new TreeView<UsbNode>(new DelegateTreeBuilder<UsbNode>(
                node => node.Children,
                node => node.Children.Count > 0))
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
            };

            s_tree.SelectionChanged += (object sender, SelectionChangedEventArgs<UsbNode> e) => ShowSelection();

            s_frame.Add(s_tree);

            var details = new FrameView("Selected")
            {
                X = Pos.Percent(58),
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(1),
            };

            s_details = new TextView
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                ReadOnly = true,
            };

            details.Add(s_details);

            // A line for what the reading itself had to say: a truncated
            // listing and a complete one look the same in a tree.
            s_note = new Label("")
            {
                X = 0,
                Y = Pos.AnchorEnd(1),
                Width = Dim.Fill(),
            };

            page.Add(s_frame, details, s_note);
            return page;
        }

        /// <summary>Reads the bus again and repopulates the tree.</summary>
        public static void Refresh()
        {
            List<UsbNode> roots = UsbSnapshot.Take(out string note);

            s_tree.ClearObjects();
            s_tree.AddObjects(roots);

            // Controllers open, devices closed: one level down is the shape of
            // the machine, two is a wall of endpoints.
            for (int i = 0; i < roots.Count; i++)
                s_tree.Expand(roots[i]);

            Shell.SetKeyPresence(UsbSnapshot.FidoInterfaces(roots).Count);

            s_frame.Title = roots.Count == 0
                ? "Bus (nothing)"
                : "Bus (" + roots.Count.ToString() + " controllers)";

            s_note.Text = note;
            ShowSelection();

            s_tree.SetNeedsDisplay();
            s_frame.SetNeedsDisplay();
        }

        private static void ShowSelection()
        {
            UsbNode node = s_tree.SelectedObject;
            s_details.Text = node == null ? "" : Describe(node);
            s_details.SetNeedsDisplay();
        }

        // Everything the service knows about one node, a field per line.
        //
        // Spelled out rather than abbreviated as in the tree row: the row has to
        // fit a column, this does not, and the two together are what makes a
        // listing answer questions instead of raising them.
        private static string Describe(UsbNode node)
        {
            var text = new System.Text.StringBuilder();

            switch (node.Kind)
            {
                case UsbKind.Controller:
                    Line(text, "kind", "xHCI controller");
                    Line(text, "index", node.Number.ToString());
                    Line(text, "pci", Format.Hex4(node.Vendor) + ":" + Format.Hex4(node.Product));
                    Line(text, "devices", node.Children.Count.ToString());
                    break;

                case UsbKind.Device:
                    Line(text, "kind", "device");
                    Line(text, "slot", node.Number.ToString());

                    if (node.Unnamed)
                    {
                        // Unknown, not zero. The distinction matters because
                        // class zero means "see the interfaces".
                        Line(text, "identity", "unread - the device descriptor did not come back");
                    }
                    else
                    {
                        Line(text, "vid:pid", Format.Hex4(node.Vendor) + ":" + Format.Hex4(node.Product));
                        Line(text, "class", Format.Class(node.Class, node.Subclass, node.Protocol));
                    }

                    Line(text, "speed", Format.Speed(node.Speed));
                    Line(text, "product", node.ProductName.Length != 0 ? node.ProductName : "(unnamed)");
                    Line(text, "maker", node.ManufacturerName.Length != 0 ? node.ManufacturerName : "(unnamed)");
                    Line(text, "serial", node.SerialNumber.Length != 0 ? node.SerialNumber : "(none)");
                    Line(text, "configured", node.Configured ? "yes" : "no");
                    Line(text, "interfaces", node.Children.Count.ToString());
                    break;

                case UsbKind.Interface:
                    Line(text, "kind", "interface");
                    Line(text, "number", node.Number.ToString());
                    Line(text, "class", Format.Class(node.Class, node.Subclass, node.Protocol));

                    if (node.UsagePage != 0 || node.Usage != 0)
                    {
                        Line(text, "usage", Format.Hex4(node.UsagePage) + ":" + Format.Hex4(node.Usage));
                        if (node.UsagePage == UsbSnapshot.FidoUsagePage
                            && node.Usage == UsbSnapshot.FidoUsage)
                            Line(text, "", "This is a FIDO authenticator. The key tab talks to it.");
                    }
                    else if (node.Class == 0x03)
                    {
                        // Zero is honest here: nobody asked. A report
                        // descriptor is only read for a HID we drive.
                        Line(text, "usage", "not read");
                    }

                    Line(text, "driver", node.Claimed ? "one of ours" : "none");
                    Line(text, "endpoints", node.Children.Count.ToString());
                    break;

                default:
                    Line(text, "kind", "endpoint");
                    Line(text, "address", "0x" + Format.Hex2((byte)node.Number));
                    Line(text, "direction", (node.Number & 0x80) != 0 ? "in (device to host)" : "out (host to device)");
                    Line(text, "type", Format.EndpointType(node.EndpointType));
                    Line(text, "max packet", node.MaxPacket.ToString());
                    Line(text, "interval", node.Interval == 0 ? "(none)" : node.Interval.ToString());
                    break;
            }

            return text.ToString();
        }

        private static void Line(System.Text.StringBuilder text, string name, string value)
        {
            if (name.Length != 0)
            {
                text.Append(name);
                text.Append(':');
                for (int i = name.Length + 1; i < 13; i++) text.Append(' ');
            }

            text.Append(value);
            text.Append('\n');
        }
    }
}
