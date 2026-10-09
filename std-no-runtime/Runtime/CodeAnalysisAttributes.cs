// Marker attributes from System.Diagnostics.CodeAnalysis used throughout
// BCL. Purely static-analysis hints — no runtime behaviour. Stubbed as
// empty classes so verbatim-ported BCL code compiles.

using System;

namespace System.Diagnostics.CodeAnalysis
{
    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.Field | AttributeTargets.ReturnValue, Inherited = false)]
    internal sealed class AllowNullAttribute : Attribute { public AllowNullAttribute() { } }

    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.Field | AttributeTargets.ReturnValue, Inherited = false)]
    internal sealed class DisallowNullAttribute : Attribute { public DisallowNullAttribute() { } }

    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.Field | AttributeTargets.ReturnValue, Inherited = false)]
    internal sealed class MaybeNullAttribute : Attribute { public MaybeNullAttribute() { } }

    [AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter | AttributeTargets.Field | AttributeTargets.ReturnValue, Inherited = false)]
    internal sealed class NotNullAttribute : Attribute { public NotNullAttribute() { } }

    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    internal sealed class MaybeNullWhenAttribute : Attribute
    {
        public MaybeNullWhenAttribute(bool returnValue) { ReturnValue = returnValue; }
        public bool ReturnValue { get; }
    }

    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    internal sealed class NotNullWhenAttribute : Attribute
    {
        public NotNullWhenAttribute(bool returnValue) { ReturnValue = returnValue; }
        public bool ReturnValue { get; }
    }

    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.ReturnValue, AllowMultiple = true, Inherited = false)]
    internal sealed class NotNullIfNotNullAttribute : Attribute
    {
        public NotNullIfNotNullAttribute(string parameterName) { ParameterName = parameterName; }
        public string ParameterName { get; }
    }

    [AttributeUsage(AttributeTargets.Method, Inherited = false)]
    internal sealed class DoesNotReturnAttribute : Attribute { public DoesNotReturnAttribute() { } }

    [AttributeUsage(AttributeTargets.Parameter, Inherited = false)]
    internal sealed class DoesNotReturnIfAttribute : Attribute
    {
        public DoesNotReturnIfAttribute(bool parameterValue) { ParameterValue = parameterValue; }
        public bool ParameterValue { get; }
    }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, Inherited = false, AllowMultiple = true)]
    internal sealed class MemberNotNullAttribute : Attribute
    {
        public MemberNotNullAttribute(string member) { Members = new[] { member }; }
        public MemberNotNullAttribute(params string[] members) { Members = members; }
        public string[] Members { get; }
    }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, Inherited = false, AllowMultiple = true)]
    internal sealed class MemberNotNullWhenAttribute : Attribute
    {
        public MemberNotNullWhenAttribute(bool returnValue, string member)
        {
            ReturnValue = returnValue;
            Members = new[] { member };
        }
        public MemberNotNullWhenAttribute(bool returnValue, params string[] members)
        {
            ReturnValue = returnValue;
            Members = members;
        }
        public bool ReturnValue { get; }
        public string[] Members { get; }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum | AttributeTargets.Constructor | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Event | AttributeTargets.Interface | AttributeTargets.Delegate, Inherited = false, AllowMultiple = false)]
    internal sealed class DynamicallyAccessedMembersAttribute : Attribute
    {
        public DynamicallyAccessedMembersAttribute(DynamicallyAccessedMemberTypes memberTypes) { MemberTypes = memberTypes; }
        public DynamicallyAccessedMemberTypes MemberTypes { get; }
    }

    [Flags]
    internal enum DynamicallyAccessedMemberTypes
    {
        None = 0,
        PublicParameterlessConstructor = 0x0001,
        PublicConstructors = 0x0002 | PublicParameterlessConstructor,
        NonPublicConstructors = 0x0004,
        PublicMethods = 0x0008,
        NonPublicMethods = 0x0010,
        PublicFields = 0x0020,
        NonPublicFields = 0x0040,
        PublicNestedTypes = 0x0080,
        NonPublicNestedTypes = 0x0100,
        PublicProperties = 0x0200,
        NonPublicProperties = 0x0400,
        PublicEvents = 0x0800,
        NonPublicEvents = 0x1000,
        Interfaces = 0x2000,
        All = ~None,
    }

    // dotnet/runtime src/libraries/System.Private.CoreLib/src/System/Diagnostics/CodeAnalysis/
    // StringSyntaxAttribute.cs (MIT), verbatim minus doc comments. Ported code (StandardFormat,
    // the Utf8Formatter/Parser family) annotates format parameters with it.
    [AttributeUsage(AttributeTargets.Parameter | AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false, Inherited = false)]
    internal sealed class StringSyntaxAttribute : Attribute
    {
        public StringSyntaxAttribute(string syntax)
        {
            Syntax = syntax;
            Arguments = new object?[0];
        }

        public StringSyntaxAttribute(string syntax, params object?[] arguments)
        {
            Syntax = syntax;
            Arguments = arguments;
        }

        public string Syntax { get; }
        public object?[] Arguments { get; }

        public const string CompositeFormat = nameof(CompositeFormat);
        public const string DateOnlyFormat = nameof(DateOnlyFormat);
        public const string DateTimeFormat = nameof(DateTimeFormat);
        public const string EnumFormat = nameof(EnumFormat);
        public const string GuidFormat = nameof(GuidFormat);
        public const string Json = nameof(Json);
        public const string NumericFormat = nameof(NumericFormat);
        public const string Regex = nameof(Regex);
        public const string TimeOnlyFormat = nameof(TimeOnlyFormat);
        public const string TimeSpanFormat = nameof(TimeSpanFormat);
        public const string Uri = nameof(Uri);
        public const string Xml = nameof(Xml);
    }

    // Trimming/AOT annotations (dotnet/runtime System.Private.CoreLib, MIT),
    // minus doc comments. ILC here does not trim by them; ported code
    // (System.Text.Json.Nodes) carries them.
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Class, Inherited = false)]
    internal sealed class RequiresUnreferencedCodeAttribute : Attribute
    {
        public RequiresUnreferencedCodeAttribute(string message) { Message = message; }
        public string Message { get; }
        public string? Url { get; set; }
    }

    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Class, Inherited = false)]
    internal sealed class RequiresDynamicCodeAttribute : Attribute
    {
        public RequiresDynamicCodeAttribute(string message) { Message = message; }
        public string Message { get; }
        public string? Url { get; set; }
    }

    [AttributeUsage(AttributeTargets.All, Inherited = false, AllowMultiple = true)]
    internal sealed class SuppressMessageAttribute : Attribute
    {
        public SuppressMessageAttribute(string category, string checkId) { Category = category; CheckId = checkId; }
        public string Category { get; }
        public string CheckId { get; }
        public string? Scope { get; set; }
        public string? Target { get; set; }
        public string? MessageId { get; set; }
        public string? Justification { get; set; }
    }

    [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Constructor | AttributeTargets.Method | AttributeTargets.Property | AttributeTargets.Event, Inherited = false, AllowMultiple = false)]
    internal sealed class ExcludeFromCodeCoverageAttribute : Attribute
    {
        public ExcludeFromCodeCoverageAttribute() { }
        public string? Justification { get; set; }
    }
}
