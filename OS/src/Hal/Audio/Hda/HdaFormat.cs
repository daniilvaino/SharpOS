// Ported from SerenityOS Kernel/Devices/Audio/IntelHDA/Format.cpp
// (commit e5966bd, BSD-2-Clause, (c) 2023 Jelle Raaijmakers; see
// LICENSE-SerenityOS.txt beside this file). Cuts (step247): ErrorOr -> bool
// with an out value; the encoding tables are switches.

namespace OS.Hal.Audio.Hda
{
    internal struct FormatParameters
    {
        public uint SampleRate;
        public byte PcmBits;
        public byte NumberOfChannels;
    }

    internal static class HdaFormat
    {
        // 3.3.41, 3.7.1: Stream Format Structure — rate as base x multiple / divisor.
        private static bool RateParameters(uint rate, out byte rateBase, out byte multiple, out byte divisor)
        {
            rateBase = 0; multiple = 0; divisor = 0;
            switch (rate)
            {
                case 6_000: divisor = 0b111; return true;
                case 8_000: divisor = 0b101; return true;
                case 9_600: divisor = 0b100; return true;
                case 11_025: rateBase = 1; divisor = 0b011; return true;
                case 16_000: divisor = 0b010; return true;
                case 22_050: rateBase = 1; divisor = 0b001; return true;
                case 24_000: divisor = 0b001; return true;
                case 32_000: multiple = 0b001; divisor = 0b010; return true;
                case 44_100: rateBase = 1; return true;
                case 48_000: return true;
                case 88_200: rateBase = 1; multiple = 0b001; return true;
                case 96_000: multiple = 0b001; return true;
                case 144_000: multiple = 0b010; return true;
                case 176_400: rateBase = 1; multiple = 0b011; return true;
                case 192_000: multiple = 0b011; return true;
                default: return false;
            }
        }

        private static bool BitsEncoding(byte bits, out byte encoding)
        {
            switch (bits)
            {
                case 8: encoding = 0b000; return true;
                case 16: encoding = 0b001; return true;
                case 20: encoding = 0b010; return true;
                case 24: encoding = 0b011; return true;
                case 32: encoding = 0b100; return true;
                default: encoding = 0; return false;
            }
        }

        public static bool Encode(FormatParameters format, out ushort encoded)
        {
            encoded = 0;
            if (!RateParameters(format.SampleRate, out byte rateBase, out byte multiple, out byte divisor)) return false;
            if (!BitsEncoding(format.PcmBits, out byte bits)) return false;
            if (format.NumberOfChannels < 1 || format.NumberOfChannels > 16) return false;
            // PCM only: bit 15 clear.
            encoded = (ushort)(((rateBase & 0x1) << 14)
                             | ((multiple & 0x7) << 11)
                             | ((divisor & 0x7) << 8)
                             | ((bits & 0x7) << 4)
                             | ((format.NumberOfChannels - 1) & 0xf));
            return true;
        }

        public static bool Decode(ushort format, out FormatParameters decoded)
        {
            decoded = default;
            byte rateBase = (byte)((format >> 14) & 0x1);
            byte multiple = (byte)((format >> 11) & 0x7);
            byte divisor = (byte)((format >> 8) & 0x7);
            uint rate = 0;
            uint[] candidates = { 6_000, 8_000, 9_600, 11_025, 16_000, 22_050, 24_000, 32_000, 44_100, 48_000, 88_200, 96_000, 144_000, 176_400, 192_000 };
            foreach (uint candidate in candidates)
            {
                RateParameters(candidate, out byte b, out byte m, out byte d);
                if (b == rateBase && m == multiple && d == divisor) { rate = candidate; break; }
            }

            byte bits = 0;
            byte encoding = (byte)((format >> 4) & 0x7);
            foreach (byte candidate in new byte[] { 8, 16, 20, 24, 32 })
            {
                BitsEncoding(candidate, out byte e);
                if (e == encoding) { bits = candidate; break; }
            }

            if (rate == 0 || bits == 0) return false;
            decoded = new FormatParameters { SampleRate = rate, PcmBits = bits, NumberOfChannels = (byte)((format & 0xf) + 1) };
            return true;
        }
    }
}
