using SharpOS.AppSdk;
using System.Runtime;

namespace FetchApp
{
    internal static unsafe class AppEntry
    {
        // Logo: 13 lines
        private const string L1  = " \u2584\u2584\u2584\u2584 \u2584\u2584 \u2584\u2584  \u2584\u2584\u2584  \u2584\u2584\u2584\u2584  \u2584\u2584\u2584\u2584";
        private const string L2  = "\u2588\u2584\u2584\u2584\u2584 \u2588\u2588\u2584\u2588\u2588 \u2588\u2588\u2580\u2588\u2588 \u2588\u2588\u2584\u2588\u2584 \u2588\u2588\u2584\u2588\u2580  ";
        private const string L3  = "\u2584\u2584\u2588\u2588\u2580\u2592\u2588\u2588\u2592\u2588\u2588\u2592\u2588\u2588\u2580\u2588\u2588\u2592\u2588\u2588\u2592\u2588\u2588\u2592\u2588\u2588\u2592";
        private const string L4  = "\u2591\u2591\u2591 \u2591\u2591 \u2591\u2591 \u2591\u2591 \u2591\u2591 \u2591\u2591 \u2591\u2591 \u2591\u2591    ";
        private const string L5  = "            ";
        private const string L6  = "     \u2588\u2588\u2588\u2588\u2588\u2588\u2588     \u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588 ";
        private const string L7  = "  \u2588\u2588\u2588\u2592\u2592\u2592\u2592\u2592\u2588\u2588\u2588  \u2588\u2588\u2588\u2592\u2592\u2592\u2592\u2592\u2588\u2588\u2588";
        private const string L8  = " \u2588\u2588\u2588     \u2592\u2592\u2588\u2588\u2588\u2592\u2588\u2588\u2588    \u2592\u2592\u2592 ";
        private const string L9  = "\u2592\u2588\u2588\u2588      \u2592\u2588\u2588\u2588\u2592\u2592\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588 ";
        private const string L10 = "\u2592\u2588\u2588\u2588      \u2592\u2588\u2588\u2588 \u2592\u2592\u2592\u2592\u2592\u2592\u2592\u2592\u2588\u2588\u2588";
        private const string L11 = "\u2592\u2592\u2588\u2588\u2588     \u2588\u2588\u2588  \u2588\u2588\u2588    \u2592\u2588\u2588\u2588";
        private const string L12 = " \u2592\u2592\u2592\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2592  \u2592\u2592\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588\u2588 ";
        private const string L13 = "   \u2592\u2592\u2592\u2592\u2592\u2592\u2592     \u2592\u2592\u2592\u2592\u2592\u2592\u2592\u2592\u2592";

        private const string Sep = "  \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500";

        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            Run();
            return 0;
        }

        [RuntimeExport("SharpAppBootstrap")]
        private static int SharpAppBootstrap(ulong startupPointer)
        {
            System.Runtime.RuntimeImports.ManagedStartup();
            return SharpAppEntry(startupPointer);
        }

        private static int Main()
        {
            Run();
            return 0;
        }

        private static void Run()
        {
            AppStartupBlock* startup = AppRuntime.Startup;

            // Logo lines 1-7 paired with info; lines 8-13 standalone
            Line(L1.PadRight(32),  "  SharpOS@kernel");
            Line(L2.PadRight(32),  Sep);
            Line(L3.PadRight(32),  "  OS:    SharpOS 0.1 (NativeAOT/UEFI)");
            Line(L4.PadRight(32),  "  Arch:  x86_64");
            LineAbi(L5.PadRight(32));
            LineBuild(L6.PadRight(32));
            LineImage(L7.PadRight(32), startup);
            LineStack(L8.PadRight(32), startup);
            AppHost.WriteString(L9);  AppHost.WriteString("\n");
            AppHost.WriteString(L10); AppHost.WriteString("\n");
            AppHost.WriteString(L11); AppHost.WriteString("\n");
            AppHost.WriteString(L12); AppHost.WriteString("\n");
            AppHost.WriteString(L13); AppHost.WriteString("\n");
            AppHost.WriteString("\n");

            // Smoke-test: std/ string queries + transforms (stage 3 + 4)
            string demo = "  Hello, SharpOS!  ";
            AppHost.WriteString("std demo:\n");
            AppHost.WriteString("  raw:        '"); AppHost.WriteString(demo); AppHost.WriteString("'\n");
            AppHost.WriteString("  Trim:       '"); AppHost.WriteString(demo.Trim()); AppHost.WriteString("'\n");
            AppHost.WriteString("  Upper:      '"); AppHost.WriteString(demo.ToUpperInvariant()); AppHost.WriteString("'\n");
            AppHost.WriteString("  Lower:      '"); AppHost.WriteString(demo.ToLowerInvariant()); AppHost.WriteString("'\n");
            AppHost.WriteString("  Sub(2,5):   '"); AppHost.WriteString(demo.Substring(2, 5)); AppHost.WriteString("'\n");
            AppHost.WriteString("  Repl(','): '"); AppHost.WriteString(demo.Replace(',', ';')); AppHost.WriteString("'\n");
            AppHost.WriteString("  Repl(Sharp->Dark): '"); AppHost.WriteString(demo.Replace("Sharp", "Dark")); AppHost.WriteString("'\n");
            AppHost.WriteString("  IndexOf(','):  "); AppHost.WriteUInt((uint)demo.IndexOf(',')); AppHost.WriteString("\n");
            AppHost.WriteString("  Contains('OS'): "); AppHost.WriteString(demo.Contains("OS") ? "true" : "false"); AppHost.WriteString("\n");
            AppHost.WriteString("  StartsWith('  Hello'): "); AppHost.WriteString(demo.StartsWith("  Hello") ? "true" : "false"); AppHost.WriteString("\n");
            AppHost.WriteString("\n");
        }

        private static void Line(string logo, string info)
        {
            AppHost.WriteString(logo);
            AppHost.WriteString(info);
            AppHost.WriteString("\n");
        }

        private static void LineBuild(string logo)
        {
            AppHost.WriteString(logo);
            AppHost.WriteString("  Build: ");
            AppHost.WriteBuildId();
            AppHost.WriteString("\n");
        }

        private static void LineAbi(string logo)
        {
            AppHost.WriteString(logo);
            AppHost.WriteString("  ABI:   ");
            AppHost.WriteUInt(AppHost.GetAbiVersion());
            AppHost.WriteString("\n");
        }

        private static void LineImage(string logo, AppStartupBlock* startup)
        {
            AppHost.WriteString(logo);
            if (startup != null)
            {
                AppHost.WriteString("  Image: ");
                AppHost.WriteHex(startup->ImageBase);
                AppHost.WriteString(" - ");
                AppHost.WriteHex(startup->ImageEnd);
            }
            AppHost.WriteString("\n");
        }

        private static void LineStack(string logo, AppStartupBlock* startup)
        {
            AppHost.WriteString(logo);
            if (startup != null)
            {
                AppHost.WriteString("  Stack: ");
                AppHost.WriteHex(startup->StackBase);
                AppHost.WriteString(" - ");
                AppHost.WriteHex(startup->StackTop);
            }
            AppHost.WriteString("\n");
        }
    }
}
