// step 118 Wave 1 — compile-time codegen migration of
// InterfaceDispatchPatcher. First real-world use of the M6.1 RelHole
// mechanism: `JMP rel32` with displacement patched at runtime from a
// `byte*` target parameter. Walker emits 5 bytes [E9 00 00 00 00] via
// Iced's db() (raw byte insertion), generator emits a patch line that
// computes `target - (dst + 1 + 4)` at install time.
//
// Body signature has a second `byte*` parameter — the rel32 target. The
// partial declaration in InterfaceDispatchPatcher.cs … wait, it's a
// method-on-class-with-state pattern; we keep TryInstall as is and just
// expose Emit(byte* dst, byte* shellcode). The generator pairs them by
// the [CompileTimeAsmBody(nameof(Emit))] attribute on the body method.

using BootAsm;

namespace OS.Kernel.Memory
{
    internal static unsafe partial class InterfaceDispatchPatcher
    {
        [CompileTimeAsm]
        public static partial int Emit(byte* dst, byte* shellcode);

        // 5 bytes: JMP rel32 to the bridge shellcode start. The walker
        // emits `[0xE9, ord_lo, ord_hi, 0xAD, 0xDE]` via a.db() — Iced
        // copies them verbatim. After Assemble the sentinel scan finds
        // them, zeroes the 4-byte disp slot, and the generator burns a
        // patch line `*(int*)(dst + 1) = (int)((long)shellcode - ((long)dst + 1 + 4));`.
        [CompileTimeAsmBody(nameof(Emit))]
        private static void Emit_Body(Iced.Intel.Assembler a, BootAsm.HoleCollector h)
        {
            h.JmpRelHole(a, "shellcode");
        }
    }
}
