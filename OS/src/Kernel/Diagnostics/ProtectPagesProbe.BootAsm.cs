// The probe's payload: a function that returns a known constant, written into
// a page that starts non-executable. The probe then asks VirtualProtect to make
// the page executable and calls it — the value coming back is the proof.
//
// Emitted through BootAsm like everything else that is x64 machine code. It is
// four instructions' worth of bytes, which is exactly the size at which "just
// write them here" is tempting and wrong: the rule is that the generator and
// Iced are the only place instructions are described.

using BootAsm;
using static Iced.Intel.AssemblerRegisters;

namespace OS.Kernel.Diagnostics
{
    internal static unsafe partial class ProtectPagesProbe
    {
        /// <summary>Marker the emitted function returns; checked by the caller.</summary>
        public const int MarkerValue = 0x5A5A;

        [CompileTimeAsm]
        private static partial int EmitMarkerFunction(byte* dst);

        [CompileTimeAsmBody(nameof(EmitMarkerFunction))]
        private static void EmitMarkerFunction_Body(Iced.Intel.Assembler a, BootAsm.HoleCollector h)
        {
            a.mov(eax, 0x5A5A);   // literal: the walker folds literals, not named consts
            a.ret();
        }
    }
}
