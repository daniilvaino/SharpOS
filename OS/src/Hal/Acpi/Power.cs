namespace OS.Hal.Acpi
{
    // Power off and reset by the machine's own means (step210), not the
    // firmware's: ACPI S5, then a reset through 0xCF9 and through the keyboard
    // controller. UEFI ResetSystem is what is left after both (Platform.Shutdown).
    //
    // S5 is a write of SLP_TYPx | SLP_EN to the PM1 control registers the FADT
    // names. SLP_TYP is not in the FADT: it is the \_S5_ package of the DSDT,
    // in AML. Only that one package is read, by its byte shape — the
    // interpreter that the DSDT otherwise wants is not needed for it:
    //   [NameOp 0x08] "_S5_" PackageOp 0x12 PkgLength NumElements a b ...
    // where a and b are BytePrefix 0x0A + byte, ZeroOp 0x00 or OneOp 0x01.
    //
    // Parsed once, at Acpi.Init: the tables are in firmware memory that is
    // certainly mapped then. At shutdown only ports are touched.
    internal static unsafe class Power
    {
        // FADT offsets (ACPI 6.x, Table 5.9).
        private const int FadtDsdt = 40;
        private const int FadtSmiCmd = 48;
        private const int FadtAcpiEnable = 52;
        private const int FadtPm1aCnt = 64;
        private const int FadtPm1bCnt = 68;
        private const int FadtXDsdt = 140;
        private const int FadtXPm1aCnt = 172;
        private const int FadtXPm1bCnt = 184;

        private const ushort SlpEn = 1 << 13;
        private const ushort SciEn = 1 << 0;

        private static ushort s_pm1a, s_pm1b, s_smiCmd;
        private static byte s_acpiEnable;
        private static ushort s_slpTypA, s_slpTypB;
        private static bool s_s5;

        /// <summary>Why S5 is not available, or null.</summary>
        public static string Missing;

        public static bool CanPowerOff => s_s5;
        public static ushort Pm1a => s_pm1a;
        public static ushort SlpTypA => s_slpTypA;

        /// <summary>From Acpi.Init with the FADT located.</summary>
        public static void Parse(byte* fadt)
        {
            s_s5 = false;
            if (fadt == null) { Missing = "no FADT"; return; }
            uint length = ((AcpiTableHeader*)fadt)->Length;

            s_smiCmd = (ushort)*(uint*)(fadt + FadtSmiCmd);
            s_acpiEnable = fadt[FadtAcpiEnable];
            s_pm1a = (ushort)*(uint*)(fadt + FadtPm1aCnt);
            s_pm1b = (ushort)*(uint*)(fadt + FadtPm1bCnt);

            // The extended blocks, when the legacy ones are empty: only the
            // I/O space (address space 1) is driven here.
            if (s_pm1a == 0 && length >= FadtXPm1aCnt + 12 && fadt[FadtXPm1aCnt] == 1)
                s_pm1a = (ushort)*(ulong*)(fadt + FadtXPm1aCnt + 4);
            if (s_pm1b == 0 && length >= FadtXPm1bCnt + 12 && fadt[FadtXPm1bCnt] == 1)
                s_pm1b = (ushort)*(ulong*)(fadt + FadtXPm1bCnt + 4);
            if (s_pm1a == 0) { Missing = "no PM1a control block in I/O space"; return; }

            ulong dsdt = 0;
            if (length >= FadtXDsdt + 8) dsdt = *(ulong*)(fadt + FadtXDsdt);
            if (dsdt == 0) dsdt = *(uint*)(fadt + FadtDsdt);
            if (dsdt == 0) { Missing = "no DSDT"; return; }

            if (!TryFindS5((byte*)dsdt, out s_slpTypA, out s_slpTypB))
            {
                Missing = "no \\_S5_ package in the DSDT";
                return;
            }
            Missing = null;
            s_s5 = true;
        }

        private static bool TryFindS5(byte* dsdt, out ushort typA, out ushort typB)
        {
            typA = typB = 0;
            AcpiTableHeader* h = (AcpiTableHeader*)dsdt;
            if (h->Length <= 36 || h->Length > 4 * 1024 * 1024) return false;
            byte* end = dsdt + h->Length;

            for (byte* p = dsdt + 36; p + 5 < end; p++)
            {
                if (p[0] != (byte)'_' || p[1] != (byte)'S' || p[2] != (byte)'5' || p[3] != (byte)'_') continue;
                // A name, not a reference: NameOp before it (maybe through
                // a root prefix), PackageOp after.
                bool named = p[-1] == 0x08 || (p[-1] == (byte)'\\' && p[-2] == 0x08);
                if (!named || p[4] != 0x12) continue;

                byte* q = p + 5;
                q += 1 + ((q[0] >> 6) & 3);          // PkgLength: lead byte + 0..3 more
                q += 1;                              // NumElements
                if (q >= end || !TryReadSmallInt(ref q, end, out typA)) return false;
                if (!TryReadSmallInt(ref q, end, out typB)) typB = 0;
                return true;
            }
            return false;
        }

        private static bool TryReadSmallInt(ref byte* q, byte* end, out ushort value)
        {
            value = 0;
            if (q >= end) return false;
            byte op = q[0];
            if (op == 0x0A && q + 1 < end) { value = q[1]; q += 2; return true; }   // BytePrefix
            if (op == 0x00 || op == 0x01) { value = op; q += 1; return true; }      // Zero / One
            return false;
        }

        /// <summary>
        /// S5. Returns only if the machine is still on after it — then the
        /// caller tries a reset.
        /// </summary>
        public static void PowerOff()
        {
            if (!s_s5 || !PortIoPatcher.IsWordInstalled) return;

            // ACPI mode: SCI_EN set by the firmware normally; if not, ask the
            // SMI handler for it and give it a moment.
            if ((PortIo.In16(s_pm1a) & SciEn) == 0 && s_smiCmd != 0 && s_acpiEnable != 0)
            {
                PortIo.Out8(s_smiCmd, s_acpiEnable);
                for (int i = 0; i < 300 && (PortIo.In16(s_pm1a) & SciEn) == 0; i++) Wait(1);
            }

            PortIo.Out16(s_pm1a, (ushort)((PortIo.In16(s_pm1a) & ~(7 << 10)) | (s_slpTypA << 10) | SlpEn));
            if (s_pm1b != 0)
                PortIo.Out16(s_pm1b, (ushort)((PortIo.In16(s_pm1b) & ~(7 << 10)) | (s_slpTypB << 10) | SlpEn));
            Wait(500);
        }

        /// <summary>A reset through 0xCF9, then through the keyboard controller. Returns if neither took.</summary>
        public static void Reset()
        {
            if (!PortIoPatcher.IsInstalled) return;

            // Reset Control Register: system reset (bit 1) with the CPU reset
            // bit (2) rising.
            PortIo.Out8(0xCF9, 0x02);
            PortIo.Out8(0xCF9, 0x06);
            Wait(500);

            // 8042: pulse the reset line, once its input buffer is empty.
            for (int i = 0; i < 100000 && (PortIo.In8(0x64) & 0x02) != 0; i++) { }
            PortIo.Out8(0x64, 0xFE);
            Wait(500);
        }

        private static void Wait(uint ms)
        {
            if (!OS.Hal.Timer.Hpet.IsInitialized) { for (int i = 0; i < 10_000_000; i++) { } return; }
            ulong end = OS.Hal.Timer.Hpet.ReadCounter() + OS.Hal.Timer.Hpet.FrequencyHz / 1000 * ms;
            while (OS.Hal.Timer.Hpet.ReadCounter() < end) { }
        }
    }
}
