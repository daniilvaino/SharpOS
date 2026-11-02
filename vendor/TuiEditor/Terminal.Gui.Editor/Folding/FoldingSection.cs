// Adapted for Terminal.Gui from AvaloniaEdit d7a6b63
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

using System.Diagnostics;

namespace Terminal.Gui.Editor.Document.Folding;

/// <summary>
///     A section that can be folded.
/// </summary>
public sealed class FoldingSection : TextSegment
{
    private readonly FoldingManager _manager;
    private bool _isFolded;

    internal FoldingSection (FoldingManager manager, int startOffset, int endOffset)
    {
        Debug.Assert (manager != null);
        _manager = manager;
        StartOffset = startOffset;
        Length = endOffset - startOffset;
    }

    /// <summary>
    ///     Gets/sets if the section is folded.
    /// </summary>
    public bool IsFolded
    {
        get => _isFolded;
        set
        {
            if (_isFolded != value)
            {
                _isFolded = value;
                _manager.RaiseFoldingChanged ();
            }
        }
    }

    /// <summary>
    ///     Gets/Sets the text used to display the collapsed version of the folding section.
    /// </summary>
    public string? Title
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                if (IsFolded)
                {
                    _manager.RaiseFoldingChanged ();
                }
            }
        }
    }

    /// <summary>
    ///     Gets the content of the collapsed lines as text.
    /// </summary>
    public string TextContent => _manager.Document.GetText (StartOffset, EndOffset - StartOffset);

    /// <summary>
    ///     Gets/Sets an additional object associated with this folding section.
    /// </summary>
    public object? Tag { get; set; }

    /// <inheritdoc />
    protected override void OnSegmentChanged ()
    {
        base.OnSegmentChanged ();
        // don't redraw if the FoldingSection wasn't added to the FoldingManager's collection yet
        if (IsConnectedToCollection)
        {
            _manager.RaiseFoldingChanged ();
        }
    }
}
