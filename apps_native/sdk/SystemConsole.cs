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
    }
}
