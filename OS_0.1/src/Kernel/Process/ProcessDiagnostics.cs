using OS.Hal;

namespace OS.Kernel.Process
{
    internal static class ProcessDiagnostics
    {
        public static void DumpSummary(ref ProcessImage processImage)
        {
            Log.Begin(LogLevel.Info);
            Console.Write("stack map: 0x");
            Console.WriteHex(processImage.StackBase, 16);
            Console.Write("..0x");
            Console.WriteHex(processImage.StackMappedTop, 16);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("startup block ready: 0x");
            Console.WriteHex(processImage.StartupBlockVirtual, 16);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("service table ready: 0x");
            Console.WriteHex(processImage.ServiceTableVirtual, 16);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("entry ready: 0x");
            Console.WriteHex(processImage.EntryPoint, 16);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("entry stack top: 0x");
            Console.WriteHex(processImage.StackTop, 16);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("process pages image/stack: ");
            Console.WriteULong(processImage.MappedImagePages);
            Console.Write("/");
            Console.WriteULong(processImage.MappedStackPages);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("process abi version/flags: ");
            Console.WriteUInt(processImage.AbiVersion);
            Console.Write("/");
            Console.WriteUInt(processImage.AbiFlags);
            Log.EndLine();
        }
    }
}
