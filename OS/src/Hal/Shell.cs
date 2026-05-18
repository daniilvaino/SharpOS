using OS.Boot;
using OS.Kernel;

namespace OS.Hal
{
    // Phase B#3 sub-step 3 — native-tier command shell ENGINE: parse a
    // command line, dispatch, print to the serial Console. Pure of any
    // input loop, so the headless oracle drives Execute() with literal
    // command strings and asserts the return codes (the scancode->line
    // path is already covered by Ps2Probe/LineEditorProbe). The
    // interactive REPL (real ReadLine via Ps2Keyboard + LineEditor,
    // echoed to FbConsole under SHARPOS_GUI=1) is the next sub-step and
    // sits on top of this engine unchanged.
    //
    // Tokeniser is hand-rolled over string indexing only (no Substring/
    // Split — those BCL surfaces are not guaranteed in this env);
    // string indexing + .Length are used everywhere and are safe.
    internal static unsafe class Shell
    {
        // Execute one command line. Returns true if the command was
        // recognised (empty line counts as handled), false for an
        // unknown command (already reported to the Console).
        public static bool Execute(string line)
        {
            if (line == null) return true;

            int n = line.Length;
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
                Console.WriteLine("commands: help ver mem devices echo clear");
                return true;
            }
            if (Word(line, cmdStart, cl, "ver"))
            {
                Console.WriteLine("SharpOS native-tier shell - Phase B#3");
                return true;
            }
            if (Word(line, cmdStart, cl, "mem"))
            {
                ulong mib = TotalUsableMiB(out uint regions);
                Console.Write("usable RAM: ");
                Console.WriteUInt((uint)mib);
                Console.Write(" MiB across ");
                Console.WriteUInt(regions);
                Console.WriteLine(" regions");
                return true;
            }
            if (Word(line, cmdStart, cl, "devices"))
            {
                Console.Write("serial COM1: ");
                Console.WriteLine(Serial.IsPresent ? "present" : "absent");
                Console.Write("framebuffer: ");
                if (Framebuffer.IsAvailable)
                {
                    Console.WriteUInt(Framebuffer.Width);
                    Console.Write("x");
                    Console.WriteUInt(Framebuffer.Height);
                    Console.WriteLine("");
                }
                else Console.WriteLine("none");
                Console.Write("ps/2 kbd: ");
                Console.WriteLine(Ps2Keyboard.IsPresent() ? "present" : "absent");
                Console.Write("acpi xsdt entries: ");
                Console.WriteUInt((uint)global::OS.Hal.Acpi.Acpi.XsdtEntryCount);
                Console.WriteLine("");
                return true;
            }
            if (Word(line, cmdStart, cl, "echo"))
            {
                for (int k = argStart; k < n; k++) Console.WriteChar(line[k]);
                Console.WriteLine("");
                return true;
            }
            if (Word(line, cmdStart, cl, "clear"))
            {
                if (Framebuffer.IsAvailable) FbConsole.Clear(0, 0, 40);
                Console.WriteLine("[screen cleared]");
                return true;
            }

            Console.Write("unknown command: ");
            for (int k = cmdStart; k < cmdEnd; k++) Console.WriteChar(line[k]);
            Console.WriteLine("");
            return false;
        }

        // Total Usable RAM (MiB) from the boot memory map, plus the
        // Usable-region count. Read-only sum — PhysicalMemory keeps no
        // stats (bump allocator), and the map is the ground truth.
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

        // line[a, a+len) == lit ?
        private static bool Word(string line, int a, int len, string lit)
        {
            if (len != lit.Length) return false;
            for (int j = 0; j < len; j++)
                if (line[a + j] != lit[j]) return false;
            return true;
        }
    }
}
