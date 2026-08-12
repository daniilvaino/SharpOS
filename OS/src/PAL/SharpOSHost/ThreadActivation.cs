using System.Runtime;
using OS.Kernel.Threading;

namespace OS.PAL.SharpOSHost
{
    // Thread activation — how CoreCLR stops a thread it does not own.
    //
    // The runtime does NOT use OS thread suspension here: our fork defines
    // TARGET_UNIX, which turns on DISABLE_THREADSUSPEND, so Thread::SuspendThread
    // is compiled out entirely. (GetThreadContext and ResumeThread being stubs
    // in the PAL is therefore beside the point — nothing calls them.)
    //
    // What it uses instead is activation injection. On Unix that is a signal:
    // interrupt the thread wherever it is, hand the runtime a Windows-style
    // CONTEXT of the interruption point, and let it decide what to do. In the
    // fork that path ended at
    //
    //     #elif defined(TARGET_SHARPOS)
    //         // thread activation injection — Phase 6.2 task
    //         return false;
    //
    // which is why the garbage collector believed the world had stopped while
    // a thread kept mutating the heap.
    //
    // We have no signals, but we have something better suited: our own timer
    // interrupt, whose handler already runs on the interrupted thread's stack
    // with a full register frame. "Interrupt a thread and run something on it"
    // is exactly that frame plus a call.
    //
    // Contract (pal.h):
    //     typedef void (*PAL_ActivationFunction)(CONTEXT *context);
    //     typedef BOOL (*PAL_SafeActivationCheckFunction)(SIZE_T ip);
    //
    // The activation function runs ON the interrupted thread and BLOCKS —
    // HandleSuspensionForInterruptedThread pushes a frame and calls
    // PulseGCMode, which waits for the collection to finish. That means it can
    // only be called with interrupts enabled and the scheduler free to run
    // other threads, or the collection it waits for could never happen.
    internal static unsafe class ThreadActivation
    {
        private static delegate* unmanaged<Context*, void> s_activation;
        private static delegate* unmanaged<nuint, int> s_safeCheck;

        private static ulong s_injected;
        private static ulong s_delivered;
        private static ulong s_notSafe;

        public static bool IsRegistered => s_activation != null;
        public static ulong Injected => s_injected;
        public static ulong Delivered => s_delivered;
        public static ulong NotSafe => s_notSafe;

        [RuntimeExport("SharpOSHost_SetActivationFunction")]
        public static void SetActivationFunction(void* activation, void* safeCheck)
        {
            s_activation = (delegate* unmanaged<Context*, void>)activation;
            s_safeCheck = (delegate* unmanaged<nuint, int>)safeCheck;
        }

        /// <summary>
        /// Request that a thread be interrupted. Returns TRUE if the request
        /// was accepted; delivery happens on the next timer tick that finds
        /// that thread running at a safe point.
        /// </summary>
        /// <remarks>
        /// Asynchronous by nature, and the runtime expects that: it sets
        /// m_hasPendingActivation and carries on, re-injecting later if the
        /// thread has still not reached a safe point.
        /// </remarks>
        [RuntimeExport("SharpOSHost_InjectActivation")]
        public static int InjectActivation(ulong hThread)
        {
            if (s_activation == null) return 0;

            Thread? target = HandleTable.LookupAs<Thread>(hThread);
            if (target == null) return 0;

            target.ActivationPending = true;
            s_injected++;
            return 1;
        }

        /// <summary>
        /// Called from the timer interrupt. Delivers a pending activation to
        /// the thread that was interrupted, if it is standing somewhere the
        /// runtime considers safe.
        /// </summary>
        public static void OnTick(void* frame)
        {
            if (s_activation == null || frame == null) return;

            Thread? curr = Scheduler.Current;
            if (curr == null || !curr.ActivationPending) return;

            ulong* f = (ulong*)frame;
            ulong rip = f[18];
            if (rip == 0) return;

            // Ask before acting. The runtime knows which instructions it can
            // be interrupted at; guessing would corrupt exactly the state this
            // exists to protect. A "no" leaves the request pending — the
            // runtime re-injects, and a later tick catches the thread
            // somewhere safe.
            if (s_safeCheck != null && s_safeCheck((nuint)rip) == 0)
            {
                s_notSafe++;
                return;
            }

            curr.ActivationPending = false;
            s_delivered++;

            Context ctx = default;
            FillContext(&ctx, f);

            // Interrupts on for the call: the handler waits for a collection
            // that runs on another thread, so the scheduler has to be able to
            // move. Off again before returning through the frame.
            OS.Hal.X64Asm.Sti();
            s_activation(&ctx);
            OS.Hal.X64Asm.Cli();

            // Write back: the handler is allowed to redirect the thread by
            // editing the context it was given.
            ApplyContext(&ctx, f);
        }

        // InterruptFrame layout, same one X64Asm.TryResumeFrame reads:
        //   0 Cr2, 1 Rax, 2 Rcx, 3 Rdx, 4 Rbx, 5 Rsi, 6 Rdi, 7 Rbp,
        //   8..15 R8..R15, 16 Vector, 17 Err, 18 Rip, 19 Cs, 20 Rflags,
        //   21 Rsp, 22 Ss
        private static void FillContext(Context* ctx, ulong* f)
        {
            ctx->Rax = f[1];  ctx->Rcx = f[2];  ctx->Rdx = f[3];  ctx->Rbx = f[4];
            ctx->Rsi = f[5];  ctx->Rdi = f[6];  ctx->Rbp = f[7];
            ctx->R8  = f[8];  ctx->R9  = f[9];  ctx->R10 = f[10]; ctx->R11 = f[11];
            ctx->R12 = f[12]; ctx->R13 = f[13]; ctx->R14 = f[14]; ctx->R15 = f[15];
            ctx->Rip = f[18];
            ctx->Rsp = f[21];
            ctx->EFlags = (uint)f[20];
            ctx->SegCs = (ushort)f[19];
            ctx->SegSs = (ushort)f[22];

            // Only the areas actually filled are advertised. Claiming
            // CONTEXT_FLOATING_POINT over a zeroed FltSave is what cleared
            // MXCSR and produced #XM on VirtualBox in step152 — zeros are not
            // "no opinion", they are an opinion.
            ctx->ContextFlags = Context.CONTEXT_CONTROL | Context.CONTEXT_INTEGER;
        }

        private static void ApplyContext(Context* ctx, ulong* f)
        {
            f[1] = ctx->Rax;  f[2] = ctx->Rcx;  f[3] = ctx->Rdx;  f[4] = ctx->Rbx;
            f[5] = ctx->Rsi;  f[6] = ctx->Rdi;  f[7] = ctx->Rbp;
            f[8] = ctx->R8;   f[9] = ctx->R9;   f[10] = ctx->R10; f[11] = ctx->R11;
            f[12] = ctx->R12; f[13] = ctx->R13; f[14] = ctx->R14; f[15] = ctx->R15;
            f[18] = ctx->Rip;
            f[21] = ctx->Rsp;
            f[20] = ctx->EFlags;
        }
    }
}
