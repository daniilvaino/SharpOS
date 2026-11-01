// Encoding.CodePage and Encoding.Default (step223, the archive ports: zip
// records whether a name is UTF-8 against the encoding's code page).
// Default is UTF-8 without a BOM, as on .NET Core and later.

namespace System.Text
{
    public abstract partial class Encoding
    {
        public static Encoding Default => new UTF8Encoding(false);

        public virtual int CodePage => this switch
        {
            UTF8Encoding => 65001,
            UnicodeEncoding u => u._bigEndian ? 1201 : 1200,
            UTF32Encoding u => u._bigEndian ? 12001 : 12000,
            ASCIIEncoding => 20127,
            Latin1Encoding => 28591,
            _ => 0,
        };
    }
}
