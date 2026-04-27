namespace OS.Hal
{
    // CMOS Real-Time Clock reader — wall-clock source for Phase 1.
    //
    // CMOS lives behind two legacy I/O ports:
    //   0x70 — index/select port (write the register number)
    //   0x71 — data port (read or write the selected register)
    //
    // CMOS register map (subset we read):
    //   0x00  seconds          0..59
    //   0x02  minutes          0..59
    //   0x04  hours            0..23  (or 1..12 with 0x80=PM in 12-hr mode)
    //   0x06  weekday          1..7
    //   0x07  day-of-month     1..31
    //   0x08  month            1..12
    //   0x09  year             0..99   (low two digits)
    //   0x32  century          19/20  (not always present, varies by board)
    //   0x0A  Status Register A (bit 7 = Update In Progress)
    //   0x0B  Status Register B (bit 1 = 24-hour mode, bit 2 = binary mode)
    //
    // Format depends on Status Register B:
    //   bit 2 = 0 → BCD     (e.g. 0x59 = 59)
    //   bit 2 = 1 → binary  (e.g. 0x3B = 59)
    //   bit 1 = 0 → 12-hour mode (high bit of hours = PM)
    //   bit 1 = 1 → 24-hour mode
    //
    // Read protocol (from osdev wiki / canonical implementations):
    //   1. Wait until UIP=0 (Status A bit 7 clear).
    //   2. Read all fields into "old" snapshot.
    //   3. Wait until UIP=0 again.
    //   4. Read all fields into "new" snapshot.
    //   5. If old == new, accept; else loop.
    //   6. Apply BCD→binary conversion if needed.
    //   7. Apply 12→24-hour conversion if needed.
    //   8. Combine century + year for full year (or guess if no century).
    //
    // Requires PortIoPatcher.TryInstall() to have run.
    internal static class Rtc
    {
        private const ushort IndexPort = 0x70;
        private const ushort DataPort = 0x71;

        private const byte RegSeconds = 0x00;
        private const byte RegMinutes = 0x02;
        private const byte RegHours = 0x04;
        private const byte RegWeekday = 0x06;
        private const byte RegDayOfMonth = 0x07;
        private const byte RegMonth = 0x08;
        private const byte RegYear = 0x09;
        private const byte RegCentury = 0x32;
        private const byte RegStatusA = 0x0A;
        private const byte RegStatusB = 0x0B;

        private const byte StatusA_UIP = 0x80;
        private const byte StatusB_24Hour = 0x02;
        private const byte StatusB_Binary = 0x04;
        private const byte HourPmFlag = 0x80;

        private static byte Read(byte reg)
        {
            // High bit of index controls NMI gate. We leave NMI behavior
            // unchanged by preserving whatever bit the firmware set —
            // QEMU/real boards typically default to NMI enabled (bit 7=0).
            // Since we only read, this is harmless either way.
            PortIo.Out8(IndexPort, reg);
            return PortIo.In8(DataPort);
        }

        private static bool IsUpdateInProgress() =>
            (Read(RegStatusA) & StatusA_UIP) != 0;

        // Convert BCD-encoded byte to binary. 0x59 → 59. The formula
        // ((v & 0x0F) + ((v >> 4) * 10)) handles values up to 99.
        private static byte BcdToBinary(byte v) =>
            (byte)((v & 0x0F) + ((v >> 4) * 10));

        // Spin until UIP clears. Bounded so a stuck CMOS doesn't hang
        // boot indefinitely. 1M iterations is well over a millisecond
        // on any modern CPU; CMOS update windows are ~244 µs.
        private static bool WaitUipClear()
        {
            for (int i = 0; i < 1_000_000; i++)
            {
                if (!IsUpdateInProgress())
                    return true;
            }
            return false;
        }

        // Snapshot of CMOS time/date in normalized binary form.
        public struct Snapshot
        {
            public ushort Year;        // full year (e.g. 2026)
            public byte Month;         // 1..12
            public byte Day;           // 1..31
            public byte Hour;          // 0..23
            public byte Minute;        // 0..59
            public byte Second;        // 0..59
            public byte Weekday;       // 1..7 as reported (impl-defined origin)
            public bool CenturyValid;  // true if RegCentury was non-zero
        }

        public static bool TryRead(out Snapshot snapshot)
        {
            snapshot = default;

            // Read twice and compare — guards against catching a write
            // partway through. Bound the retry count so a malfunctioning
            // CMOS doesn't deadlock boot.
            byte secOld = 0, minOld = 0, hrOld = 0, dayOld = 0;
            byte monOld = 0, yrOld = 0, centOld = 0, dowOld = 0;
            byte sec = 0, min = 0, hr = 0, day = 0, mon = 0, yr = 0, cent = 0, dow = 0;

            for (int attempt = 0; attempt < 16; attempt++)
            {
                if (!WaitUipClear()) return false;

                secOld = Read(RegSeconds);
                minOld = Read(RegMinutes);
                hrOld = Read(RegHours);
                dowOld = Read(RegWeekday);
                dayOld = Read(RegDayOfMonth);
                monOld = Read(RegMonth);
                yrOld = Read(RegYear);
                centOld = Read(RegCentury);

                if (!WaitUipClear()) return false;

                sec = Read(RegSeconds);
                min = Read(RegMinutes);
                hr = Read(RegHours);
                dow = Read(RegWeekday);
                day = Read(RegDayOfMonth);
                mon = Read(RegMonth);
                yr = Read(RegYear);
                cent = Read(RegCentury);

                if (sec == secOld && min == minOld && hr == hrOld &&
                    day == dayOld && mon == monOld && yr == yrOld &&
                    cent == centOld && dow == dowOld)
                {
                    break;
                }

                if (attempt == 15) return false;
            }

            byte statusB = Read(RegStatusB);
            bool isBinary = (statusB & StatusB_Binary) != 0;
            bool is24Hour = (statusB & StatusB_24Hour) != 0;

            // Hours: rescue PM bit before BCD conversion (BCD-encoded
            // hour can have its high bit set as the PM flag, which
            // would interfere with the BCD decode).
            bool pm = false;
            if (!is24Hour)
            {
                if ((hr & HourPmFlag) != 0)
                {
                    pm = true;
                    hr = (byte)(hr & 0x7F);
                }
            }

            if (!isBinary)
            {
                sec = BcdToBinary(sec);
                min = BcdToBinary(min);
                hr = BcdToBinary(hr);
                day = BcdToBinary(day);
                mon = BcdToBinary(mon);
                yr = BcdToBinary(yr);
                cent = BcdToBinary(cent);
                dow = BcdToBinary(dow);
            }

            // 12-hour PM correction. 12 PM stays 12; 1..11 PM → +12.
            // 12 AM → 0 hours.
            if (!is24Hour)
            {
                if (pm)
                {
                    if (hr < 12) hr = (byte)(hr + 12);
                }
                else
                {
                    if (hr == 12) hr = 0;
                }
            }

            ushort fullYear;
            bool centuryValid = cent != 0;
            if (centuryValid)
            {
                fullYear = (ushort)((ushort)cent * 100 + yr);
            }
            else
            {
                // No century register — guess. Year values 0..69 → 21st
                // century (2000..2069), 70..99 → 20th century. Same
                // convention used by `dotnet/runtime` and most BIOSes
                // that lack a century byte.
                fullYear = yr < 70 ? (ushort)(2000 + yr) : (ushort)(1900 + yr);
            }

            snapshot.Year = fullYear;
            snapshot.Month = mon;
            snapshot.Day = day;
            snapshot.Hour = hr;
            snapshot.Minute = min;
            snapshot.Second = sec;
            snapshot.Weekday = dow;
            snapshot.CenturyValid = centuryValid;
            return true;
        }
    }
}
