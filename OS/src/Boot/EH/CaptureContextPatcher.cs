namespace OS.Boot.EH
{
    // Patches CaptureContextStub.CaptureContext body with the byte
    // sequence below. Same pattern as ByRefAssignRefPatcher / PortIoPatcher.
    //
    // The shellcode (53 bytes) snapshots GPRs + IP + RSP into the
    // PAL_LIMITED_CONTEXT pointed to by RCX, then returns.
    //
    //   ; entry: RCX = ctx; [rsp] = return address
    //   48 8B 04 24                mov  rax, [rsp]                ; return address
    //   48 89 01                   mov  [rcx + 0x00], rax         ; ctx->IP
    //   48 8D 44 24 08             lea  rax, [rsp + 8]            ; caller's RSP
    //   48 89 41 08                mov  [rcx + 0x08], rax         ; ctx->Rsp
    //   48 89 69 10                mov  [rcx + 0x10], rbp
    //   48 89 79 18                mov  [rcx + 0x18], rdi
    //   48 89 71 20                mov  [rcx + 0x20], rsi
    //   48 89 41 28                mov  [rcx + 0x28], rax         ; Rax slot — placeholder
    //   48 89 59 30                mov  [rcx + 0x30], rbx
    //   4C 89 61 38                mov  [rcx + 0x38], r12
    //   4C 89 69 40                mov  [rcx + 0x40], r13
    //   4C 89 71 48                mov  [rcx + 0x48], r14
    //   4C 89 79 50                mov  [rcx + 0x50], r15
    //   C3                         ret
    internal static unsafe class CaptureContextPatcher
    {
        private static bool s_installed;

        public static bool IsInstalled => s_installed;

        public static bool TryInstall()
        {
            if (s_installed) return true;

            byte* target = (byte*)CaptureContextStub.GetMethodAddress();
            if (target == null) return false;

            int i = 0;
            // mov rax, [rsp]
            target[i++] = 0x48; target[i++] = 0x8B; target[i++] = 0x04; target[i++] = 0x24;
            // mov [rcx+0x00], rax
            target[i++] = 0x48; target[i++] = 0x89; target[i++] = 0x01;
            // lea rax, [rsp+8]
            target[i++] = 0x48; target[i++] = 0x8D; target[i++] = 0x44; target[i++] = 0x24; target[i++] = 0x08;
            // mov [rcx+0x08], rax
            target[i++] = 0x48; target[i++] = 0x89; target[i++] = 0x41; target[i++] = 0x08;
            // mov [rcx+0x10], rbp
            target[i++] = 0x48; target[i++] = 0x89; target[i++] = 0x69; target[i++] = 0x10;
            // mov [rcx+0x18], rdi
            target[i++] = 0x48; target[i++] = 0x89; target[i++] = 0x79; target[i++] = 0x18;
            // mov [rcx+0x20], rsi
            target[i++] = 0x48; target[i++] = 0x89; target[i++] = 0x71; target[i++] = 0x20;
            // mov [rcx+0x28], rax
            target[i++] = 0x48; target[i++] = 0x89; target[i++] = 0x41; target[i++] = 0x28;
            // mov [rcx+0x30], rbx
            target[i++] = 0x48; target[i++] = 0x89; target[i++] = 0x59; target[i++] = 0x30;
            // mov [rcx+0x38], r12
            target[i++] = 0x4C; target[i++] = 0x89; target[i++] = 0x61; target[i++] = 0x38;
            // mov [rcx+0x40], r13
            target[i++] = 0x4C; target[i++] = 0x89; target[i++] = 0x69; target[i++] = 0x40;
            // mov [rcx+0x48], r14
            target[i++] = 0x4C; target[i++] = 0x89; target[i++] = 0x71; target[i++] = 0x48;
            // mov [rcx+0x50], r15
            target[i++] = 0x4C; target[i++] = 0x89; target[i++] = 0x79; target[i++] = 0x50;
            // ret
            target[i++] = 0xC3;

            // Sanity check first byte and last opcode landed correctly.
            if (target[0] != 0x48 || target[i - 1] != 0xC3)
                return false;

            s_installed = true;
            return true;
        }
    }
}
