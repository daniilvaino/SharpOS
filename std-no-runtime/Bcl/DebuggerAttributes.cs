// Zero-runtime BCL attribute stubs needed for code lifted verbatim from
// upstream libraries (Iced, dotnet/runtime BCL ports, etc.). The attributes
// drive IDE/debugger UX only — IL just stamps the metadata; nothing reads
// them at execution time on our tier. Names and constructor shapes match
// BCL exactly so [Obsolete], [DebuggerDisplay(...)], [EditorBrowsable(...)]
// etc. compile against canonical namespaces without source edits.

using System;

namespace System
{
    [AttributeUsage(AttributeTargets.All, Inherited = false)]
    public sealed class ObsoleteAttribute : Attribute
    {
        public ObsoleteAttribute() { }
        public ObsoleteAttribute(string message) { }
        public ObsoleteAttribute(string message, bool error) { }
        public string Message => string.Empty;
        public bool IsError => false;
        public string DiagnosticId { get; set; } = string.Empty;
        public string UrlFormat { get; set; } = string.Empty;
    }
}

namespace System.Diagnostics
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Delegate | AttributeTargets.Enum
        | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property
        | AttributeTargets.Method | AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class DebuggerDisplayAttribute : Attribute
    {
        public DebuggerDisplayAttribute(string value) { Value = value; }
        public string Value { get; }
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public Type? TargetType { get; set; }
    }

    public enum DebuggerBrowsableState
    {
        Never = 0,
        Collapsed = 2,
        RootHidden = 3,
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, AllowMultiple = false)]
    public sealed class DebuggerBrowsableAttribute : Attribute
    {
        public DebuggerBrowsableAttribute(DebuggerBrowsableState state) { State = state; }
        public DebuggerBrowsableState State { get; }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Assembly,
        AllowMultiple = true)]
    public sealed class DebuggerTypeProxyAttribute : Attribute
    {
        // Plain if instead of `type?.ToString() ?? string.Empty` — the
        // `?.` + `??` chain inside a ctor crashes Roslyn lowering on our
        // minimal Object/String stubs (Sequence contains no elements
        // в SyntheticBoundNodeFactory.New на .Single() ctor-lookup).
        public DebuggerTypeProxyAttribute(Type type)
        {
            if (type == null) ProxyTypeName = string.Empty;
            else ProxyTypeName = type.ToString();
        }
        public DebuggerTypeProxyAttribute(string typeName) { ProxyTypeName = typeName; }
        public string ProxyTypeName { get; }
        public string Target { get; set; } = string.Empty;
        public Type? TargetType { get; set; }
    }

    [AttributeUsage(AttributeTargets.Constructor | AttributeTargets.Method
        | AttributeTargets.Property | AttributeTargets.Class | AttributeTargets.Struct,
        AllowMultiple = false)]
    public sealed class DebuggerStepThroughAttribute : Attribute
    {
        public DebuggerStepThroughAttribute() { }
    }

    [AttributeUsage(AttributeTargets.Constructor | AttributeTargets.Method,
        AllowMultiple = false)]
    public sealed class DebuggerHiddenAttribute : Attribute
    {
        public DebuggerHiddenAttribute() { }
    }

    [AttributeUsage(AttributeTargets.Constructor | AttributeTargets.Method
        | AttributeTargets.Class, AllowMultiple = false)]
    public sealed class DebuggerNonUserCodeAttribute : Attribute
    {
        public DebuggerNonUserCodeAttribute() { }
    }
}

namespace System.ComponentModel
{
    public enum EditorBrowsableState
    {
        Always = 0,
        Never = 1,
        Advanced = 2,
    }

    [AttributeUsage(AttributeTargets.All)]
    public sealed class EditorBrowsableAttribute : Attribute
    {
        public EditorBrowsableAttribute(EditorBrowsableState state) { State = state; }
        public EditorBrowsableState State { get; }
    }
}
