using System.Runtime;
using System.Runtime.InteropServices;

namespace OS.PAL.SharpOSHost
{
    // step126.8 — kernel-side policy for Thread.SetApartmentState/
    // GetApartmentState. PowerShell's Main is decorated [STAThread];
    // CoreCLR runtime invokes Thread.CurrentThread.SetApartmentState(STA)
    // before calling Main. Without a QCALL target the indirect call would
    // dereference NULL (#PF on RIP=0).
    //
    // Per SharpOS invariant the policy ("what state to accept", "what to
    // return", "side-effects") lives in managed C# here. The fork's QCALL
    // entry in qcallentrypoints.cpp is a thin shim that just routes.
    //
    // On unikernel COM apartment model is meaningless (no other processes
    // to marshal calls to/from). We accept any requested state silently
    // and report back what was requested — PowerShell continues thinking
    // it's in STA, never actually exercises COM marshalling, and reaches
    // the prompt. If someone later writes `New-Object -ComObject Foo`
    // they'll get a clean PlatformNotSupportedException further down the
    // CoCreateInstance path.
    internal static unsafe class ThreadApartment
    {
        // .NET ApartmentState enum (System.Threading.ApartmentState):
        //   STA = 0, MTA = 1, Unknown = 2
        public const int STA     = 0;
        public const int MTA     = 1;
        public const int Unknown = 2;

        // Currently advertised state. Last-write-wins — PowerShell sets
        // it once during runtime entry, and may query it back later from
        // diagnostics / Thread.GetApartmentState().
        private static int s_currentState = Unknown;

        [RuntimeExport("SharpOSHost_ThreadSetApartmentState")]
        public static int SetApartmentState(int state)
        {
            // Validate range (defensive — managed caller already enum-bound).
            if (state == STA || state == MTA || state == Unknown)
                s_currentState = state;
            // Return whatever the caller asked for so they think it stuck.
            // (.NET contract is: SetApartmentState returns the state that
            // was actually set, which may differ from requested if the
            // thread was already in a different apartment. We accept all.)
            return state;
        }

        [RuntimeExport("SharpOSHost_ThreadGetApartmentState")]
        public static int GetApartmentState()
        {
            return s_currentState;
        }
    }
}
