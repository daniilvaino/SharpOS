using System.Runtime;

namespace OS.PAL.SharpOSHost
{
    // Authenticode helper policy (wintrust.dll). The fork already answers
    // WinVerifyTrust with TRUST_E_NOSIGNATURE — "this file carries no
    // signature" — which is the truth on a unikernel with no certificate
    // store and no clock-verified trust chain.
    //
    // PowerShell does not stop there: after the verdict it asks for the
    // provider data behind the state handle to describe *why*, and that is
    // where importing PSReadLine died — not on the verdict, but on
    // WTHelperProvDataFromStateData not existing at all, so the P/Invoke
    // failed to resolve its entry point.
    //
    // There is nothing to describe: no verification ran, so there is no
    // provider data, no signer and no certificate. All three helpers return
    // null, which is what the real ones do when a handle carries no state.
    internal static unsafe class AuthenticodePolicy
    {
        // CRYPT_PROVIDER_DATA* WTHelperProvDataFromStateData(HANDLE hStateData)
        [RuntimeExport("SharpOSHost_WTHelperProvDataFromStateData")]
        public static void* ProvDataFromStateData(void* hStateData) => null;

        // CRYPT_PROVIDER_SGNR* WTHelperGetProvSignerFromChain(
        //     CRYPT_PROVIDER_DATA*, DWORD idxSigner, BOOL fCounterSigner, DWORD idxCounterSigner)
        [RuntimeExport("SharpOSHost_WTHelperGetProvSignerFromChain")]
        public static void* GetProvSignerFromChain(void* provData, uint idxSigner,
                                                   int fCounterSigner, uint idxCounterSigner) => null;

        // CRYPT_PROVIDER_CERT* WTHelperGetProvCertFromChain(CRYPT_PROVIDER_SGNR*, DWORD idxCert)
        [RuntimeExport("SharpOSHost_WTHelperGetProvCertFromChain")]
        public static void* GetProvCertFromChain(void* signer, uint idxCert) => null;
    }
}
