namespace OS.Boot.EH
{
    // What an unhandled exception says on its way out.
    //
    // It used to say one line: "unhandled managed exception — no handler
    // matched", and then panic. Not the type, not the message, not a frame —
    // so every such failure started from scratch, reading raw [seh-step] lines
    // and subtracting an image base by hand to get something a symbolizer
    // would take.
    //
    // None of that was missing for want of information. The first pass already
    // records every frame's IP into the exception object as it searches, and
    // ExceptionHooks has carried an UnhandledHandler slot the whole time. The
    // slot was simply never filled: it defaults to null, and a null hook is
    // silence.
    internal static unsafe class UnhandledExceptionReport
    {
        public static void Install()
        {
            ExceptionHooks.UnhandledHandler = &Report;
        }

        // Runs while the exception is still unwinding, on a stack that may
        // already be damaged: no allocation, no formatting machinery, nothing
        // that can throw. The message is a stored string and the IP array
        // already exists — both are reads.
        private static void Report(System.Exception ex)
        {
            if (ex == null) return;

            OS.Hal.Console.Write("\r\n[unhandled] ");

            // The type, as the address of its MethodTable. There is no
            // reflection here to ask for a name, but that address is a symbol
            // in the image, so the symbolizer turns it into one — the same way
            // the heap census names what it counted.
            //
            // Worth the four lines: three separate crashes this week reported
            // "(no message)" and nothing else, and each time the first
            // question was "an access violation or an argument error?".
            ulong mt = *(ulong*)*(ulong**)System.Runtime.CompilerServices.Unsafe.AsPointer(ref ex);
            OS.Hal.Console.Write("type mt=0x");
            OS.Hal.Console.WriteHex(mt);
            OS.Hal.Console.Write(" ");

            string message = ex.Message;
            OS.Hal.Console.Write(message != null && message.Length != 0
                ? message
                : "(no message)");
            OS.Hal.Console.WriteLine("");

            System.IntPtr[] frames = ex.GetStackIPs();
            if (frames == null || frames.Length == 0)
            {
                OS.Hal.Console.WriteLine("[unhandled] no frames recorded");
                return;
            }

            // Each frame against ITS OWN image, asked the same way the unwinder
            // asks. The first version subtracted the kernel base from every
            // address, which is right only while the whole stack is kernel
            // code: a fault inside an app, loaded at its own base, produced
            // fourteen frames of arithmetic nonsense (rva=0x83456308 for a
            // frame at 0x100034308). Printing a wrong number confidently is
            // worse than printing none.
            for (int i = 0; i < frames.Length; i++)
            {
                ulong ip = (ulong)(long)frames[i];

                OS.Hal.Console.Write("[unhandled]   #");
                OS.Hal.Console.WriteUInt((uint)i);
                OS.Hal.Console.Write(" rip=0x");
                OS.Hal.Console.WriteHex(ip);

                ulong imageBase = 0;
                if (OS.PAL.SharpOSHost.SehUnwind.LookupFunctionEntry(ip, &imageBase) != null
                    && imageBase != 0 && ip >= imageBase)
                {
                    OS.Hal.Console.Write(" ib=0x");
                    OS.Hal.Console.WriteHex(imageBase);
                    OS.Hal.Console.Write(" rva=0x");
                    OS.Hal.Console.WriteHex(ip - imageBase);
                }
                else
                {
                    // No .pdata covers it: JIT output, or a frame the walker
                    // does not know. Say so instead of inventing an offset.
                    OS.Hal.Console.Write(" (no image)");
                }

                OS.Hal.Console.WriteLine("");
            }

            OS.Hal.Console.WriteLine(
                "[unhandled] symbolize each frame against its own ib: "
                + "pwsh tools/symbolize.ps1 -Rip <rip> -ImageBase <ib>");
        }
    }
}
