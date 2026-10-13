namespace PipeApps
{
    // --from cborseq: a CBOR Sequence only — a map per item, a top-level array taken for one item (step206).
    internal sealed class FromCborseq : FromCbor
    {
        public override string Format => "cborseq";
        protected override bool SequenceOnly => true;
    }
}
