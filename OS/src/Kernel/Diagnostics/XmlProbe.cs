using System;
using OS.Hal;
using OS.Kernel.Pe;

namespace OS.Kernel.Diagnostics
{
    /// <summary>
    /// Runs the vendored XML parser against a manifest and checks what it read.
    /// </summary>
    /// <remarks>
    /// The parser scans with vector instructions, so this is also the first
    /// consumer of the vector types outside a probe written to test them. The
    /// sample is deliberately longer than one vector wide and carries a comment
    /// and an attribute whose value contains a character the scanner stops on,
    /// so the SIMD path is entered and left more than once.
    /// </remarks>
    internal static class XmlProbe
    {
        // A const, not a static field: a static string field would give this
        // type a class constructor (limits §1).
        private const string Sample =
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n"
            + "<assembly xmlns=\"urn:schemas-microsoft-com:asm.v1\" manifestVersion=\"1.0\">\n"
            + "  <!-- the stock half of the manifest stays exactly as it was -->\n"
            + "  <assemblyIdentity type=\"win32\" name=\"ProbeApp\" version=\"1.0.0.0\" processorArchitecture=\"amd64\" />\n"
            + "  <trustInfo xmlns=\"urn:schemas-microsoft-com:asm.v2\">\n"
            + "    <security>\n"
            + "      <requestedPrivileges xmlns=\"urn:schemas-microsoft-com:asm.v3\">\n"
            + "        <requestedExecutionLevel level=\"asInvoker\" uiAccess=\"false\" />\n"
            + "      </requestedPrivileges>\n"
            + "    </security>\n"
            + "  </trustInfo>\n"
            + "  <sharpos xmlns=\"urn:sharpos:app.v1\">\n"
            + "    <app schema=\"1\" abi=\"3\" serviceAbi=\"0\" />\n"
            + "  </sharpos>\n"
            + "</assembly>\n";

        public static void Run()
        {
            ReportVectorArithmetic();

            SharpAppManifest manifest = SharpAppManifest.Parse(Sample);

            bool ok = manifest.Found
                && manifest.Schema == 1
                && manifest.Abi == 3
                && manifest.ServiceAbi == 0
                && manifest.Name == "ProbeApp";

            Log.Begin(ok ? LogLevel.Info : LogLevel.Warn);
            Console.Write("[xml] manifest schema=");
            Console.WriteUInt(manifest.Schema);
            Console.Write(" abi=");
            Console.WriteUInt(manifest.Abi);
            Console.Write(" serviceAbi=");
            Console.WriteUInt(manifest.ServiceAbi);
            Console.Write(" name=");
            Console.Write(manifest.Name ?? "(none)");

            if (manifest.Error != null)
            {
                Console.Write(" error=");
                Console.Write(manifest.Error);
            }

            Console.Write(ok ? " PASS" : " FAIL");
            Log.EndLine();

            ReportMalformed();
        }

        /// <summary>
        /// The parser subtracts and complements vectors, not just compares
        /// them, and those operators have no software fallback.
        /// </summary>
        /// <remarks>
        /// Asked before the parse and with the answer caught, because an
        /// operator ILC did not take over throws — and a throw from inside the
        /// parser would arrive as "the manifest failed", which is the wrong
        /// diagnosis entirely.
        /// </remarks>
        private static void ReportVectorArithmetic()
        {
            bool subtractOk;
            bool complementOk;
            string detail = "";

            try
            {
                var five = System.Runtime.Intrinsics.Vector128.Create((ushort)5);
                var three = System.Runtime.Intrinsics.Vector128.Create((ushort)3);
                var two = System.Runtime.Intrinsics.Vector128.Create((ushort)2);

                subtractOk = (five - three) == two;
                complementOk = ~System.Runtime.Intrinsics.Vector128<ushort>.Zero
                    == System.Runtime.Intrinsics.Vector128<ushort>.AllBitsSet;
            }
            catch (Exception e)
            {
                subtractOk = false;
                complementOk = false;
                detail = e.Message;
            }

            bool ok = subtractOk && complementOk;

            Log.Begin(ok ? LogLevel.Info : LogLevel.Warn);
            Console.Write("[xml] vector arith sub=");
            Console.Write(subtractOk ? "1" : "0");
            Console.Write(" not=");
            Console.Write(complementOk ? "1" : "0");
            if (detail.Length != 0)
            {
                Console.Write(" threw=");
                Console.Write(detail);
            }
            Console.Write(ok ? " PASS" : " FAIL");
            Log.EndLine();
        }

        /// <summary>
        /// A manifest that does not parse must fail loudly, not quietly return
        /// zeros — the launch path refuses on that difference.
        /// </summary>
        private static void ReportMalformed()
        {
            SharpAppManifest broken = SharpAppManifest.Parse(
                "<assembly><sharpos xmlns=\"urn:sharpos:app.v1\"><app schema=\"1\"");

            bool ok = !broken.Found && broken.Error != null;

            Log.Begin(ok ? LogLevel.Info : LogLevel.Warn);
            Console.Write("[xml] malformed refused=");
            Console.Write(!broken.Found ? "1" : "0");
            Console.Write(" reported=");
            Console.Write(broken.Error != null ? "1" : "0");
            Console.Write(ok ? " PASS" : " FAIL");
            Log.EndLine();
        }
    }
}
