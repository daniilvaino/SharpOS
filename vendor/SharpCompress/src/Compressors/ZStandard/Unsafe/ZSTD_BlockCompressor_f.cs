using System.Runtime.InteropServices;

namespace SharpCompress.Compressors.ZStandard.Unsafe;

// SharpOS cut: [UnmanagedFunctionPointer(CallingConvention.Cdecl)] — the delegate is only ever
// called from managed code; the attribute makes ILC emit marshalling stubs, which need
// NativeAOT's InteropHelpers (not ported)
public unsafe delegate nuint ZSTD_BlockCompressor_f(
    ZSTD_MatchState_t* bs,
    SeqStore_t* seqStore,
    uint* rep,
    void* src,
    nuint srcSize
);
