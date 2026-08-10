// Namespaces that upstream files import but never use — the residue of an IDE
// adding usings faster than anyone removes them.
//
// C# resolves `using` at compile time whether or not a single name comes from
// it, so an unused import of a namespace that does not exist is still a build
// error. Declaring the namespace empty satisfies the import and brings in
// nothing.
//
// If a real name from one of these is ever needed, this file will not help and
// should not be made to: the error will name the type, and that type gets a
// real implementation somewhere honest.

namespace System.Data
{
    // Imported by Fami\Fami.Core\Mappers\MMC1.cs. Nothing from it is used.
}
