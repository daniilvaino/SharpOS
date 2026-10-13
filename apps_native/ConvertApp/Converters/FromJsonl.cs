namespace PipeApps
{
    // --from jsonl: JSON Lines only — an object per value, a top-level array refused (step206).
    internal sealed class FromJsonl : FromJson
    {
        public override string Format => "jsonl";
        protected override bool SequenceOnly => true;
    }
}
