// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from dotnet/runtime src/libraries/System.Private.CoreLib/src/System/Collections/Generic/DebugViewDictionaryItem.cs (.NET 10, MIT), unchanged.

using System.Diagnostics;

namespace System.Collections.Generic
{
    /// <summary>
    /// Defines a key/value pair for displaying an item of a dictionary by a debugger.
    /// </summary>
    [DebuggerDisplay("{Value}", Name = "[{Key}]")]
    internal readonly struct DebugViewDictionaryItem<TKey, TValue>
    {
        public DebugViewDictionaryItem(TKey key, TValue value)
        {
            Key = key;
            Value = value;
        }

        public DebugViewDictionaryItem(KeyValuePair<TKey, TValue> keyValue)
        {
            Key = keyValue.Key;
            Value = keyValue.Value;
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Collapsed)]
        public TKey Key { get; }

        [DebuggerBrowsable(DebuggerBrowsableState.Collapsed)]
        public TValue Value { get; }
    }
}
