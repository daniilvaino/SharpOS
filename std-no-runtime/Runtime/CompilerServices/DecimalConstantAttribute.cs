// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from dotnet/runtime release/8.0 (v8.0.27, MIT):
//   src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/DecimalConstantAttribute.cs
// Verbatim. Roslyn emits it on every `const decimal` field and decimal-default
// parameter (decimal has no metadata constant form), so it must exist for
// decimal.MaxValue & co. to compile.

// Note: If you add a new ctor overloads you need to update ParameterInfo.RawDefaultValue

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter, Inherited = false)]
    public sealed class DecimalConstantAttribute : Attribute
    {
        private readonly decimal _dec;

        [CLSCompliant(false)]
        public DecimalConstantAttribute(
            byte scale,
            byte sign,
            uint hi,
            uint mid,
            uint low
        )
        {
            _dec = new decimal((int)low, (int)mid, (int)hi, sign != 0, scale);
        }

        public DecimalConstantAttribute(
            byte scale,
            byte sign,
            int hi,
            int mid,
            int low
        )
        {
            _dec = new decimal(low, mid, hi, sign != 0, scale);
        }

        public decimal Value => _dec;
    }
}
