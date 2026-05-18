using OS.Hal;

namespace OS.Kernel.Diagnostics
{
    // Phase B regression oracle for the own 16550 UART (COM1) driver.
    // Status line goes via Console (UEFI ConOut mirror — always visible).
    // The proof line goes via the OWN Serial driver straight to the
    // 16550. Pre-ExitBootServices both reach the same physical COM1, so
    // if BOTH lines show in the captured serial, the own-driver path is
    // confirmed working while UEFI is still active — i.e. the post-EBS
    // diagnostic substrate is ready (prerequisite for Phase D / off-ramp).
    internal static class SerialProbe
    {
        public static void Run()
        {
            bool present = Serial.Init();
            Console.WriteLine(present
                ? "[serial] COM1 own 16550: PRESENT (loopback OK)"
                : "[serial] COM1 own 16550: ABSENT (no chip / -serial none)");
            if (present)
                Serial.WriteString("[serial] direct-UART line via own 16550 driver - Phase B OK\n");

            // Phase B#2 sub-step 1 — verify GOP framebuffer was captured
            // (UefiGop.TryCapture, Boot Services side). Headless-checkable
            // by the numbers; map+renderer is the next sub-step.
            var bi = Platform.GetBootInfo();
            if (bi.GraphicsAvailable != 0)
            {
                Console.Write("[gop] ");
                Console.WriteInt((int)bi.FramebufferWidth);
                Console.Write("x");
                Console.WriteInt((int)bi.FramebufferHeight);
                Console.Write(" base=0x");
                Console.WriteHex(bi.FramebufferBase);
                Console.Write(" size=0x");
                Console.WriteHex(bi.FramebufferSize);
                Console.Write(" stride=");
                Console.WriteInt((int)bi.FramebufferStride);
                Console.Write(" fmt=");
                Console.WriteInt((int)bi.FramebufferPixelFormat);
                Console.WriteLine("");
            }
            else
            {
                Console.WriteLine("[gop] none (GraphicsAvailable=0)");
            }

            // Phase B#2 sub-step 2 — prove the FB MMIO mapping is live:
            // write a sentinel pixel and read it back through the same
            // kernel VA (headless-verifiable oracle — px0 must equal the
            // packed sentinel). Also paint a 64x64 magenta square at the
            // origin: harmless eyeball proof under SHARPOS_GUI=1 (the
            // renderer clears the screen in a later sub-step anyway).
            if (Framebuffer.IsAvailable)
            {
                Framebuffer.PutPixel(0, 0, 0x12, 0x34, 0x56);
                uint px0 = Framebuffer.GetPixelRaw(0, 0);
                for (uint yy = 0; yy < 64; yy++)
                    for (uint xx = 0; xx < 64; xx++)
                        Framebuffer.PutPixel(xx, yy, 0xFF, 0x00, 0xFF);
                Console.Write("[fb] map+rw OK va=0x");
                Console.WriteHex(Framebuffer.BaseAddress);
                Console.Write(" px0=0x");
                Console.WriteHex(px0);
                Console.WriteLine("");
            }
            else
            {
                Console.WriteLine("[fb] not mapped");
            }
        }
    }
}
