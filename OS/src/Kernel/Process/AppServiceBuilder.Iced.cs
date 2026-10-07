// step 115 follow-up #4: Iced-driven thunk emitters for AppServiceBuilder.
// Two thunk shapes (Win64 one-arg / SysV one-arg) emitted via Iced; the
// legacy byte-streams live next to these (see AppServiceBuilder.cs) and
// each TryWrite call parallel-emits both + byte-compares them via
// CompareOrPanic. Isolated in its own partial file so the `using static
// Iced.Intel.AssemblerRegisters` import doesn't shadow anything in
// AppServiceBuilder's hot path. After several green boots the legacy
// emitters + the compare gate get pulled out and only the Iced halves
// remain.

using OS.Hal;
using static Iced.Intel.AssemblerRegisters;

namespace OS.Kernel.Process
{
    internal static unsafe partial class AppServiceBuilder
    {
        private sealed class ThunkBufWriter : Iced.Intel.CodeWriter
        {
            private readonly byte* _p;
            private readonly int _cap;
            private int _i;
            public ThunkBufWriter(byte* p, int capacity) { _p = p; _cap = capacity; _i = 0; }
            public int Count => _i;
            public override void WriteByte(byte value)
            {
                if (_i < _cap) _p[_i++] = value;
            }
        }

        // Windows x64 ABI: arg0 already in rcx; call the target, then leave
        // instead of returning if the calling thread is being ended (step194:
        // a thread of an ending process finishes its service and goes on the
        // way back to its app's code).
        //   mov  rax, target              ; 10   frame: from 14 ...
        //   sub  rsp, 0x28                ;  4
        //   call rax                      ;  2   returns to 16
        //   mov  rcx, &killFlag           ; 10
        //   cmp  byte [rcx], 0            ;  3
        //   jne  leave                    ;  2
        //   add  rsp, 0x28                ;  4   ... to 31
        //   ret                           ;  1
        // leave:                              (36, frame again)
        //   mov  rcx, leaveTarget         ; 10
        //   call rcx                      ;  2   never returns
        // rax (the service's result) is left alone on the normal path.
        private static int EmitWin64OneArgThunkIced(byte* p, int cap, ulong target)
            => EmitServiceThunk(p, cap, target, systemV: false);

        // System V AMD64 ABI: arg0 comes in rdi; translate to rcx first
        // (`mov rcx, rdi`, 3 bytes), then the same as above.
        private static int EmitSystemVOneArgThunkIced(byte* p, int cap, ulong target)
            => EmitServiceThunk(p, cap, target, systemV: true);

        // Where, past the sub, the frame of a thunk ends (the add) and resumes
        // (the leave path): TryUnwindServiceThunk reads these.
        private const uint ThunkFrameEndAfterSub = 21;
        private const uint ThunkLeaveAfterSub = 26;

        private static int EmitServiceThunk(byte* p, int cap, ulong target, bool systemV)
        {
            var a = new Iced.Intel.Assembler(64);
            var leave = a.CreateLabel();
            if (systemV) a.mov(rcx, rdi);
            a.mov(rax, target);
            a.sub(rsp, 0x28);
            a.call(rax);
            a.mov(rcx, (ulong)OS.Kernel.Threading.Scheduler.CurrentKilledAddress);
            a.cmp(__byte_ptr[rcx], 0);
            a.jne(leave);
            a.add(rsp, 0x28);
            a.ret();
            a.Label(ref leave);
            delegate* unmanaged<void> leaveTarget = &LeaveFromService;
            a.mov(rcx, (ulong)leaveTarget);
            a.call(rcx);

            var w = new ThunkBufWriter(p, cap);
            a.Assemble(w, 0);
            return w.Count;
        }

        [System.Runtime.InteropServices.UnmanagedCallersOnly]
        private static void LeaveFromService()
        {
            OS.Kernel.Threading.Thread self = OS.Kernel.Threading.Scheduler.Current;
            OS.Kernel.Exec.JumpStub.LeaveApp(self, self.App?.KillCode ?? OS.Kernel.Exec.JumpStub.UnhandledExitCode);
        }
    }
}
