// Ported from SerenityOS Kernel/Devices/Audio/IntelHDA/OutputPath.h
// (commit e5966bd, BSD-2-Clause, (c) 2023 Jelle Raaijmakers; see
// LICENSE-SerenityOS.txt). Cuts / changes (step247): ErrorOr -> bool, a
// failure names its step; to_string -> Describe for the boot line; EAPD is
// enabled on the pin when it has one; the format is chosen (48 kHz, else
// 44.1 kHz, 16-bit stereo) instead of copied from the converter.

using System.Collections.Generic;

namespace OS.Hal.Audio.Hda
{
    internal sealed class HdaOutputPath
    {
        public const byte FixedPcmBits = 16;
        public const byte FixedChannelCount = 2;

        private readonly List<HdaWidget> _path;
        public readonly HdaOutputStream Stream;

        /// <summary>The step that failed, for the boot line.</summary>
        public string Failure = "";

        public HdaOutputPath(List<HdaWidget> path, HdaOutputStream stream)
        {
            _path = path;
            Stream = stream;
        }

        private HdaWidget Get(WidgetType type)
        {
            foreach (HdaWidget widget in _path)
                if (widget.WidgetType == type) return widget;
            return null;
        }

        public HdaWidget Pin => Get(WidgetType.PinComplex);
        public HdaWidget Output => Get(WidgetType.AudioOutput);

        public bool Activate()
        {
            HdaWidget output = Output, pin = Pin;
            if (output == null || pin == null) { Failure = "path without converter or pin"; return false; }

            // Power on the function group and every widget that can be.
            if (!output.Parent.SetPowerStateD0()) { Failure = "group power"; return false; }
            foreach (HdaWidget widget in _path)
                if (widget.PowerControlSupported && !widget.SetPowerStateD0()) { Failure = "widget power"; return false; }

            if (!output.SetConverterStreamAndChannel(Stream.StreamNumber, HdaOutputStream.FixedChannel)) { Failure = "stream/channel"; return false; }

            // Full volume on every output amp: gain = offset is 0 dB.
            foreach (HdaWidget widget in _path)
            {
                if (!widget.OutputAmpPresent) continue;
                if (!widget.SetAmplifierGainMute(mute: false, gain: widget.OutputAmpCapabilities.Offset)) { Failure = "amp"; return false; }
            }

            // Connect each widget to the next one towards the converter.
            for (int i = 0; i < _path.Count - 1; i++)
            {
                HdaWidget left = _path[i], right = _path[i + 1];
                if (left.ConnectionList.Count <= 1) continue;   // hard-wired
                int index = left.ConnectionList.IndexOf(right.NodeId);
                if (index < 0) { Failure = "connection"; return false; }
                if (!left.SetConnectionSelect((byte)index)) { Failure = "select"; return false; }
            }

            if (!pin.EnablePinOutput()) { Failure = "pin control"; return false; }
            if (pin.PinEapdCapable) pin.EnableEapd();

            foreach (uint rate in new uint[] { 48_000, 44_100 })
            {
                var format = new FormatParameters { SampleRate = rate, PcmBits = FixedPcmBits, NumberOfChannels = FixedChannelCount };
                if (output.SupportsPcm(rate, FixedPcmBits) && output.ChannelCount >= FixedChannelCount)
                    return SetFormat(format);
            }
            Failure = "no 48/44.1 kHz 16-bit stereo";
            return false;
        }

        // 7.3.3.8: the stream descriptor's format and the converter's must match.
        private bool SetFormat(FormatParameters format)
        {
            if (!Stream.SetFormat(format)) { Failure = "stream format"; return false; }
            if (!Output.SetConverterFormat(format)) { Failure = "converter format"; return false; }
            return true;
        }

        public string Describe()
        {
            string s = "";
            foreach (HdaWidget widget in _path)
                s += (s.Length == 0 ? "" : ">") + widget.NodeId.ToString();
            return s;
        }
    }
}
