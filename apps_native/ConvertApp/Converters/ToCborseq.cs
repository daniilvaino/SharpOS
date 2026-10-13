namespace PipeApps
{
    // --to cborseq: a CBOR Sequence (RFC 8742) — ToCbor's maps one after another, no tag, no array (step206).
    internal sealed class ToCborseq : ToCbor
    {
        public override string Format => "cborseq";
        protected override bool Sequence => true;
    }
}
