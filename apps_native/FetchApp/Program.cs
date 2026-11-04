// FETCH: what this system is and what came up, from the kernel's SystemInfo
// (step216; SYSINFO of step211 merged in). Two channels, with hints:
//
//   - the standard output: the SystemInfo record, data for programs — not
//     printed on the terminal (`fetch | kql "project Cpu.Avx, Build.Tests"`);
//   - stdtext: the panel, a logo column and the facts beside it, a section
//     a line — the whole record is the object's, in the inspector; left out
//     when the record goes to a pipe or a file.
//
// `env PIPEHINTS=off FETCH` prints both; `fetch 3>&1 >&- | less` sends the
// panel down the pipe.

using System;
using System.Collections.Generic;
using System.Runtime;
using SharpOS.AppSdk;
using SharpOS.Std.Pipes;
using SharpOS.Std.SystemInformation;

namespace FetchApp
{
    internal static unsafe class AppEntry
    {
        /// <summary>Column the facts start in, wide enough for the logo.</summary>
        private const int LogoWidth = 32;

        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            return AppRuntime.Finish(Run());
        }

        [RuntimeExport("SharpAppBootstrap")]
        private static int SharpAppBootstrap(ulong startupPointer)
        {
            RuntimeImports.ManagedStartup();
            return SharpAppEntry(startupPointer);
        }

        private static int Main() => Run();

        private static int Run()
        {
            SystemInfo info;
            try { info = Process.GetSystemInfo(); }
            catch (InvalidOperationException e)
            {
                Console.Error.WriteLine("fetch: " + e.Message);
                return 1;
            }

            using PipeWriter<SystemInfo> output = Pipe.Write<SystemInfo>(PipeHints.NotForTerminal);
            output.Copy(info);

            Pipe.TextHints = PipeHints.DescribesOutput;
            string[] logo = Logo;
            List<string> facts = Facts(info);
            // A section a line, cut at the edge: the panel is as tall as the logo.
            int room = Console.WindowWidth - LogoWidth - 2;
            int lines = Math.Max(logo.Length, facts.Count);
            for (int i = 0; i < lines; i++)
            {
                string left = i < logo.Length ? logo[i] : "";
                string right = i < facts.Count ? facts[i] : "";
                Pipe.Text.Write(right.Length == 0 ? left + "\n" : left.PadRight(LogoWidth) + "  " + Fit(right, room) + "\n");
            }
            Pipe.Text.Write("\n");
            return 0;
        }

        private static List<string> Facts(SystemInfo s)
        {
            var facts = new List<string>
            {
                "\u001b[1mSharpOS\u001b[0m " + s.Build.Id,
                "────────────────────",
            };

            Line(facts, "build", Join(s.Build.Tests ? "tests" : "no tests", s.Build.HostedRuntime ? "hosted runtime" : null,
                                      s.Build.Fork == "none" ? null : "fork " + s.Build.Fork));
            Line(facts, "firmware", s.Firmware.Vendor + " rev " + s.Firmware.Revision.ToString() + ", boot " + s.Firmware.BootMedium);
            Line(facts, "cpu", s.Cpu.Vendor + ", " + s.Cpu.Processors.ToString() + " cpu"
                               + Join(s.Cpu.Sse41 ? "sse4.1" : null, s.Cpu.Avx ? "avx" : null, s.Cpu.Fma ? "fma" : null, ", "));
            Line(facts, "memory", s.Memory.FreeMiB.ToString() + " of " + s.Memory.TotalMiB.ToString() + " MiB free");

            var storage = new List<string>();
            foreach (StorageFacts d in s.Storage)
                storage.Add(d.Mounted ? d.Kind + " " + d.FileSystem : d.Kind + " not mounted");
            Line(facts, "storage", storage.Count == 0 ? "none" : string.Join(", ", storage));

            var input = new List<string>();
            foreach (InputFacts i in s.Input)
                if (i.Present) input.Add(i.Kind);
            Line(facts, "input", input.Count == 0 ? "none" : string.Join(", ", input));

            Line(facts, "display", s.Display.Present
                ? s.Display.Width.ToString() + "x" + s.Display.Height.ToString() + (s.Display.WriteCombining ? ", write-combining" : "")
                : "none");
            AudioFacts a = s.Audio;
            string card = a == null || a.Kind == "none" || a.Kind == null
                ? ""
                : a.Kind + (string.IsNullOrEmpty(a.Codec) ? "" : " " + a.Codec) + ", " + a.SampleRate.ToString() + " Hz";
            string speaker = a != null && a.Speaker ? "pc speaker" : "";
            Line(facts, "audio", card.Length == 0 && speaker.Length == 0 ? "none"
                : card.Length == 0 ? speaker : speaker.Length == 0 ? card : card + ", " + speaker);
            Line(facts, "timers", (s.Timers.HpetHz == 0 ? "no hpet" : "hpet " + (s.Timers.HpetHz / 1000000).ToString() + " MHz")
                                  + (s.Timers.ApicTimer ? ", apic" : ""));
            Line(facts, "bus", "pci " + s.Bus.PciDevices.ToString() + ", xhci " + s.Bus.XhciControllers.ToString()
                               + ", usb " + s.Bus.UsbDevices.ToString() + ", msi " + s.Bus.MsiSources.ToString());
            Line(facts, "power", s.Power.PowerOff ? "acpi power off" : "no acpi power off");
            return facts;
        }

        // The names that are there, by commas; a leading one when the last is the separator.
        private static string Join(string a, string b, string c, string lead = null)
        {
            var parts = new List<string>();
            if (a != null) parts.Add(a);
            if (b != null) parts.Add(b);
            if (c != null) parts.Add(c);
            if (lead != null) return parts.Count == 0 ? "" : lead + string.Join(", ", parts);
            return string.Join(", ", parts);
        }

        private const int NameWidth = 9;

        private static void Line(List<string> facts, string name, string value)
            => facts.Add("\u001b[36m" + name + "\u001b[0m" + new string(' ', NameWidth - name.Length) + value);

        // A line no wider than the room left of the panel; escapes take no room.
        private static string Fit(string line, int room)
        {
            if (room <= 0) return line;
            int shown = 0, cut = 0;
            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '\u001b')
                {
                    while (i < line.Length && line[i] != 'm') i++;
                    continue;
                }
                if (++shown > room) return line.Substring(0, cut) + "…\u001b[0m";
                if (shown < room) cut = i + 1;
            }
            return line;
        }

        // A property rather than a static field with an initialiser: no class
        // constructor needed.
        private static string[] Logo => new string[]
        {
            " ▄▄▄▄ ▄▄ ▄▄  ▄▄▄  ▄▄▄▄  ▄▄▄▄",
            "█▄▄▄▄ ██▄██ ██▀██ ██▄█▄ ██▄█▀  ",
            "▄▄██▀▒██▒██▒██▀██▒██▒██▒██▒",
            "░░░ ░░ ░░ ░░ ░░ ░░ ░░ ░░    ",
            "            ",
            "     ███████     █████████ ",
            "  ███▒▒▒▒▒███  ███▒▒▒▒▒███",
            " ███     ▒▒███▒███    ▒▒▒ ",
            "▒███      ▒███▒▒█████████ ",
            "▒███      ▒███ ▒▒▒▒▒▒▒▒███",
            "▒▒███     ███  ███    ▒███",
            " ▒▒▒███████▒  ▒▒█████████ ",
            "   ▒▒▒▒▒▒▒     ▒▒▒▒▒▒▒▒▒",
        };
    }
}
