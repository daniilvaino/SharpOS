// System.Diagnostics — minimal Debug implementation. BCL's Debug class
// has IndentLevel, listeners, conditional builds, asynchronous flush —
// we implement the subset that real BCL code reaches for: Assert / Fail
// / WriteLine / Write / Print / Indent / Unindent / IndentLevel.
//
// Output goes through the tier-specific DebugOutput backend: kernel routes
// to the HAL console (Diagnostics.Output.KernelConsole.cs), apps to the
// AppHost service table (apps_native/sdk/DebugOutput.AppHost.cs) — csproj
// picks exactly one, same pattern as GcMemorySource.{KernelHeap,AppStatic}.
// Asserts halt — there is no "ignore and continue" mode. This matches our
// broader pattern: when an invariant breaks in our environment, halting is
// safer than corrupted execution.
//
// `[Conditional("DEBUG")]` is preserved on the methods so call-sites
// in Release builds (no DEBUG symbol defined) get stripped by the C#
// compiler. That also means asserts are "free" in Release.

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
        private static int s_indentLevel;
        private const int IndentSize = 4;

        public static int IndentLevel
        {
            get => s_indentLevel;
            set => s_indentLevel = value < 0 ? 0 : value;
        }

        [Conditional("DEBUG")]
        public static void Assert(bool condition)
        {
            if (!condition) Fail("Assertion failed");
        }

        [Conditional("DEBUG")]
        public static void Assert(bool condition, string message)
        {
            if (!condition) Fail(message);
        }

        [Conditional("DEBUG")]
        public static void Assert(bool condition, string message, string detailMessage)
        {
            if (!condition) Fail(message, detailMessage);
        }

        [Conditional("DEBUG")]
        public static void Fail(string message)
        {
            DebugOutput.Write("\r\n*** Debug.Fail: ");
            DebugOutput.Write(message ?? "(null)");
            DebugOutput.Write(" ***\r\n");
            while (true) { }
        }

        [Conditional("DEBUG")]
        public static void Fail(string message, string detailMessage)
        {
            DebugOutput.Write("\r\n*** Debug.Fail: ");
            DebugOutput.Write(message ?? "(null)");
            if (detailMessage != null)
            {
                DebugOutput.Write(" — ");
                DebugOutput.Write(detailMessage);
            }
            DebugOutput.Write(" ***\r\n");
            while (true) { }
        }

        [Conditional("DEBUG")]
        public static void Write(string message)
        {
            if (message != null) DebugOutput.Write(message);
        }

        [Conditional("DEBUG")]
        public static void WriteLine(string message)
        {
            WriteIndent();
            if (message != null) DebugOutput.Write(message);
            DebugOutput.Write("\r\n");
        }

        [Conditional("DEBUG")]
        public static void WriteLine() => DebugOutput.Write("\r\n");

        [Conditional("DEBUG")]
        public static void Print(string message) => WriteLine(message);

        [Conditional("DEBUG")]
        public static void Indent() => s_indentLevel++;

        [Conditional("DEBUG")]
        public static void Unindent()
        {
            if (s_indentLevel > 0) s_indentLevel--;
        }

        private static void WriteIndent()
        {
            int spaces = s_indentLevel * IndentSize;
            for (int i = 0; i < spaces; i++) DebugOutput.WriteChar(' ');
        }
    }
}
