// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from dotnet/runtime v8.0 System.Console: ConsoleCancelEventArgs.cs and
// ConsoleSpecialKey.cs, unchanged.

namespace System
{

    public delegate void ConsoleCancelEventHandler(object? sender, ConsoleCancelEventArgs e);

    public sealed class ConsoleCancelEventArgs : EventArgs
    {
        private readonly ConsoleSpecialKey _type;

        internal ConsoleCancelEventArgs(ConsoleSpecialKey type)
        {
            _type = type;
        }

        // Whether to cancel the break event.  By setting this to true, the
        // Control-C will not kill the process.
        public bool Cancel
        {
            get; set;
        }

        public ConsoleSpecialKey SpecialKey
        {
            get { return _type; }
        }
    }

    public enum ConsoleSpecialKey
    {
        ControlC = 0,
        ControlBreak = 1,
    }
}
