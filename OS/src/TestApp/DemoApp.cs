using Console = OS.Hal.Console;
using OS.Hal;
using OS.Kernel.Memory;
using OS.Kernel.Util;

namespace OS.TestApp
{
    internal static unsafe class DemoApp
    {
        public static void Run()
        {
            Log.Write(LogLevel.Info, "demo start");

            for (int i = 0; i < 8; i++)
            {
                Console.Write("fib(");
                Console.WriteInt(i);
                Console.Write(")=");
                Console.WriteInt(Fib(i));
                Console.WriteLine("");
            }

            RunHeapTest();
            Log.Write(LogLevel.Info, "demo done");
        }

        private static int Fib(int n)
        {
            if (n <= 1)
                return n;

            int a = 0;
            int b = 1;
            for (int i = 2; i <= n; i++)
            {
                int next = a + b;
                a = b;
                b = next;
            }

            return b;
        }

        private static void RunHeapTest()
        {
            Log.Write(LogLevel.Info, "demo heap test start");

            void* block16 = KernelHeap.Alloc(16);
            PrintPointer("alloc 16", block16);

            void* block64 = KernelHeap.Alloc(64);
            PrintPointer("alloc 64", block64);

            if (block64 != null)
            {
                Memory.Zero(block64, 64);
                byte* bytes = (byte*)block64;
                for (uint i = 0; i < 64; i++)
                    bytes[i] = (byte)(i + 1);

                uint checksum = 0;
                for (uint i = 0; i < 64; i++)
                    checksum += bytes[i];

                Log.Begin(LogLevel.Info);
                Console.Write("heap checksum: ");
                Console.WriteUInt(checksum);
                Log.EndLine();
            }

            if (block16 != null)
            {
                KernelHeap.Free(block16);
                Log.Write(LogLevel.Info, "free 16");
            }

            void* block8 = KernelHeap.Alloc(8);
            PrintPointer("alloc 8", block8);

            if (block8 != null)
                KernelHeap.Free(block8);

            if (block64 != null)
                KernelHeap.Free(block64);

            Log.Write(LogLevel.Info, "demo heap test ok");
        }

        private static void PrintPointer(string label, void* pointer)
        {
            Log.Begin(LogLevel.Info);
            Console.Write(label);
            Console.Write(" -> ");

            if (pointer == null)
            {
                Console.Write("none");
            }
            else
            {
                Console.Write("0x");
                Console.WriteHex((ulong)pointer, 8);
            }

            Log.EndLine();
        }
    }
}
