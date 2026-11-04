using System.Runtime.CompilerServices;

namespace OS.Hal.Audio.Hda
{
    /// <summary>
    /// A window onto the controller's MMIO registers (SerenityOS's IOWindow,
    /// reduced to what the driver uses). Reads are NoInlining: a poll loop on
    /// a register nothing in managed code writes would otherwise be read once
    /// and hoisted (docs: AOT hoists MMIO polls).
    /// </summary>
    internal readonly unsafe struct HdaRegisters
    {
        private readonly ulong _base;

        public HdaRegisters(ulong address) => _base = address;

        public HdaRegisters Offset(ulong offset) => new HdaRegisters(_base + offset);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public byte Read8(ulong offset) => *(byte*)(_base + offset);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public ushort Read16(ulong offset) => *(ushort*)(_base + offset);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public uint Read32(ulong offset) => *(uint*)(_base + offset);

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Write8(ulong offset, byte value) => *(byte*)(_base + offset) = value;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Write16(ulong offset, ushort value) => *(ushort*)(_base + offset) = value;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Write32(ulong offset, uint value) => *(uint*)(_base + offset) = value;
    }

    // Timing.h: the link runs 48 kHz frames whatever the streams' rates.
    internal static class HdaTiming
    {
        public const uint ControllerTimeoutMicroseconds = 300_000;

        public static uint FrameDelayMicroseconds(uint frames) => frames * 1_000_000u / 48_000u + 1u;

        /// <summary>Busy-waits about <paramref name="microseconds"/> by HPET; no-op without one.</summary>
        public static void Delay(uint microseconds)
        {
            if (!OS.Hal.Timer.Hpet.IsInitialized) return;
            ulong end = OS.Hal.Timer.Hpet.ReadCounter() + OS.Hal.Timer.Hpet.FrequencyHz * microseconds / 1_000_000UL;
            for (uint spin = 0; spin < 100_000_000u && OS.Hal.Timer.Hpet.ReadCounter() < end; spin++) { }
        }

        /// <summary>Deadline in HPET ticks, 0 without a clock (then only the spin cap ends waits).</summary>
        public static ulong Deadline(uint microseconds)
            => OS.Hal.Timer.Hpet.IsInitialized
                ? OS.Hal.Timer.Hpet.ReadCounter() + OS.Hal.Timer.Hpet.FrequencyHz * microseconds / 1_000_000UL
                : 0;

        public static bool Expired(ulong deadline, ref uint spins)
            => ++spins > 50_000_000u || (deadline != 0 && OS.Hal.Timer.Hpet.ReadCounter() >= deadline);
    }
}
