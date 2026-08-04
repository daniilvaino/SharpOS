using System.Runtime;

namespace OS.PAL.SharpOSHost
{
    // Software Restriction Policy (SAFER) policy. There is no SRP on a
    // unikernel: no Group Policy, no machine scope, no restricted tokens.
    //
    // The subtle part is how "no restriction" is expressed. SaferComputeTokenFromLevel
    // reports its verdict through the *output token*, not the return value:
    //   - success + NULL token  => the code runs unrestricted
    //   - success + real token  => the code must run under that restricted token
    // PowerShell reads exactly that: a non-null token means restricted, which it
    // surfaces as "blocked by software restriction policies". The old shim handed
    // back a non-null sentinel meaning it as "call succeeded", and PS dutifully
    // refused to load PSReadLine's format file.
    internal static unsafe class SaferPolicy
    {
        // BOOL SaferComputeTokenFromLevel(SAFER_LEVEL_HANDLE, HANDLE inToken,
        //                                 PHANDLE outToken, DWORD flags, LPVOID reserved)
        [RuntimeExport("SharpOSHost_SaferComputeTokenFromLevel")]
        public static int ComputeTokenFromLevel(void** outAccessToken)
        {
            if (outAccessToken != null)
                *outAccessToken = null;   // unrestricted
            return 1;                     // TRUE
        }
    }
}
