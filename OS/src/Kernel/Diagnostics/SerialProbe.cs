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
        }
    }
}
