// Ported from dotnet/runtime release/8.0 (MIT):
//   src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/InternalsVisibleToAttribute.cs
// Verbatim.

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
    public sealed class InternalsVisibleToAttribute : Attribute
    {
        public InternalsVisibleToAttribute(string assemblyName)
        {
            AssemblyName = assemblyName;
        }

        public string AssemblyName { get; }
        public bool AllInternalsVisible { get; set; } = true;
    }
}
