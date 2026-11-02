namespace Terminal.Gui.Editor;

/// <summary>
///     Provides representative sample content for design-time preview.
///     Populated by <see cref="Editor.EnableForDesign" /> — inert at runtime.
/// </summary>
internal static class EditorDesignData
{
    /// <summary>
    ///     A short, self-contained C# snippet that exercises syntax highlighting (keywords,
    ///     strings, comments), line numbers, and word wrap in a design-time preview.
    /// </summary>
    internal const string SampleCSharpCode =
        """
        // Terminal.Gui.Editor — design preview
        using System;

        namespace Demo;

        /// <summary>Sample class for design-time preview.</summary>
        public class Greeter
        {
            private readonly string _name;

            public Greeter (string name)
            {
                _name = name ?? throw new ArgumentNullException (nameof (name));
            }

            public string Greet () => $"Hello, {_name}!";

            public static void Main ()
            {
                Greeter g = new ("World");
                Console.WriteLine (g.Greet ());
            }
        }
        """;
}
