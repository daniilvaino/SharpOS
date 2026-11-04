// Ported from SerenityOS Kernel/Devices/Audio/IntelHDA/Codec.{h,cpp}
// (commit e5966bd, BSD-2-Clause, (c) 2023 Jelle Raaijmakers; see
// LICENSE-SerenityOS.txt). Cuts (step247): ErrorOr -> bool (a failed command
// fails the node and the codec); RefPtr -> references; the node map is a
// 256-slot array by node id; the name tables and debug_dump are gone — one
// summary line per codec is printed by the caller; NodeWithChildren's
// template becomes a list of the base type; only audio function groups are
// populated. Changes: SetAmplifierGainMute puts the input-amp flag in bit 14
// (upstream shifts both flags to 15); EAPD/BTL enable (7.3.3.16) is added for
// pins that have it; the codec revision check takes any 1.x.

using System.Collections.Generic;

namespace OS.Hal.Audio.Hda
{
    // 7.3.3: Controls.
    internal enum CodecControlVerb : ushort
    {
        GetParameter = 0xf00,
        GetConnectionSelectControl = 0xf01,
        SetConnectionSelectControl = 0x701,
        GetConnectionListEntry = 0xf02,
        GetAmplifierGainMute = 0xb,
        SetAmplifierGainMute = 0x3,
        GetConverterFormat = 0xa,
        SetConverterFormat = 0x2,
        SetPowerState = 0x705,
        GetConverterStreamChannel = 0xf06,
        SetConverterStreamChannel = 0x706,
        SetPinWidgetControl = 0x707,
        SetEapdBtlEnable = 0x70c,
        GetConfigurationDefault = 0xf1c,
    }

    // 7.3.4: Parameters.
    internal enum GetParameterId : byte
    {
        VendorId = 0x00,
        RevisionId = 0x02,
        SubordinateNodeCount = 0x04,
        FunctionGroupType = 0x05,
        AudioWidgetCapabilities = 0x09,
        SupportedPcmSizeRates = 0x0a,
        SupportedStreamFormats = 0x0b,
        PinCapabilities = 0x0c,
        InputAmplifierCapabilities = 0x0d,
        ConnectionListLength = 0x0e,
        OutputAmplifierCapabilities = 0x12,
    }

    internal enum NodeType { Root, FunctionGroup, Widget }

    internal abstract class HdaNode
    {
        public readonly HdaCodec Codec;
        public readonly HdaNode Parent;
        public readonly byte NodeId;
        public readonly NodeType Type;

        protected HdaNode(HdaCodec codec, HdaNode parent, byte nodeId, NodeType type)
        {
            Codec = codec; Parent = parent; NodeId = nodeId; Type = type;
        }

        public bool Command(CodecControlVerb verb, ushort payload, out uint response)
            => Codec.Controller.SendCommand(Codec.Address, NodeId, verb, payload, out response);

        public bool Parameter(GetParameterId id, out uint value)
            => Command(CodecControlVerb.GetParameter, (ushort)id, out value);

        // 7.3.3.10: Power State; D0 = 0.
        public bool SetPowerStateD0() => Command(CodecControlVerb.SetPowerState, 0, out _);

        public virtual bool Initialize()
        {
            Codec.Register(this);
            return true;
        }

        // NodeWithChildren::populate_child_nodes (7.3.4.3).
        protected bool PopulateChildren(List<HdaNode> into, bool widgets)
        {
            if (!Parameter(GetParameterId.SubordinateNodeCount, out uint count)) return false;
            byte start = (byte)((count >> 16) & 0xff);
            int total = (int)(count & 0xff);
            for (int i = 0; i < total; i++)
            {
                HdaNode child = widgets
                    ? new HdaWidget(Codec, this, (byte)(start + i))
                    : new HdaFunctionGroup(Codec, this, (byte)(start + i));
                if (!child.Initialize()) return false;
                into.Add(child);
            }
            return true;
        }
    }

    internal enum WidgetType : byte
    {
        AudioOutput = 0x0, AudioInput = 0x1, AudioMixer = 0x2, AudioSelector = 0x3,
        PinComplex = 0x4, Power = 0x5, VolumeKnob = 0x6, BeepGenerator = 0x7, VendorDefined = 0xf,
    }

    // 7.3.3.31: Configuration Default, tables 109 and 111.
    internal enum PinPortConnectivity : byte { Jack = 0b00, NoConnection = 0b01, FixedFunction = 0b10, JackAndFixedFunction = 0b11 }
    internal enum PinDefaultDevice : byte
    {
        LineOut = 0x0, Speaker = 0x1, HPOut = 0x2, CD = 0x3, SpdifOut = 0x4, DigitalOtherOut = 0x5,
        ModemLineSide = 0x6, ModemHandsetSide = 0x7, LineIn = 0x8, Aux = 0x9, MicIn = 0xa,
        Telephony = 0xb, SpdifIn = 0xc, DigitalOtherIn = 0xd, Reserved = 0xe, Other = 0xf,
    }

    // 7.3.4.10: Amplifier Capabilities.
    internal struct AmplifierCapabilities
    {
        public bool MutingSupported;
        public byte StepSize, NumberOfSteps, Offset;
    }

    internal sealed class HdaWidget : HdaNode
    {
        // 7.3.4.6: Audio Widget Capabilities, figure 86.
        private const uint InputAmpPresent = 1u << 1, OutputAmpPresentFlag = 1u << 2, AmpParamOverride = 1u << 3,
                           FormatOverride = 1u << 4, ConnectionListPresentFlag = 1u << 8, PowerControlSupportedFlag = 1u << 10;
        // 7.3.4.9: Pin Capabilities, figure 89.
        private const uint OutputCapable = 1u << 4, InputCapable = 1u << 5, EapdCapable = 1u << 16;

        public WidgetType WidgetType;
        public byte ChannelCount;
        public bool PowerControlSupported, ConnectionListPresent, OutputAmpPresent, HasInputAmp;
        private bool _formatOverride, _ampParamOverride;
        public uint SupportedPcmSizeRates;
        public AmplifierCapabilities OutputAmpCapabilities, InputAmpCapabilities;
        public bool PinOutputSupported, PinInputSupported, PinEapdCapable;
        public PinPortConnectivity PortConnectivity;
        public PinDefaultDevice DefaultDevice;
        public byte DefaultAssociation, Sequence;
        public readonly List<byte> ConnectionList = new List<byte>();
        public byte ConnectionIndex;

        public HdaWidget(HdaCodec codec, HdaNode parent, byte nodeId) : base(codec, parent, nodeId, NodeType.Widget) { }

        public bool SupportsStream => WidgetType == WidgetType.AudioInput || WidgetType == WidgetType.AudioOutput;
        public byte ConnectionSelectedNodeId => ConnectionList[ConnectionIndex];

        public override bool Initialize()
        {
            base.Initialize();
            if (!Parameter(GetParameterId.AudioWidgetCapabilities, out uint caps)) return false;
            WidgetType = (WidgetType)((caps >> 20) & 0xf);
            ChannelCount = (byte)((((caps >> 15) & 0xe) | (caps & 0x1)) + 1);
            PowerControlSupported = (caps & PowerControlSupportedFlag) != 0;
            ConnectionListPresent = (caps & ConnectionListPresentFlag) != 0;
            _formatOverride = (caps & FormatOverride) != 0;
            _ampParamOverride = (caps & AmpParamOverride) != 0;
            OutputAmpPresent = (caps & OutputAmpPresentFlag) != 0;
            HasInputAmp = (caps & InputAmpPresent) != 0;

            if (SupportsStream)
            {
                // 7.3.4.7: Supported PCM Size, Rates — the widget's own or its group's.
                HdaNode support = _formatOverride ? this : Parent;
                if (!support.Parameter(GetParameterId.SupportedPcmSizeRates, out SupportedPcmSizeRates)) return false;
            }

            HdaNode ampNode = _ampParamOverride ? this : Parent;
            if (OutputAmpPresent && !ReadAmpCapabilities(ampNode, GetParameterId.OutputAmplifierCapabilities, out OutputAmpCapabilities)) return false;
            if (HasInputAmp && !ReadAmpCapabilities(ampNode, GetParameterId.InputAmplifierCapabilities, out InputAmpCapabilities)) return false;

            if (WidgetType == WidgetType.PinComplex)
            {
                if (!Parameter(GetParameterId.PinCapabilities, out uint pin)) return false;
                PinInputSupported = (pin & InputCapable) != 0;
                PinOutputSupported = (pin & OutputCapable) != 0;
                PinEapdCapable = (pin & EapdCapable) != 0;

                if (!Command(CodecControlVerb.GetConfigurationDefault, 0, out uint config)) return false;
                Sequence = (byte)(config & 0xf);
                DefaultAssociation = (byte)((config >> 4) & 0xf);
                DefaultDevice = (PinDefaultDevice)((config >> 20) & 0xf);
                PortConnectivity = (PinPortConnectivity)(config >> 30);
            }

            return !ConnectionListPresent || PopulateConnectionList();
        }

        private static bool ReadAmpCapabilities(HdaNode node, GetParameterId id, out AmplifierCapabilities capabilities)
        {
            capabilities = default;
            if (!node.Parameter(id, out uint value)) return false;
            capabilities.MutingSupported = ((value >> 31) & 0x1) != 0;
            capabilities.StepSize = (byte)((value >> 16) & 0x7f);
            capabilities.NumberOfSteps = (byte)(((value >> 8) & 0x7f) + 1);
            capabilities.Offset = (byte)(value & 0x7f);
            return true;
        }

        // 7.3.4.11, 7.3.3.3.
        private bool PopulateConnectionList()
        {
            if (!Parameter(GetParameterId.ConnectionListLength, out uint info)) return false;
            bool longForm = ((info >> 7) & 0x1) != 0;
            int length = (int)(info & 0x7f);
            int perRequest = longForm ? 2 : 4;
            for (int offset = 0; offset < length; offset += perRequest)
            {
                if (!Command(CodecControlVerb.GetConnectionListEntry, (ushort)offset, out uint entries)) return false;
                for (int i = 0; i < perRequest && offset + i < length; i++)
                {
                    // Long-form entries name 16-bit node ids; a codec with more than
                    // 255 nodes is not something this driver addresses.
                    ConnectionList.Add((byte)(entries & (longForm ? 0xffffu : 0xffu)));
                    entries >>= 32 / perRequest;
                }
            }

            // 7.1.3: one entry is hard-wired, with no selector to read.
            if (length == 1)
            {
                ConnectionIndex = 0;
                return true;
            }
            if (!Command(CodecControlVerb.GetConnectionSelectControl, 0, out uint selected)) return false;
            ConnectionIndex = (byte)(selected & 0xff);
            if (ConnectionIndex >= ConnectionList.Count) ConnectionIndex = 0;
            return true;
        }

        // 7.3.3.7: Amplifier Gain/Mute. Both directions the widget has, both sides.
        public bool SetAmplifierGainMute(bool mute, byte gain, byte connectionIndex = 0)
        {
            ushort payload = (ushort)(((OutputAmpPresent ? 1 : 0) << 15)
                                    | ((HasInputAmp ? 1 : 0) << 14)
                                    | (1 << 13) | (1 << 12)
                                    | ((connectionIndex & 0xf) << 8)
                                    | ((mute ? 1 : 0) << 7)
                                    | (gain & 0x7f));
            return Command(CodecControlVerb.SetAmplifierGainMute, payload, out _);
        }

        // 7.3.3.2.
        public bool SetConnectionSelect(byte index) => Command(CodecControlVerb.SetConnectionSelectControl, index, out _);

        // 7.3.3.11.
        public bool SetConverterStreamAndChannel(byte stream, byte channel)
            => Command(CodecControlVerb.SetConverterStreamChannel, (ushort)(((stream & 0xf) << 4) | (channel & 0xf)), out _);

        // 7.3.3.13: Pin Widget Control — output on, low-impedance amp on.
        public bool EnablePinOutput() => Command(CodecControlVerb.SetPinWidgetControl, (1 << 7) | (1 << 6), out _);

        // 7.3.3.16: EAPD/BTL Enable — the external amplifier many laptops
        // gate their speakers behind.
        public bool EnableEapd() => Command(CodecControlVerb.SetEapdBtlEnable, 0x2, out _);

        // 7.3.3.8: Converter Format.
        public bool GetConverterFormat(out FormatParameters format)
        {
            format = default;
            return Command(CodecControlVerb.GetConverterFormat, 0, out uint value)
                && HdaFormat.Decode((ushort)(value & 0xffff), out format);
        }

        public bool SetConverterFormat(FormatParameters format)
            => HdaFormat.Encode(format, out ushort payload)
               && Command(CodecControlVerb.SetConverterFormat, payload, out _);

        // 7.3.4.7: bit 16 + n for 8/16/20/24/32 bits; bits 0..11 for the rates.
        public bool SupportsPcm(uint rate, byte bits)
        {
            int sizeBit = bits switch { 8 => 0, 16 => 1, 20 => 2, 24 => 3, 32 => 4, _ => -1 };
            int rateBit = rate switch
            {
                8_000 => 0, 11_025 => 1, 16_000 => 2, 22_050 => 3, 32_000 => 4, 44_100 => 5,
                48_000 => 6, 88_200 => 7, 96_000 => 8, 176_400 => 9, 192_000 => 10, 384_000 => 11, _ => -1,
            };
            return sizeBit >= 0 && rateBit >= 0
                && (SupportedPcmSizeRates & (1u << (16 + sizeBit))) != 0
                && (SupportedPcmSizeRates & (1u << rateBit)) != 0;
        }
    }

    internal sealed class HdaFunctionGroup : HdaNode
    {
        public readonly List<HdaNode> Widgets = new List<HdaNode>();
        public bool IsAudio;

        public HdaFunctionGroup(HdaCodec codec, HdaNode parent, byte nodeId) : base(codec, parent, nodeId, NodeType.FunctionGroup) { }

        public override bool Initialize()
        {
            base.Initialize();
            // 7.3.4.4: Function Group Type — 1 is audio. Widgets of a modem or
            // vendor group are not ours to drive.
            if (!Parameter(GetParameterId.FunctionGroupType, out uint type)) return false;
            IsAudio = (type & 0xff) == 0x1;
            return !IsAudio || PopulateChildren(Widgets, widgets: true);
        }
    }

    internal sealed class HdaRootNode : HdaNode
    {
        public readonly List<HdaNode> Groups = new List<HdaNode>();
        public ushort VendorId, DeviceId;

        public HdaRootNode(HdaCodec codec) : base(codec, null, 0, NodeType.Root) { }

        public override bool Initialize()
        {
            base.Initialize();
            if (!Parameter(GetParameterId.VendorId, out uint vendor)) return false;
            VendorId = (ushort)(vendor >> 16);
            DeviceId = (ushort)vendor;
            // SerenityOS refuses a codec that is not HDA 1.0; real codecs report
            // 1.0 almost without exception, and a newer minor is still the same
            // verb set, so only the major is checked.
            if (!Parameter(GetParameterId.RevisionId, out uint revision)) return false;
            if (((revision >> 20) & 0xf) != 1) return false;
            return PopulateChildren(Groups, widgets: false);
        }
    }

    internal sealed class HdaCodec
    {
        public readonly HdaController Controller;
        public readonly byte Address;
        public HdaRootNode Root;
        private readonly HdaNode[] _nodes = new HdaNode[256];

        public HdaCodec(HdaController controller, byte address)
        {
            Controller = controller;
            Address = address;
        }

        public void Register(HdaNode node) => _nodes[node.NodeId] = node;

        public HdaNode NodeById(byte nodeId) => _nodes[nodeId];

        public IEnumerable<HdaWidget> Widgets()
        {
            foreach (HdaNode node in _nodes)
                if (node is HdaWidget widget) yield return widget;
        }
    }
}
