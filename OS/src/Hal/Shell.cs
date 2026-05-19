using OS.Boot;
using OS.Kernel;

namespace OS.Hal
{
    // Phase B#3 — native-tier command shell.
    //
    // Engine (ExecuteCore) parses a command line held as char[]+len and
    // dispatches, writing through ShellOut (serial Console always; +
    // FbTty when the interactive REPL is live). Pure of any input loop,
    // so the headless oracle drives Execute(string) with literals and
    // asserts the return codes.
    //
    // RunInteractive() is the real REPL: poll Ps2Keyboard -> Decode ->
    // LineEditor, echo to serial+FbTty, dispatch on Enter, until `exit`.
    // It blocks on input so it is gated default-off (would hang the
    // headless regression run) and only entered under SHARPOS_GUI=1.
    //
    // Tokeniser is hand-rolled over indexing only (no Substring/Split —
    // not guaranteed in this env).
    internal static unsafe class Shell
    {
        public const int Capacity = 128;
        private static readonly char[] s_scratch = new char[Capacity];

        // String entry point (headless oracle). Copies into the scratch
        // buffer and runs the shared char[] core.
        public static bool Execute(string line)
        {
            if (line == null) return true;
            int len = line.Length;
            if (len > Capacity) len = Capacity;
            for (int i = 0; i < len; i++) s_scratch[i] = line[i];
            return ExecuteCore(s_scratch, len);
        }

        // Shared engine over buf[0,len).
        public static bool ExecuteCore(char[] line, int len)
        {
            if (line == null || len <= 0) return true;
            if (len > line.Length) len = line.Length;

            int n = len;
            int i = 0;
            while (i < n && line[i] == ' ') i++;
            int cmdStart = i;
            while (i < n && line[i] != ' ') i++;
            int cmdEnd = i;                       // [cmdStart, cmdEnd)
            while (i < n && line[i] == ' ') i++;
            int argStart = i;                     // rest of line

            int cl = cmdEnd - cmdStart;
            if (cl == 0) return true;             // blank line

            if (Word(line, cmdStart, cl, "help"))
            {
                ShellOut.WriteLine("commands: help ver mem devices echo clear exit");
                return true;
            }
            if (Word(line, cmdStart, cl, "ver"))
            {
                ShellOut.WriteLine("SharpOS native-tier shell - Phase B#3");
                return true;
            }
            if (Word(line, cmdStart, cl, "mem"))
            {
                ulong mib = TotalUsableMiB(out uint regions);
                ShellOut.Write("usable RAM: ");
                ShellOut.WriteUInt((uint)mib);
                ShellOut.Write(" MiB across ");
                ShellOut.WriteUInt(regions);
                ShellOut.WriteLine(" regions");
                return true;
            }
            if (Word(line, cmdStart, cl, "devices"))
            {
                ShellOut.Write("serial COM1: ");
                ShellOut.WriteLine(Serial.IsPresent ? "present" : "absent");
                ShellOut.Write("framebuffer: ");
                if (Framebuffer.IsAvailable)
                {
                    ShellOut.WriteUInt(Framebuffer.Width);
                    ShellOut.Write("x");
                    ShellOut.WriteUInt(Framebuffer.Height);
                    ShellOut.WriteLine("");
                }
                else ShellOut.WriteLine("none");
                ShellOut.Write("ps/2 kbd: ");
                ShellOut.WriteLine(Ps2Keyboard.IsPresent() ? "present" : "absent");
                ShellOut.Write("acpi xsdt entries: ");
                ShellOut.WriteUInt((uint)global::OS.Hal.Acpi.Acpi.XsdtEntryCount);
                ShellOut.WriteLine("");
                return true;
            }
            if (Word(line, cmdStart, cl, "echo"))
            {
                for (int k = argStart; k < n; k++) ShellOut.WriteChar(line[k]);
                ShellOut.WriteLine("");
                return true;
            }
            if (Word(line, cmdStart, cl, "clear"))
            {
                FbTty.Clear();                 // wipe + home cursor (no-op headless)
                ShellOut.WriteLine("[screen cleared]");
                return true;
            }
            if (Word(line, cmdStart, cl, "exit"))
            {
                ShellOut.WriteLine("bye");
                return true;
            }

            ShellOut.Write("unknown command: ");
            for (int k = cmdStart; k < cmdEnd; k++) ShellOut.WriteChar(line[k]);
            ShellOut.WriteLine("");
            return false;
        }

        // True iff the first token of buf[0,len) is "exit".
        public static bool IsExit(char[] buf, int len)
        {
            if (buf == null) return false;
            if (len > buf.Length) len = buf.Length;
            int i = 0;
            while (i < len && buf[i] == ' ') i++;
            int a = i;
            while (i < len && buf[i] != ' ') i++;
            return Word(buf, a, i - a, "exit");
        }

        // Interactive REPL. Real keystrokes via the own PS/2 driver,
        // echoed to serial + FbTty. Blocking — gated default-off.
        public static void RunInteractive()
        {
            FbTty.Init(0xE6, 0xE6, 0x00, 0x00, 0x00, 0x28);   // amber on navy
            ShellOut.ToFb = true;
            ShellOut.WriteLine("SharpOS native-tier shell");
            ShellOut.WriteLine("type 'help'; 'exit' to leave");

            char[] buf = LineEditor.Buffer;
            while (true)
            {
                ShellOut.Write("> ");
                LineEditor.Reset();
                Ps2Keyboard.ResetState();

                bool submitted = false;
                while (!submitted)
                {
                    if (!Ps2Keyboard.TryReadScancode(out byte sc)) continue;
                    Ps2Keyboard.KeyKind k = Ps2Keyboard.Decode(sc, out char ch, out _);
                    LineEditor.Status st = LineEditor.Feed(k, ch);

                    if (k == Ps2Keyboard.KeyKind.Char && st == LineEditor.Status.Changed)
                        ShellOut.WriteChar(ch);
                    else if (k == Ps2Keyboard.KeyKind.Backspace && st == LineEditor.Status.Changed)
                    {
                        Console.WriteChar('\b');
                        FbTty.Backspace();
                    }
                    else if (st == LineEditor.Status.Submitted)
                        submitted = true;
                }

                ShellOut.WriteLine("");
                int len = LineEditor.Length;
                if (IsExit(buf, len)) { ShellOut.WriteLine("bye"); break; }
                ExecuteCore(buf, len);
            }

            ShellOut.ToFb = false;
        }

        // Total Usable RAM (MiB) from the boot memory map + region
        // count. Read-only sum — PhysicalMemory keeps no stats.
        public static ulong TotalUsableMiB(out uint regionCount)
        {
            regionCount = 0;
            BootInfo bi = Platform.GetBootInfo();
            MemoryMapInfo map = bi.MemoryMap;
            if (map.Regions == null || map.RegionCount == 0) return 0;

            ulong pages = 0;
            for (uint r = 0; r < map.RegionCount; r++)
            {
                MemoryRegion* reg = &map.Regions[r];
                if (reg->Type != MemoryRegionType.Usable) continue;
                pages += reg->PageCount;
                regionCount++;
            }
            return (pages * 4096UL) / (1024UL * 1024UL);
        }

        // buf[a, a+len) == lit ?
        private static bool Word(char[] buf, int a, int len, string lit)
        {
            if (len != lit.Length) return false;
            for (int j = 0; j < len; j++)
                if (buf[a + j] != lit[j]) return false;
            return true;
        }
    }
}
