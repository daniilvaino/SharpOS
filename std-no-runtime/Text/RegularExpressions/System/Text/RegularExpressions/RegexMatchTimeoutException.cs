// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;

namespace System.Text.RegularExpressions
{
    /// <summary>
    /// This is the exception that is thrown when a RegEx matching timeout occurs.
    /// </summary>
    // SharpOS cut: [Serializable], [TypeForwardedFrom], ISerializable — no legacy formatter serialization.
    public class RegexMatchTimeoutException : TimeoutException
    {
        /// <summary>
        /// Constructs a new RegexMatchTimeoutException.
        /// </summary>
        /// <param name="regexInput">Matching timeout occurred during matching within the specified input.</param>
        /// <param name="regexPattern">Matching timeout occurred during matching to the specified pattern.</param>
        /// <param name="matchTimeout">Matching timeout occurred because matching took longer than the specified timeout.</param>
        public RegexMatchTimeoutException(string regexInput, string regexPattern, TimeSpan matchTimeout)
            : base(SR.RegexMatchTimeoutException_Occurred)
        {
            Input = regexInput;
            Pattern = regexPattern;
            MatchTimeout = matchTimeout;
        }

        /// <summary>
        /// This constructor is provided in compliance with common .NET Framework design patterns;
        /// developers should prefer using the constructor
        /// <code>public RegexMatchTimeoutException(string input, string pattern, TimeSpan matchTimeout)</code>.
        /// </summary>
        public RegexMatchTimeoutException() : base(SR.RegexMatchTimeoutException_Occurred) { }

        /// <summary>
        /// This constructor is provided in compliance with common .NET Framework design patterns;
        /// developers should prefer using the constructor
        /// <code>public RegexMatchTimeoutException(string input, string pattern, TimeSpan matchTimeout)</code>.
        /// </summary>
        /// <param name="message">The error message that explains the reason for the exception.</param>
        public RegexMatchTimeoutException(string message) : base(message ?? SR.RegexMatchTimeoutException_Occurred) { }

        /// <summary>
        /// This constructor is provided in compliance with common .NET Framework design patterns;
        /// developers should prefer using the constructor
        /// <code>public RegexMatchTimeoutException(string input, string pattern, TimeSpan matchTimeout)</code>.
        /// </summary>
        /// <param name="message">The error message that explains the reason for the exception.</param>
        /// <param name="inner">The exception that is the cause of the current exception, or a <code>null</code>.</param>
        public RegexMatchTimeoutException(string message, Exception inner) : base(message ?? SR.RegexMatchTimeoutException_Occurred, inner) { }

        // SharpOS cut: the serialization constructor and ISerializable.GetObjectData.

        public string Input { get; } = string.Empty;

        public string Pattern { get; } = string.Empty;

        public TimeSpan MatchTimeout { get; } = TimeSpan.FromTicks(-1);
    }
}
