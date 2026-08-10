// System.Console for the app tier, backed by the AppHost service table
// (kernel console via WriteString). Write/WriteLine(string) only — the
// subset ported app code (ManagedDoom load/progress logging) uses.

using SharpOS.AppSdk;

namespace System
{
    public static class Console
    {
        public static void Write(string value)
        {
            if (value != null) AppHost.WriteString(value);
        }

        public static void WriteLine(string value)
        {
            if (value != null) AppHost.WriteString(value);
            AppHost.WriteString("\n");
        }

        public static void WriteLine()
        {
            AppHost.WriteString("\n");
        }

        // BCL's object overloads. Ported code reaches for these without
        // thinking — Console.WriteLine(e) in a catch block is the common one —
        // and the alternative is a compile error at a call site nobody wants to
        // edit. ToString() on an exception gives its message here rather than
        // the type-and-stack the BCL prints.
        public static void Write(object value) => Write(value?.ToString());

        public static void WriteLine(object value) => WriteLine(value?.ToString());
    }
}
