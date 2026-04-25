// System.Diagnostics — minimal stubs for compile-time compatibility with
// verbatim-ported BCL code. Debug.Assert becomes a no-op (we don't ship
// a debug assertion engine; contract violations that BCL catches with
// asserts will manifest as crashes here instead). Conditional attribute
// is declared so `[Conditional("DEBUG")]` lines compile, but since we
// don't define DEBUG in Release builds the decorated method calls get
// stripped by the compiler anyway.

using System;

namespace System.Diagnostics
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = false, AllowMultiple = true)]
    public sealed class ConditionalAttribute : Attribute
    {
        public ConditionalAttribute(string conditionString) { ConditionString = conditionString; }
        public string ConditionString { get; }
    }

    public static class Debug
    {
        [Conditional("DEBUG")]
        public static void Assert(bool condition) { }

        [Conditional("DEBUG")]
        public static void Assert(bool condition, string message) { }

        [Conditional("DEBUG")]
        public static void WriteLine(string message) { }
    }
}
