using System.Text;
using SharpOS.AppSdk;

namespace Shell
{
    /// <summary>
    /// One line from the keyboard, echoed as it is typed. Enough to drive the
    /// prompt: characters and backspace, nothing else.
    /// </summary>
    /// <remarks>
    /// The key service never blocks — it answers NoData when nothing is
    /// pending — so the wait is a poll with a sleep in it. Ten milliseconds
    /// is what the Terminal.Gui main loop uses, and it is below what a typist
    /// can feel.
    /// </remarks>
    internal static class LineReader
    {
        private const uint IdleMilliseconds = 10;

        public static string Read()
        {
            var line = new StringBuilder();

            while (true)
            {
                if (AppHost.TryReadKey(out KeyInfo key) != AppServiceStatus.Ok)
                {
                    AppThreads.Sleep(IdleMilliseconds);
                    continue;
                }

                char c = (char)key.UnicodeChar;

                // Key releases arrive with no character. So do modifiers.
                if (c == '\0') continue;

                if (c == '\r' || c == '\n')
                {
                    AppHost.WriteString("\n");
                    return line.ToString();
                }

                if (c == '\b')
                {
                    if (line.Length > 0)
                    {
                        line.Remove(line.Length - 1, 1);
                        // Back over the character, blank it, back again: the
                        // console has no way to erase in place.
                        AppHost.WriteString("\b \b");
                    }
                    continue;
                }

                if (c < ' ') continue;

                line.Append(c);
                AppHost.WriteChar(c);
            }
        }
    }
}
