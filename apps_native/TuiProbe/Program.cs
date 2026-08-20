// Does Terminal.Gui's core compile against our std?
//
// Nothing here is meant to run yet. The point of this project is the compiler's
// answer: which types and members the library reaches for that we do not have.
// Every port so far (PeNet, mini-LINQ, XtermSharp) started the same way, and the
// first error list is always long and then collapses quickly once the real gaps
// are told apart from their consequences.

internal static class Program
{
    private static int Main()
    {
        // Touching a few types keeps the linker from dropping the library
        // wholesale before the compiler has had its say.
        var rect = new Terminal.Gui.Rect(0, 0, 80, 25);
        var attr = Terminal.Gui.Attribute.Make(Terminal.Gui.Color.White, Terminal.Gui.Color.Black);
        return rect.Width + rect.Height + (int)attr.Value;
    }
}
