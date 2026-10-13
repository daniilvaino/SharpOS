namespace PipeApps
{
    // --to jsonl: JSON Lines — ToJson's objects, one per line, no array around them (step206).
    internal sealed class ToJsonl : ToJson
    {
        public override string Format => "jsonl";
        protected override bool Lines => true;
    }
}
