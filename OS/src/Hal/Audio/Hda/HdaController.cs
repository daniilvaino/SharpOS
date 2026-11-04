// Ported from SerenityOS Kernel/Devices/Audio/IntelHDA/Controller.{h,cpp}
// (commit e5966bd, BSD-2-Clause, (c) 2023 Jelle Raaijmakers; see
// LICENSE-SerenityOS.txt). Cuts / changes (step247):
//  - no interrupt (INTCTL stays 0): the stream is fed by a kernel thread over
//    the link position, as Ac97 is — ours are MSI and there is no INTx route;
//  - the controller version check accepts any 1.x; a 32-bit-only controller
//    is driven too (our DMA pages are identity-mapped and checked below 4 GiB);
//  - ErrorOr -> bool with Missing naming the step; Vector/HashMap -> List and
//    arrays; AudioChannel/AudioManagement -> OS.Kernel.Audio.AudioOut;
//  - the output route search is upstream's breadth-first walk from each
//    physically connected line-out/speaker/headphone pin to a converter.

using System.Collections.Generic;
using OS.Kernel.Audio;

namespace OS.Hal.Audio.Hda
{
    internal sealed unsafe class HdaController
    {
        // 3.3: controller registers.
        private const ulong GlobalCapabilities = 0x00, VersionMinor = 0x02, VersionMajor = 0x03, GlobalControl = 0x08,
                            StateChangeStatus = 0x0e, InterruptControl = 0x20, CorbOffset = 0x40, RirbOffset = 0x50,
                            StreamsOffset = 0x80;
        private const uint ControllerReset = 1u << 0, AcceptUnsolicited = 1u << 8;   // 3.3.7

        private static HdaController s_instance;
        public static bool Present => s_instance != null;
        public static string Missing { get; private set; } = "";
        public static string Summary { get; private set; } = "";
        /// <summary>The codec playing, vendor:device; "" without one.</summary>
        public static string Codec { get; private set; } = "";

        private HdaRegisters _registers;
        private byte _inputStreams, _outputStreams;
        private CommandOutboundRingBuffer _corb;
        private ResponseInboundRingBuffer _rirb;
        private readonly List<HdaCodec> _codecs = new List<HdaCodec>();
        private HdaOutputPath _path;

        /// <summary>One line per controller tried, for the boot log: which, and what came of it.</summary>
        public static readonly List<string> Report = new List<string>();

        // Every HD Audio controller, the first with an output path wins. A
        // Ryzen laptop has two: the GPU's, whose codec has only HDMI/DP pins
        // (digital, not taken), and the analog one with the speakers — and the
        // GPU's came first by PCI order (step250, rig: "no output path").
        public static bool Start()
        {
            Pci.Init();
            HdaController chosen = null;
            for (int i = 0; i < Pci.Count && chosen == null; i++)
            {
                Pci.PciDev dev = Pci.Get(i);
                if (dev.ClassID != 0x04 || dev.SubClassID != 0x03) continue;
                var controller = new HdaController();
                bool ok = controller.Initialize(dev, out string why);
                Report.Add(dev.Bus.ToString() + ":" + dev.Slot.ToString() + "." + dev.Func.ToString()
                         + " " + Hex(dev.VendorID) + ":" + Hex(dev.DeviceID)
                         + " " + controller.DescribeCodecs()
                         + (ok ? " -> path " + controller._path.Describe() : " -> " + why));
                if (ok) chosen = controller; else Missing = why;
            }
            if (chosen == null)
            {
                if (Report.Count == 0) Missing = "no device";
                return false;
            }
            s_instance = chosen;
            HdaCodec playing = chosen._path.Output.Codec;
            Codec = Hex(playing.Root.VendorId) + ":" + Hex(playing.Root.DeviceId);
            Summary = "codec " + Codec + " path " + chosen._path.Describe()
                    + " " + chosen._path.Stream.CurrentFormat.SampleRate.ToString() + " Hz";
            AudioOut.Attach((int)chosen._path.Stream.CurrentFormat.SampleRate);
            chosen._path.Stream.Start();
            OS.Kernel.Threading.Scheduler.Spawn(&FeedEntry, 16 * 1024);
            return true;
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void FeedEntry()
        {
            while (true)
            {
                s_instance._path.Stream.Feed();
                OS.Kernel.Threading.Scheduler.Sleep(5);
            }
        }

        // Codecs as "vendor:device(pins)", the pins being the output-capable
        // pin complexes as default device / connectivity — what decides
        // whether a path is found.
        private string DescribeCodecs()
        {
            if (_codecs.Count == 0) return "no codecs";
            string s = "";
            foreach (HdaCodec codec in _codecs)
            {
                string pins = "";
                foreach (HdaWidget w in codec.Widgets())
                {
                    if (w.WidgetType != WidgetType.PinComplex || !w.PinOutputSupported) continue;
                    pins += (pins.Length == 0 ? "" : ",") + w.NodeId.ToString() + "=" + ((int)w.DefaultDevice).ToString("x")
                          + "/" + ((int)w.PortConnectivity).ToString();
                }
                s += (s.Length == 0 ? "" : " ") + "codec " + Hex(codec.Root.VendorId) + ":" + Hex(codec.Root.DeviceId) + "(" + pins + ")";
            }
            return s;
        }

        private bool Initialize(Pci.PciDev dev, out string why)
        {
            if ((dev.Bar0 & 1) != 0) { why = "BAR0 not memory"; return false; }
            ulong mmio = dev.Bar0 & ~0xFul;
            if ((dev.Bar0 & 0x6) == 0x4) mmio |= (ulong)dev.Bar1 << 32;    // 64-bit BAR
            if (mmio == 0) { why = "BAR0 unassigned"; return false; }
            if (!OS.Kernel.Memory.VirtualMemory.MapFixed((void*)mmio, mmio, 0x4000, exec: false,
                    OS.Kernel.Memory.VirtualMemory.MemoryKind.Device)) { why = "MMIO map"; return false; }
            Pci.EnableMemoryAndBusMaster(ref dev);
            _registers = new HdaRegisters(mmio);

            // 3.3.3, 3.3.4.
            byte major = _registers.Read8(VersionMajor), minor = _registers.Read8(VersionMinor);
            if (major != 1) { why = "spec version " + major.ToString() + "." + minor.ToString(); return false; }

            // 3.3.2.
            ushort capabilities = _registers.Read16(GlobalCapabilities);
            _outputStreams = (byte)(capabilities >> 12);
            _inputStreams = (byte)((capabilities >> 8) & 0xf);
            if (_outputStreams == 0) { why = "no output streams"; return false; }

            if (!Reset()) { why = "controller reset"; return false; }
            _registers.Write32(InterruptControl, 0);

            _corb = CommandOutboundRingBuffer.Create(_registers.Offset(CorbOffset));
            _rirb = ResponseInboundRingBuffer.Create(_registers.Offset(RirbOffset));
            if (_corb == null || _rirb == null) { why = "CORB/RIRB"; return false; }
            if (!_corb.RegisterWithController() || !_rirb.RegisterWithController()) { why = "CORB/RIRB start"; return false; }

            // 3.3.9: a bit per codec that announced itself after the reset.
            ushort codecs = _registers.Read16(StateChangeStatus);
            for (byte address = 0; address < 14; address++)
            {
                if ((codecs & (1 << address)) == 0) continue;
                var codec = new HdaCodec(this, address);
                var root = new HdaRootNode(codec);
                if (!root.Initialize()) continue;     // a codec we cannot read is skipped, not fatal
                codec.Root = root;
                _codecs.Add(codec);
            }
            if (_codecs.Count == 0) { why = "no codec answered"; return false; }

            return ConfigureOutputRoute(out why);
        }

        private static string Hex(ushort value) => value.ToString("x4");

        // 3.3.7, 4.3.
        private bool Reset()
        {
            uint control = _registers.Read32(GlobalControl) & ~ControllerReset & ~AcceptUnsolicited;
            _registers.Write32(GlobalControl, control);
            if (!WaitGlobalReset(set: false)) return false;

            HdaTiming.Delay(100);
            _registers.Write32(GlobalControl, _registers.Read32(GlobalControl) | ControllerReset);
            if (!WaitGlobalReset(set: true)) return false;

            // 4.3: at least 521 us (25 frames) for codecs to announce themselves.
            HdaTiming.Delay(HdaTiming.FrameDelayMicroseconds(25));
            return true;
        }

        private bool WaitGlobalReset(bool set)
        {
            ulong deadline = HdaTiming.Deadline(HdaTiming.ControllerTimeoutMicroseconds);
            uint spins = 0;
            while (((_registers.Read32(GlobalControl) & ControllerReset) != 0) != set)
                if (HdaTiming.Expired(deadline, ref spins)) return false;
            return true;
        }

        /// <summary>One verb to one node, waiting for its response (7.3).</summary>
        public bool SendCommand(byte codecAddress, byte nodeId, CodecControlVerb verb, ushort payload, out uint response)
        {
            response = 0;
            uint command = (uint)codecAddress << 28 | (uint)nodeId << 20;
            ushort v = (ushort)verb;
            // 7.3: a 12-bit verb carries an 8-bit payload; a 4-bit one, 16.
            if ((v & 0xf00) != 0)
                command |= ((uint)(v & 0xfff) << 8) | (uint)(payload & 0xff);
            else
                command |= ((uint)(v & 0xf) << 16) | payload;

            if (!_corb.WriteValue(command)) return false;

            ulong deadline = HdaTiming.Deadline(HdaTiming.ControllerTimeoutMicroseconds);
            uint spins = 0;
            ulong full;
            while (!_rirb.TryReadValue(out full))
                if (HdaTiming.Expired(deadline, ref spins)) return false;
            response = (uint)(full & 0xffffffffu);
            return true;
        }

        private bool ConfigureOutputRoute(out string why)
        {
            foreach (HdaCodec codec in _codecs)
            {
                // Output pins with a physical connection, wired to a line out,
                // a speaker or headphones.
                foreach (HdaWidget pin in codec.Widgets())
                {
                    if (pin.WidgetType != WidgetType.PinComplex || !pin.PinOutputSupported) continue;
                    bool connected = pin.PortConnectivity == PinPortConnectivity.Jack
                                  || pin.PortConnectivity == PinPortConnectivity.FixedFunction
                                  || pin.PortConnectivity == PinPortConnectivity.JackAndFixedFunction;
                    bool output = pin.DefaultDevice == PinDefaultDevice.LineOut
                               || pin.DefaultDevice == PinDefaultDevice.Speaker
                               || pin.DefaultDevice == PinDefaultDevice.HPOut;
                    if (!connected || !output) continue;

                    List<HdaWidget> path = FindPathToConverter(codec, pin);
                    if (path == null) continue;

                    // Output stream 0 of the controller, numbered 1 on the link.
                    var stream = new HdaOutputStream(_registers.Offset(StreamsOffset + (ulong)_inputStreams * 0x20), 1);
                    var route = new HdaOutputPath(path, stream);
                    if (!route.Activate()) { why = "path " + route.Describe() + ": " + route.Failure; return false; }
                    _path = route;
                    why = "";
                    return true;
                }
            }
            why = "no output path";
            return false;
        }

        // Breadth-first from the pin to the nearest audio output converter;
        // the path is returned pin first.
        private static List<HdaWidget> FindPathToConverter(HdaCodec codec, HdaWidget pin)
        {
            var queue = new List<HdaWidget> { pin };
            var parents = new Dictionary<byte, HdaWidget>();
            var visited = new HashSet<byte>();
            int head = 0;
            while (head < queue.Count)
            {
                HdaWidget current = queue[head++];
                if (current.WidgetType == WidgetType.AudioOutput)
                {
                    var path = new List<HdaWidget>();
                    for (HdaWidget node = current; node != null; node = parents.TryGetValue(node.NodeId, out HdaWidget p) ? p : null)
                        path.Add(node);
                    path.Reverse();
                    return path;
                }
                visited.Add(current.NodeId);
                foreach (byte id in current.ConnectionList)
                {
                    if (!(codec.NodeById(id) is HdaWidget next) || visited.Contains(id) || parents.ContainsKey(id)) continue;
                    queue.Add(next);
                    parents[id] = current;
                }
            }
            return null;
        }
    }
}
