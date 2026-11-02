// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;

namespace System.Text.RegularExpressions
{
    /// <summary>
    /// An exception as a result of a parse error in a regular expression <see cref="RegularExpressions"/>, with
    /// detailed information in the <see cref="Error"/> and <see cref="Offset"/> properties.
    /// </summary>
    // SharpOS cut: [Serializable] — no legacy formatter serialization.
#if SYSTEM_TEXT_REGULAREXPRESSIONS
    public
#else
    internal
#endif
    sealed class RegexParseException : ArgumentException
    {
        /// <summary>Gets the error that happened during parsing.</summary>
        public RegexParseError Error { get; }

        /// <summary>Gets the zero-based character offset in the regular expression pattern where the parse error occurs.</summary>
        public int Offset { get; }

        // No need for a serialization ctor: we swap the active type during serialization.

        internal RegexParseException(RegexParseError error, int offset, string message) : base(message)
        {
            Error = error;
            Offset = offset;
        }

        // SharpOS cut: GetObjectData override (serialization as ArgumentException).
    }
}
