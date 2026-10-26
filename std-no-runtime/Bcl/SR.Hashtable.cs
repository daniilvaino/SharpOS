// Resource strings for the Hashtable port (Hashtable.cs, CompatibleComparer.cs), with the texts of
// dotnet/runtime's System.Private.CoreLib Strings.resx (.NET 10, MIT). Only the keys those files use
// that no other SR partial in std already has.

namespace System
{
    internal static partial class SR
    {
        internal const string Arg_CannotMixComparisonInfrastructure = @"The usage of IKeyComparer and IHashCodeProvider/IComparer interfaces cannot be mixed; use one or the other.";
        internal const string Arg_HTCapacityOverflow = @"Hashtable's capacity overflowed and went negative. Check load factor, capacity and the current size of the table.";
        internal const string Argument_AddingDuplicate__ = @"Item has already been added. Key in dictionary: '{0}'  Key being added: '{1}'";
        internal const string Argument_ImplementIComparable = @"At least one object must implement IComparable.";
        internal const string ArgumentOutOfRange_HashtableLoadFactor = @"Load factor needs to be between 0.1 and 1.0.";
        internal const string InvalidOperation_EnumNotStarted = @"Enumeration has not started. Call MoveNext.";
        internal const string InvalidOperation_EnumOpCantHappen = @"Enumeration has either not started or has already finished.";
        internal const string InvalidOperation_HashInsertFailed = @"Hashtable insert failed. Load factor too high. The most common cause is multiple threads writing to the Hashtable simultaneously.";
    }
}
