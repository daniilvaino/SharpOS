using System.IO;
using SharpOS.AppSdk;

namespace Shell
{
    // The side panel at the prompt: the objects of the last command, a tree
    // each, beside the lines that stand for them in the main panel. Opens on
    // the first object, as tmux splits a window; strings and bytes leave it
    // as it is. Each command that shows an object starts it afresh.
    internal sealed class Inspector : TextWriter
    {
        private bool _fresh = true;
        private int _shown;

        private Inspector() { }

        /// <summary>An inspector, or null when the kernel has no panels.</summary>
        internal static Inspector Create() => AppHost.Terminal.SidePanelColumns < 0 ? null : new Inspector();

        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        /// <summary>A command starts: its first object clears what the last one left.</summary>
        internal void Begin() => _fresh = true;

        /// <summary>Takes the screen back from the panel.</summary>
        internal void Hide() => AppHost.Terminal.SidePanel(0);

        public override void Write(char value) => Write(value.ToString());

        public override void Write(string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (_fresh)
            {
                _fresh = false;
                _shown = 0;
                Open();
                // From the top, no cursor: the panel is read, not typed in.
                // The last command's tree leaves the scrollback too.
                AppHost.Terminal.WriteSide("\u001b[?25l\u001b[H\u001b[2J\u001b[3J");
            }
            // A blank line between two objects.
            if (_shown++ > 0) AppHost.Terminal.WriteSide("\n");
            AppHost.Terminal.WriteSide(value);
        }

        // Two fifths of the screen, at the right; the panel already there stays as it is.
        private static void Open()
        {
            if (AppHost.Terminal.SidePanelColumns > 0) return;
            int screen = System.Console.WindowWidth;
            if (screen <= 0) return;
            AppHost.Terminal.SidePanel(screen * 2 / 5);
        }
    }
}
