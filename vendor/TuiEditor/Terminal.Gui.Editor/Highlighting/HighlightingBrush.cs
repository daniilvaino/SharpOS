// Adapted for Terminal.Gui from AvaloniaEdit d7a6b63

#nullable disable
// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using Terminal.Gui.Drawing;

namespace Terminal.Gui.Editor.Highlighting;

using Color = Terminal.Gui.Drawing.Color; // SharpOS: v1's Terminal.Gui.Color (an enum) would be found first

/// <summary>
///     A brush used for syntax highlighting. In Terminal.Gui, this wraps a simple <see cref="Color" />.
/// </summary>
public class HighlightingBrush
{
    /// <summary>Creates a highlighting brush with the specified color.</summary>
    public HighlightingBrush (Color color)
    {
        Color = color;
    }

    /// <summary>Creates a highlighting brush with no color (inherits from parent).</summary>
    protected HighlightingBrush ()
    {
        Color = null;
    }

    /// <summary>Gets the color represented by this brush.</summary>
    public Color? Color { get; }

    /// <inheritdoc />
    public override string ToString ()
    {
        return Color?.ToString () ?? "Inherit";
    }

    /// <inheritdoc />
    public override bool Equals (object obj)
    {
        return obj is HighlightingBrush other && Color.Equals (other.Color);
    }

    /// <inheritdoc />
    public override int GetHashCode ()
    {
        return Color?.GetHashCode () ?? 0;
    }
}

/// <summary>
///     A highlighting brush that wraps a named system color. In Terminal.Gui, this is equivalent
///     to <see cref="HighlightingBrush" /> since all colors are simple values.
/// </summary>
public sealed class SystemColorHighlightingBrush : HighlightingBrush
{
    /// <summary>Creates a system-color brush.</summary>
    public SystemColorHighlightingBrush (Color color) : base (color) { }
}
