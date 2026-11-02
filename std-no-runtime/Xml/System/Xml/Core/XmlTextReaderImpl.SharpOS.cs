// SharpOS's half of XmlTextReaderImpl: what stands in for the DTD machinery
// that is not ported (Schema/DtdParser.cs and the DTD model, SchemaEntity).
// The reader without a DTD is upstream's: DtdProcessing.Prohibit (the
// default) throws at <!DOCTYPE, Ignore skips it, only the predefined and
// character entities resolve. Parse is refused here, when the DOCTYPE is met.

namespace System.Xml
{
    internal sealed partial class XmlTextReaderImpl
    {
        private static IDtdParser CreateDtdParser() =>
            throw new NotSupportedException("DTD processing (DtdProcessing.Parse) is not supported by SharpOS's System.Xml; use Prohibit or Ignore.");

        // An undeclared entity read as empty text, where upstream made a
        // SchemaEntity for it (_disableUndeclaredEntityCheck).
        private sealed class EmptyEntity : IDtdEntityInfo
        {
            public EmptyEntity(string name) => Name = name;

            public string Name { get; }
            public bool IsExternal => false;
            public bool IsDeclaredInExternal => false;
            public bool IsUnparsedEntity => false;
            public bool IsParameterEntity => false;
            public string BaseUriString => string.Empty;
            public string DeclaredUriString => string.Empty;
            public string? SystemId => null;
            public string? PublicId => null;
            public string? Text => string.Empty;
            public int LineNumber => 0;
            public int LinePosition => 0;
        }
    }
}
