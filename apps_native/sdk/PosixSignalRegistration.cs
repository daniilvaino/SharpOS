// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from dotnet/runtime v8.0 System.Private.CoreLib:
// Runtime/InteropServices/PosixSignal.cs, PosixSignalContext.cs and
// PosixSignalRegistration.cs, unchanged; the platform half is
// PosixSignalRegistration.AppHost.cs. Cut: the unused Token(signal, sigNo,
// handler) constructor (Unix raw signal numbers); [UnsupportedOSPlatform("windows")]
// on SIGWINCH — apps build for win-x64, and here it is supported.

using System.IO;
using System.Runtime.Versioning;

namespace System.Runtime.InteropServices
{
    /// <summary>Specifies a POSIX signal number.</summary>
    public enum PosixSignal
    {
        /// <summary>Hangup</summary>
        SIGHUP = -1,

        /// <summary>Interrupt</summary>
        SIGINT = -2,

        /// <summary>Quit</summary>
        SIGQUIT = -3,

        /// <summary>Termination</summary>
        SIGTERM = -4,

        /// <summary>Child stopped</summary>
        [UnsupportedOSPlatform("windows")]
        SIGCHLD = -5,

        /// <summary>Continue if stopped</summary>
        [UnsupportedOSPlatform("windows")]
        SIGCONT = -6,

        /// <summary>Window resized</summary>
        SIGWINCH = -7,

        /// <summary>Terminal input for background process</summary>
        [UnsupportedOSPlatform("windows")]
        SIGTTIN = -8,

        /// <summary>Terminal output for background process</summary>
        [UnsupportedOSPlatform("windows")]
        SIGTTOU = -9,

        /// <summary>Stop typed at terminal</summary>
        [UnsupportedOSPlatform("windows")]
        SIGTSTP = -10
    }

    /// <summary>
    /// Provides data for a <see cref="PosixSignalRegistration"/> event.
    /// </summary>
    public sealed class PosixSignalContext
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PosixSignalContext"/> class.
        /// </summary>
        public PosixSignalContext(PosixSignal signal) => Signal = signal;

        /// <summary>
        /// Gets the signal that occurred.
        /// </summary>
        public PosixSignal Signal { get; internal set; }

        /// <summary>
        /// Gets or sets a value that indicates whether to cancel the default handling of the signal. The default is <see langword="false"/>.
        /// </summary>
        public bool Cancel { get; set; }
    }

    /// <summary>Handles a <see cref="PosixSignal"/>.</summary>
    public sealed partial class PosixSignalRegistration : IDisposable
    {
        /// <summary>The state associated with this registration.</summary>
        /// <remarks>
        /// This is separate from the registration instance so that this token may be stored
        /// in a statically rooted table, with a finalizer on the registration able to remove it.
        /// </remarks>
        private Token? _token;

        /// <summary>Registers a <paramref name="handler"/> that is invoked when the <paramref name="signal"/> occurs.</summary>
        /// <param name="signal">The signal to register for.</param>
        /// <param name="handler">The handler that gets invoked.</param>
        /// <returns>A <see cref="PosixSignalRegistration"/> instance that can be disposed to unregister the handler.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.</exception>
        /// <exception cref="PlatformNotSupportedException"><paramref name="signal"/> is not supported by the platform.</exception>
        /// <exception cref="IOException">An error occurred while setting up the signal handling or while installing the handler for the specified signal.</exception>
        /// <remarks>
        /// Raw values can be provided for <paramref name="signal"/> on Unix by casting them to <see cref="PosixSignal"/>.
        /// Default handling of the signal can be canceled through <see cref="PosixSignalContext.Cancel"/>.
        /// <see cref="PosixSignal.SIGINT"/> and <see cref="PosixSignal.SIGQUIT"/> can be canceled on both
        /// Windows and on Unix platforms; <see cref="PosixSignal.SIGTERM"/> can only be canceled on Unix.
        /// On Unix, terminal configuration can be canceled for <see cref="PosixSignal.SIGCHLD"/> and <see cref="PosixSignal.SIGCONT"/>.
        /// </remarks>
        [UnsupportedOSPlatform("android")]
        [UnsupportedOSPlatform("browser")]
        [UnsupportedOSPlatform("ios")]
        [UnsupportedOSPlatform("tvos")]
        public static PosixSignalRegistration Create(PosixSignal signal, Action<PosixSignalContext> handler)
        {
            ArgumentNullException.ThrowIfNull(handler);

            return Register(signal, handler);
        }

        /// <summary>Initializes the registration to wrap the specified token.</summary>
        private PosixSignalRegistration(Token token) => _token = token;

        /// <summary>Unregister the handler.</summary>
        public void Dispose()
        {
            Unregister();
            GC.SuppressFinalize(this);
        }

        /// <summary>Unregister the handler.</summary>
        ~PosixSignalRegistration() => Unregister();

        /// <summary>The state associated with a registration.</summary>
        private sealed class Token
        {
            public Token(PosixSignal signal, Action<PosixSignalContext> handler)
            {
                Signal = signal;
                Handler = handler;
            }

            public PosixSignal Signal { get; }
            public Action<PosixSignalContext> Handler { get; }
        }
    }
}
