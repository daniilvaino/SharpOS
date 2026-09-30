// System.Buffers.SpanAction<T, TArg> — the callback string.Create and its
// relatives hand a buffer to.
//
// Declared rather than ported: it is one line in the BCL too. Here it is used
// by System.Formats.Cbor, whose indefinite-length string reader fills a buffer
// through one of these.

namespace System.Buffers
{
    public delegate void SpanAction<T, in TArg>(Span<T> span, TArg arg);
}
