using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace Terminal.Gui.Editor;

/// <summary>
///     Composite gutter hosting an optional <see cref="LineNumberGutter" /> and an optional <see cref="FoldingGutter" />.
///     Hosted as a SubView of the <see cref="Editor" />'s <see cref="Padding" />.
/// </summary>
public sealed class Gutter : View
{
    private readonly Editor _editor;
    private FoldingGutter? _foldingGutter;
    private LineNumberGutter? _lineNumbers;

    /// <summary>Initializes a new <see cref="Gutter" /> for <paramref name="editor" />.</summary>
    public Gutter (Editor editor)
    {
        ArgumentNullException.ThrowIfNull (editor);

        _editor = editor;
        CanFocus = false;

        MouseBindings.Add (MouseFlags.WheeledUp, Command.ScrollUp);
        MouseBindings.Add (MouseFlags.WheeledDown, Command.ScrollDown);
    }

    /// <summary>
    ///     Synchronizes the internal layout. Called by the editor when the gutter width changes.
    /// </summary>
    internal void SyncLayout ()
    {
        GutterOptions options = _editor.GutterOptions;
        var showLineNumbers = options.HasFlag (GutterOptions.LineNumbers);
        var showFolding = options.HasFlag (GutterOptions.Folding) && _editor.FoldingManager is not null;

        // --- Line numbers ---
        if (showLineNumbers)
        {
            if (_lineNumbers is null)
            {
                _lineNumbers = new LineNumberGutter (_editor)
                {
                    X = 0,
                    Y = 0,
                    Width = Dim.Fill (),
                    Height = Dim.Fill ()
                };
                Add (_lineNumbers);
            }
        }
        else
        {
            if (_lineNumbers is not null)
            {
                Remove (_lineNumbers);
                _lineNumbers.Dispose ();
                _lineNumbers = null;
            }
        }

        // --- Folding ---
        if (showFolding)
        {
            if (_foldingGutter is null)
            {
                _foldingGutter = new FoldingGutter (_editor)
                {
                    Y = 0,
                    Width = 2,
                    Height = Dim.Fill ()
                };
                Add (_foldingGutter);
            }
        }
        else
        {
            if (_foldingGutter is not null)
            {
                Remove (_foldingGutter);
                _foldingGutter.Dispose ();
                _foldingGutter = null;
            }
        }

        // Adjust widths depending on which subviews are present.
        if (_lineNumbers is not null && _foldingGutter is not null)
        {
            // Line numbers on the left, fold indicators on the right.
            _lineNumbers.X = 0;
            _lineNumbers.Width = Dim.Fill (2);
            _foldingGutter.X = Pos.Right (_lineNumbers);
        }
        else if (_lineNumbers is not null)
        {
            _lineNumbers.X = 0;
            _lineNumbers.Width = Dim.Fill ();
        }
        else
        {
            _foldingGutter?.X = 0;
        }
    }
}
