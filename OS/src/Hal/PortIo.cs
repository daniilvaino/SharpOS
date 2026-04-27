namespace OS.Hal
{
    // x86 port I/O. Wraps the patched shellcode in PortIoStub.
    // Callers must ensure PortIoPatcher.TryInstall() ran successfully —
    // before that, In8/Out8 fall through to the unpatched bodies and
    // panic.
    internal static unsafe class PortIo
    {
        public static byte In8(ushort port)
        {
            delegate* unmanaged<ushort, byte> fn =
                (delegate* unmanaged<ushort, byte>)PortIoStub.GetInbAddress();
            return fn(port);
        }

        public static void Out8(ushort port, byte value)
        {
            delegate* unmanaged<ushort, byte, void> fn =
                (delegate* unmanaged<ushort, byte, void>)PortIoStub.GetOutbAddress();
            fn(port, value);
        }
    }
}
