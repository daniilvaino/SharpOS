// Ported from dotnet/runtime release/8.0 (v8.0.27, MIT):
//   src/libraries/System.Private.CoreLib/src/System/IUtfChar.cs
//
// SharpOS cut: the `IBinaryInteger<TSelf>` base interface. The generic-math
// interface family (IBinaryInteger, INumber, the operator interfaces, ...) is
// not in this std, and the number formatter - the only user of IUtfChar -
// calls nothing but the CastFrom/CastToUInt32 statics declared here. Char and
// Byte implement the interface (Number/Primitives.Formatting.cs).

namespace System
{
    // NOTE: This is a workaround for current inlining limitations of some backend code generators.
    // We would prefer to not have this interface at all and instead just use TChar.CreateTruncuating.
    // Once inlining is improved on these hot code paths in formatting, we can remove this interface.

    /// <summary>Internal interface used to unify char and byte in formatting operations.</summary>
    internal interface IUtfChar<TSelf> // SharpOS cut: `: IBinaryInteger<TSelf>` - see header.
        where TSelf : unmanaged, IUtfChar<TSelf>
    {
        /// <summary>Casts the specified value to this type.</summary>
        public static abstract TSelf CastFrom(byte value);

        /// <summary>Casts the specified value to this type.</summary>
        public static abstract TSelf CastFrom(char value);

        /// <summary>Casts the specified value to this type.</summary>
        public static abstract TSelf CastFrom(int value);

        /// <summary>Casts the specified value to this type.</summary>
        public static abstract TSelf CastFrom(uint value);

        /// <summary>Casts the specified value to this type.</summary>
        public static abstract TSelf CastFrom(ulong value);

        /// <summary>Casts a value of this type to an UInt32.</summary>
        public static abstract uint CastToUInt32(TSelf value);
    }
}
