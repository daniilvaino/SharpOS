// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Text.Json
{
    /// <summary>Provides tools for avoiding stack overflows.</summary>
    internal static class StackHelper
    {
        /// <summary>Tries to ensure there is sufficient stack to execute the average .NET function.</summary>
        public static bool TryEnsureSufficientExecutionStack()
        {
            // SharpOS cut: RuntimeHelpers.TryEnsureSufficientExecutionStack — std
            // cannot ask how much of the thread's stack is left. Always "enough":
            // a recursion too deep for the stack faults instead of throwing
            // InsufficientExecutionStackException (JsonElement.DeepEquals).
            return true;
        }
    }
}
