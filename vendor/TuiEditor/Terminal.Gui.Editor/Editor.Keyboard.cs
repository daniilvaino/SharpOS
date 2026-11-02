using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Input;

namespace Terminal.Gui.Editor;

public partial class Editor
{
    /// <summary>
    ///     Runs before command bindings. When completion is active, intercepts accept
    ///     (Enter/Tab) and dismiss (Esc/Left/Right) keys so they don't trigger the normal
    ///     editor command bindings; Up/Down are left to the focused popup ListView. Also
    ///     checks provider-specific trigger keys (e.g. Ctrl+Space).
    ///     Additionally, tracks the kill-ring consecutive-kill flag: snapshots
    ///     <c>_lastCommandWasKill</c> into <c>_previousCommandWasKill</c>, then clears
    ///     <c>_lastCommandWasKill</c>. The kill commands re-set it after executing.
    /// </summary>
    /// <inheritdoc />
    protected override bool OnKeyDown (Key key)
    {
        if (HandleCompletionKey (key))
        {
            // A completion-consumed key is not a kill command — break any consecutive-kill run.
            _lastCommandWasKill = false;

            return true;
        }

        _previousCommandWasKill = _lastCommandWasKill;
        _lastCommandWasKill = false;

        var result = base.OnKeyDown (key);

        // Clear the snapshot so it does not leak into a subsequent InvokeCommand call.
        // If the dispatched command was a kill, _lastCommandWasKill is already true;
        // _previousCommandWasKill is no longer needed.
        _previousCommandWasKill = false;

        return result;
    }

    /// <summary>
    ///     Catches keystrokes that didn't match any registered <see cref="Command" /> binding (set up in
    ///     <see cref="CreateCommandsAndBindings" />) and inserts the typed rune into the document. Skips
    ///     modified keys and control characters — those are either bound elsewhere or not editor input.
    /// </summary>
    /// <inheritdoc />
    protected override bool OnKeyDownNotHandled (Key key)
    {
        if (TryHandleKeyboardColumnSelect (key))
        {
            return true;
        }

        // Esc clears a multi-caret block. Resolve the key from the application's
        // Command.Quit binding — the Editor itself binds no Quit, so the earlier
        // Editor-scoped KeyBindings.GetFirstFromCommands(Command.Quit) lookup resolved
        // to null and the clear never ran (regressed multi-caret Esc in 7ad560e). This
        // tracks the same key Terminal.Gui uses framework-wide for escape/cancel.
        if (key == Application.GetDefaultKey (Command.Quit) && HasMultipleCarets)
        {
            ClearAdditionalCarets ();
            ClearSelection ();

            return true;
        }

        if (key.IsCtrl || key.IsAlt)
        {
            return false;
        }

        // Rune.IsControl already covers U+0000 (default(Rune)), so no explicit NUL guard is needed.
        if (key is not { AsRune: { } rune } || Rune.IsControl (rune))
        {
            return false;
        }

        return InsertTypedText (rune.ToString ());
    }

    /// <summary>
    ///     The single canonical "type text at the caret" path: honors read-only,
    ///     multi-caret, selection, and overwrite mode, then refreshes the completion
    ///     popup. Shared by <see cref="OnKeyDownNotHandled" /> and the completion popup
    ///     key handler so the two never drift — the completion path previously skipped
    ///     the multi-caret branch and inserted at a single caret only.
    /// </summary>
    private bool InsertTypedText (string text)
    {
        if (ReadOnly)
        {
            return true;
        }

        if (HasMultipleCarets)
        {
            MultiCaretInsert (text);

            return true;
        }

        if (HasSelection)
        {
            ReplaceSelection (text);
        }
        else if (OverwriteMode && _document is not null)
        {
            OverwriteAtCaret (text);
        }
        else
        {
            _document!.Insert (CaretOffset, text);
        }

        // After inserting, notify the completion system so it can open / filter.
        NotifyCompletionAfterInsert ();

        return true;
    }

    private bool TryHandleKeyboardColumnSelect (Key key)
    {
        if (!key.IsCtrl || !key.IsShift || !key.IsAlt)
        {
            return false;
        }

        Key baseKey = key.NoCtrl.NoShift.NoAlt;

        if (baseKey == Key.CursorUp)
        {
            ColumnSelectByKeyboard (-1, 0);

            return true;
        }

        if (baseKey == Key.CursorDown)
        {
            ColumnSelectByKeyboard (1, 0);

            return true;
        }

        if (baseKey == Key.CursorLeft)
        {
            ColumnSelectByKeyboard (0, -1);

            return true;
        }

        if (baseKey == Key.CursorRight)
        {
            ColumnSelectByKeyboard (0, 1);

            return true;
        }

        var pageDelta = Math.Max (1, Viewport.Height);

        if (baseKey == Key.PageUp)
        {
            ColumnSelectByKeyboard (-pageDelta, 0);

            return true;
        }

        if (baseKey != Key.PageDown)
        {
            return false;
        }

        ColumnSelectByKeyboard (pageDelta, 0);

        return true;
    }

    /// <summary>
    ///     Canonical delete-left: deletes the selection or the grapheme before the
    ///     caret(s) (multi-caret aware) and refreshes completion. Shared by the
    ///     <see cref="Command.DeleteCharLeft" /> binding and the completion popup key
    ///     handler so Backspace behaves identically with or without the popup open.
    /// </summary>
    private bool? DeleteCharLeftAndRefresh ()
    {
        var result = MultiCaretDeleteLeft ();
        NotifyCompletionAfterInsert ();

        return result;
    }
}
