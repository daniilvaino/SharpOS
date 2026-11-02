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

namespace Terminal.Gui.Editor.Highlighting.Xshd;

/// <summary>
///     A color in an Xshd file.
/// </summary>
public class XshdColor : XshdElement
{
    /// <summary>
    ///     Gets/sets the name.
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    ///     Gets/sets the foreground brush.
    /// </summary>
    public HighlightingBrush Foreground { get; set; }

    /// <summary>
    ///     Gets/sets the background brush.
    /// </summary>
    public HighlightingBrush Background { get; set; }

    /// <summary>
    ///     Gets/sets the bold flag.
    /// </summary>
    public bool? Bold { get; set; }

    /// <summary>
    ///     Gets/sets the underline flag
    /// </summary>
    public bool? Underline { get; set; }

    /// <summary>
    ///     Gets/sets the strikethrough flag
    /// </summary>
    public bool? Strikethrough { get; set; }

    /// <summary>
    ///     Gets/sets the italic flag.
    /// </summary>
    public bool? Italic { get; set; }

    /// <summary>
    ///     Gets/Sets the example text that demonstrates where the color is used.
    /// </summary>
    public string ExampleText { get; set; }

    /// <summary>
    ///     Gets/Sets the optional <c>category</c> attribute: a Terminal.Gui <c>VisualRole</c> name
    ///     that overrides the built-in xshd-name → role mapping for this color. Terminal.Gui
    ///     fork addition over upstream AvaloniaEdit xshd.
    /// </summary>
    public string Category { get; set; }

    /// <inheritdoc />
    public override object AcceptVisitor (IXshdVisitor visitor)
    {
        return visitor.VisitColor (this);
    }
}
