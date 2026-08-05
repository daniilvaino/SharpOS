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
using static Iced.Intel.AssemblerRegisters;

namespace OS.Kernel.Memory
{
    internal static unsafe partial class InterfaceDispatchPatcher
    {
        [CompileTimeAsm]
        public static partial int Emit(byte* dst, byte* shellcode);

        // 14 bytes: an absolute indirect jump through a data slot that
        // follows it:
        //
        //     FF 25 00 00 00 00      jmp qword ptr [rip+0]
        //     <8-byte target>        patched at install time
        //
        // This replaced a 5-byte `jmp rel32` (step148). rel32 only reaches
        // +/-2 GiB, and on real hardware the firmware loaded the image at
        // ~6.4 GB while handing out the EfiLoaderCode pool at ~2.3 GB — over
        // 3 GB apart, so the patch refused to install and the first interface
        // dispatch panicked. Distance is the firmware's choice, so the jump
        // must not depend on it.
        //
        // The indirect form also clobbers no register, which matters here:
        // the caller arrives with the dispatch cell and `this` already in
        // place, and the bridge shellcode expects them untouched.
        [CompileTimeAsmBody(nameof(Emit))]
        private static void Emit_Body(Iced.Intel.Assembler a, BootAsm.HoleCollector h)
        {
            var slot = a.CreateLabel();
            // RIP-relative through the slot below, with the encoder computing
            // the displacement — writing the bytes by hand would hard-code an
            // assumption that nothing ever lands between the jump and the slot.
            a.jmp(__qword_ptr[slot]);
            h.DataSlotHole(a, ref slot, "shellcode");
        }
    }
}
