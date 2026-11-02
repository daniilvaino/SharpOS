using System.Globalization;
using Terminal.Gui.App;
using Terminal.Gui.Editor.Configuration;
using Terminal.Gui.Editor.Document;
using Terminal.Gui.Editor.Document.Folding;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;

namespace Terminal.Gui.Editor;

public partial class Editor
{
    /// <summary>
    ///     Editor-specific default key bindings layered on top of <see cref="View.DefaultKeyBindings" />.
    ///     The base layer already maps cursor / Home / End / PageUp / PageDown (and their Shift variants)
    ///     to the corresponding movement and *Extend <see cref="Command" />s, plus Ctrl+A → SelectAll;
    ///     this dictionary covers what's editor-specific (Enter, Backspace/Delete, Ctrl+Z / Ctrl+Y, the
    ///     Ctrl+Home/End whole-document binds).
    /// </summary>
    /// <remarks>
    ///     Process-wide static. Do not mutate from parallel tests — see Terminal.Gui's same convention
    ///     on <see cref="Terminal.Gui.Views.TextField.DefaultKeyBindings" />.
    /// </remarks>
    public new static Dictionary<Command, PlatformKeyBinding>? DefaultKeyBindings
    {
        get => EditorSettings.Defaults.DefaultKeyBindings;
        set => EditorSettings.Defaults.DefaultKeyBindings = value;
    }

    private void CreateCommandsAndBindings ()
    {
        // View's SetupKeyboard pre-binds Enter→Accept and Space→Activate. In a text editor those
        // are the literal characters, so reclaim them before applying layered bindings.
        KeyBindings.Remove (Key.Enter);
        KeyBindings.Remove (Key.Space);

        // Plain movement (collapses any existing selection)
        AddCommand (Command.Left, () => MoveCaretByCollapsing (-1));
        AddCommand (Command.Right, () => MoveCaretByCollapsing (1));
        AddCommand (Command.Up, () => MoveCaretVerticallyCollapsing (-1));
        AddCommand (Command.Down, () => MoveCaretVerticallyCollapsing (1));
        AddCommand (Command.LeftStart, MoveCaretToLineStart);
        AddCommand (Command.RightEnd, MoveCaretToLineEnd);
        AddCommand (Command.Start, () => SetCaretAndReturnTrue (0));
        AddCommand (Command.End, () => SetCaretAndReturnTrue (_document!.TextLength));
        AddCommand (Command.PageUp, () => MoveCaretVerticallyCollapsing (-Math.Max (1, Viewport.Height)));
        AddCommand (Command.PageDown, () => MoveCaretVerticallyCollapsing (Math.Max (1, Viewport.Height)));
        AddCommand (Command.ScrollUp, () => ScrollVerticalCommand (-1));
        AddCommand (Command.ScrollDown, () => ScrollVerticalCommand (1));
        AddCommand (Command.ScrollLeft, () => ScrollHorizontalCommand (-1));
        AddCommand (Command.ScrollRight, () => ScrollHorizontalCommand (1));

        // Selection-extending movement
        AddCommand (Command.LeftExtend, () => ExtendCommand (() => ExtendCaretBy (-1)));
        AddCommand (Command.RightExtend, () => ExtendCommand (() => ExtendCaretBy (1)));
        AddCommand (Command.UpExtend, () => Multiline ? ExtendCommand (() => ExtendCaretVertically (-1)) : true);
        AddCommand (Command.DownExtend, () => Multiline ? ExtendCommand (() => ExtendCaretVertically (1)) : true);
        AddCommand (Command.LeftStartExtend,
            () => ExtendCommand (() =>
                ExtendCaretTo (!Multiline ? 0 : _document!.GetLineByOffset (CaretOffset).Offset)));

        AddCommand (Command.RightEndExtend, () => ExtendCommand (() =>
        {
            if (!Multiline)
            {
                ExtendCaretTo (_document!.TextLength);
            }
            else
            {
                DocumentLine line = _document!.GetLineByOffset (CaretOffset);
                ExtendCaretTo (line.Offset + line.Length);
            }
        }));

        AddCommand (Command.StartExtend, () => ExtendCommand (() => ExtendCaretTo (0)));
        AddCommand (Command.EndExtend, () => ExtendCommand (() => ExtendCaretTo (_document!.TextLength)));
        AddCommand (Command.PageUpExtend,
            () => Multiline ? ExtendCommand (() => ExtendCaretVertically (-Math.Max (1, Viewport.Height))) : true);
        AddCommand (Command.PageDownExtend,
            () => Multiline ? ExtendCommand (() => ExtendCaretVertically (Math.Max (1, Viewport.Height))) : true);

        // Selection ops
        AddCommand (Command.SelectAll, () =>
        {
            SelectAll ();

            return true;
        });

        // Clipboard
        AddCommand (Command.Copy, () =>
        {
            if (!HasSelection)
            {
                // No selection: copy the current line (including its delimiter) — standard
                // "line copy" behaviour matching VS Code / Sublime / JetBrains editors.
                if (_document is null)
                {
                    return true;
                }

                DocumentLine line = _document.GetLineByOffset (CaretOffset);
                App?.Clipboard?.TrySetClipboardData (_document.GetText (line.Offset, line.TotalLength));

                return true;
            }

            App?.Clipboard?.TrySetClipboardData (SelectedText);

            return true;
        });

        AddCommand (Command.Cut, () =>
        {
            if (ReadOnly || !HasSelection)
            {
                return true;
            }

            // Abort cut if clipboard write fails — never destroy text without placing it on the clipboard.
            if (App?.Clipboard?.TrySetClipboardData (SelectedText) is not true)
            {
                return true;
            }

            using (_document!.RunUpdate ())
            {
                ReplaceSelection (string.Empty);
            }

            return true;
        });

        AddCommand (Command.Paste, () =>
        {
            if (ReadOnly)
            {
                return true;
            }

            IClipboard? clipboard = App?.Clipboard;

            if (clipboard is null || !clipboard.TryGetClipboardData (out var contents))
            {
                return true;
            }

            // In single-line mode, strip newlines from pasted content so the document
            // stays on one line.
            if (!Multiline)
            {
                contents = contents.ReplaceLineEndings (string.Empty);
            }

            using (_document!.RunUpdate ())
            {
                if (HasSelection)
                {
                    ReplaceSelection (contents);
                }
                else
                {
                    _document.Insert (CaretOffset, contents);
                }
            }

            return true;
        });

        // Kill-ring — Emacs-style line-boundary kill; unbound by default (no entry in
        // DefaultKeyBindings). Users opt in via Editor.DefaultKeyBindings config.
        AddCommand (Command.CutToEndOfLine, CutToEndOfLine);
        AddCommand (Command.CutToStartOfLine, CutToStartOfLine);

        // Editing — selection-aware (multi-caret aware), with completion notification.
        AddCommand (Command.NewLine, () =>
        {
            DismissCompletion ();

            return MultiCaretNewLine ();
        });
        AddCommand (Command.DeleteCharLeft, DeleteCharLeftAndRefresh);
        AddCommand (Command.DeleteCharRight, () =>
        {
            DismissCompletion ();

            return MultiCaretDeleteRight ();
        });

        // History
        AddCommand (Command.Undo, () =>
        {
            if (ReadOnly || !_document!.UndoStack.CanUndo)
            {
                return true;
            }

            ClearSelection ();
            _document!.UndoStack.Undo ();

            return true;
        });

        AddCommand (Command.Redo, () =>
        {
            if (ReadOnly || !_document!.UndoStack.CanRedo)
            {
                return true;
            }

            ClearSelection ();
            _document!.UndoStack.Redo ();

            return true;
        });

        // Folding
        AddCommand (Command.Collapse, ToggleFoldUnderCaret);

        // Indentation — InsertTab / Unindent return bool, wrapped for CommandImplementation (bool?).
        AddCommand (Command.InsertTab, () => InsertTab ());
        AddCommand (Command.Unindent, () => Unindent ());

        // Find / Replace
        AddCommand (Command.Find, InvokeFindRequested);
        AddCommand (Command.Replace, InvokeReplaceRequested);
        AddCommand (Command.FindNext, FindNextCommand);
        AddCommand (Command.FindPrevious, FindPreviousCommand);

        // Vertical multi-caret: add a caret one line above / below the block at the sticky column.
        AddCommand (Command.InsertCaretAbove, () => AddCaretVertically (-1));
        AddCommand (Command.InsertCaretBelow, () => AddCaretVertically (1));
        // Word navigation and kill
        AddCommand (Command.WordLeft, () =>
        {
            MoveCaretToWordBoundary (false);
            return true;
        });
        AddCommand (Command.WordRight, () =>
        {
            MoveCaretToWordBoundary (true);
            return true;
        });
        AddCommand (Command.WordLeftExtend,
            () => ExtendCommand (() => ExtendCaretTo (GetWordBoundaryOffset (CaretOffset, false))));
        AddCommand (Command.WordRightExtend,
            () => ExtendCommand (() => ExtendCaretTo (GetWordBoundaryOffset (CaretOffset, true))));
        AddCommand (Command.KillWordLeft, () =>
        {
            KillToWordBoundary (false);
            return true;
        });
        AddCommand (Command.KillWordRight, () =>
        {
            KillToWordBoundary (true);
            return true;
        });

        // Overwrite mode
        AddCommand (Command.ToggleOverwrite, () =>
        {
            OverwriteMode = !OverwriteMode;

            return true;
        });
        AddCommand (Command.EnableOverwrite, () =>
        {
            OverwriteMode = true;

            return true;
        });
        AddCommand (Command.DisableOverwrite, () =>
        {
            OverwriteMode = false;

            return true;
        });

        // Context menu — return false when suppressed so the command can bubble.
        AddCommand (Command.Context, () =>
        {
            if (ContextMenu is null)
            {
                return false;
            }

            ShowContextMenu ();

            return true;
        });

        ApplyKeyBindings (View.DefaultKeyBindings, DefaultKeyBindings);

        // Reclaim Tab / Shift+Tab from the framework's default focus-cycling bindings so our
        // InsertTab / Unindent commands fire instead.
        KeyBindings.Remove (Key.Tab);
        KeyBindings.Remove (Key.Tab.WithShift);
        KeyBindings.Add (Key.Tab, Command.InsertTab);
        KeyBindings.Add (Key.Tab.WithShift, Command.Unindent);

        MouseBindings.Add (MouseFlags.WheeledUp, Command.ScrollUp);
        MouseBindings.Add (MouseFlags.WheeledDown, Command.ScrollDown);
        MouseBindings.Add (MouseFlags.WheeledLeft, Command.ScrollLeft);
        MouseBindings.Add (MouseFlags.WheeledRight, Command.ScrollRight);

        // Allow scroll commands from gutter subviews (hosted in Padding) to bubble up to this Editor.
        CommandsToBubbleUp = [Command.ScrollUp, Command.ScrollDown, Command.ScrollLeft, Command.ScrollRight];
    }

    private bool? ExtendCommand (Action extend)
    {
        extend ();

        return true;
    }

    private bool? MoveCaretByCollapsing (int delta)
    {
        MoveCaretByCollapsingSelection (delta);

        return true;
    }

    private bool? MoveCaretVerticallyCollapsing (int delta)
    {
        if (!Multiline)
        {
            return true;
        }

        MoveCaretVerticallyCollapsingSelection (delta);

        return true;
    }

    private bool? ScrollVerticalCommand (int delta)
    {
        // In single-line mode, vertical scroll is a no-op but must return true (handled)
        // to prevent the event from bubbling to parent containers.
        if (!Multiline)
        {
            return true;
        }

        if (_document is null || ScrollVertical (delta) != true)
        {
            return false;
        }

        SetNeedsDraw ();

        return true;
    }

    private bool? ScrollHorizontalCommand (int delta)
    {
        if (_document is null || ScrollHorizontal (delta) != true)
        {
            return false;
        }

        SetNeedsDraw ();

        return true;
    }

    private bool? InsertNewLineWithAutoIndent ()
    {
        if (ReadOnly || !Multiline)
        {
            return true;
        }

        // Wrap both the newline insertion and the auto-indent in a single undo group
        // so that one Ctrl+Z undoes the entire Enter operation.
        using (_document!.RunUpdate ())
        {
            if (HasSelection)
            {
                ReplaceSelection ("\n");
            }
            else
            {
                _document.Insert (CaretOffset, "\n");
            }

            // After the newline is inserted the caret sits at the start of the new line.
            // Ask the indentation strategy to fill in leading whitespace.
            if (IndentationStrategy is not { } strategy)
            {
                return true;
            }

            DocumentLine newLine = _document.GetLineByOffset (CaretOffset);
            strategy.IndentLine (_document, newLine);
        }

        return true;
    }

    private bool? InsertOrReplace (string text)
    {
        if (ReadOnly)
        {
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

        return true;
    }

    /// <summary>
    ///     Overwrites the grapheme at the caret with <paramref name="text" />. If the caret is at
    ///     line-end, falls back to a plain insert so the newline is not consumed.
    /// </summary>
    private void OverwriteAtCaret (string text)
    {
        OverwriteAtOffset (CaretOffset, text);
    }

    /// <summary>
    ///     Overwrites the grapheme at the given <paramref name="offset" /> with <paramref name="text" />.
    ///     If the offset is at line-end, falls back to a plain insert so the newline is not consumed.
    /// </summary>
    private void OverwriteAtOffset (int offset, string text)
    {
        DocumentLine line = _document!.GetLineByOffset (offset);
        var lineEnd = line.Offset + line.Length;

        if (offset >= lineEnd)
        {
            // At or past end-of-line content — just insert.
            _document.Insert (offset, text);

            return;
        }

        // Determine the length of the grapheme cluster under the caret so wide runes are
        // replaced atomically. StringInfo.GetNextTextElementLength gives cluster length in chars.
        var remaining = _document.GetText (offset, lineEnd - offset);
        var graphemeLength = StringInfo.GetNextTextElementLength (remaining);

        // Use RemoveAndInsert so that the caret anchor (AfterInsertion) moves past the
        // inserted text. The default same-length Replace uses CharacterReplace mode which
        // does not move anchors at all.
        _document.Replace (offset, graphemeLength, text, OffsetChangeMappingType.RemoveAndInsert);
    }

    private bool? DeleteLeft ()
    {
        if (ReadOnly)
        {
            return true;
        }

        if (HasSelection)
        {
            ReplaceSelection (string.Empty);
        }
        else if (TryDeleteIndentationLeft ())
        {
            return true;
        }
        else if (CaretOffset > 0)
        {
            var graphemeLen = GetGraphemeLengthBackward (CaretOffset);

            // If at start of line (graphemeLen == 0), remove the full delimiter to join with previous line (CRLF counts as 2).
            var removeLen = graphemeLen > 0 ? graphemeLen : GetDelimiterLengthBackwardAt (CaretOffset);
            _document!.Remove (CaretOffset - removeLen, removeLen);
        }

        return true;
    }

    private bool? DeleteRight ()
    {
        if (ReadOnly)
        {
            return true;
        }

        if (HasSelection)
        {
            ReplaceSelection (string.Empty);
        }
        else if (CaretOffset < _document!.TextLength)
        {
            var graphemeLen = GetGraphemeLengthForward (CaretOffset);

            // If at end of line (graphemeLen == 0), remove the full delimiter to join with next line (CRLF counts as 2).
            var removeLen = graphemeLen > 0 ? graphemeLen : GetDelimiterLengthForwardAt (CaretOffset);
            _document!.Remove (CaretOffset, removeLen);
        }

        return true;
    }

    private int GetDelimiterLengthForwardAt (int offset)
    {
        DocumentLine line = _document!.GetLineByOffset (offset);
        var delimiterStart = line.Offset + line.Length;
        var delimiterLen = line.DelimiterLength;

        if (delimiterLen == 0)
        {
            return 1;
        }

        // If already inside the delimiter (possible with older behavior), delete the remainder.
        if (offset > delimiterStart && offset < delimiterStart + delimiterLen)
        {
            return delimiterStart + delimiterLen - offset;
        }

        return delimiterLen;
    }

    private int GetDelimiterLengthBackwardAt (int offset)
    {
        DocumentLine line = _document!.GetLineByOffset (offset);

        // If already inside this line's delimiter, delete back to the end-of-line text.
        var lineStart = line.Offset;

        if (offset > lineStart + line.Length && offset <= lineStart + line.TotalLength)
        {
            return offset - (lineStart + line.Length);
        }

        DocumentLine? previous = line.PreviousLine;

        if (previous is null)
        {
            return 1;
        }

        return Math.Max (1, previous.DelimiterLength);
    }

    private bool? SetCaretAndReturnTrue (int offset)
    {
        CaretOffset = offset;

        return true;
    }

    private bool? MoveCaretToLineStart ()
    {
        if (!Multiline)
        {
            CaretOffset = 0;

            return true;
        }

        DocumentLine line = _document!.GetLineByOffset (CaretOffset);
        CaretOffset = line.Offset;

        return true;
    }

    private bool? MoveCaretToLineEnd ()
    {
        if (!Multiline)
        {
            CaretOffset = _document!.TextLength;

            return true;
        }

        DocumentLine line = _document!.GetLineByOffset (CaretOffset);
        CaretOffset = line.Offset + line.Length;

        return true;
    }

    /// <summary>
    ///     Returns the length (in UTF-16 code units) of the grapheme cluster starting at
    ///     <paramref name="offset" />. Returns 0 if <paramref name="offset" /> is at or beyond
    ///     the end of the line's text content.
    /// </summary>
    private int GetGraphemeLengthForward (int offset)
    {
        DocumentLine line = _document!.GetLineByOffset (offset);
        var lineEnd = line.Offset + line.Length;

        if (offset >= lineEnd)
        {
            // At or past end-of-line text — the delimiter is handled separately.
            return 0;
        }

        // GetTextAsMemory avoids a string allocation when the rope implementation supports it.
        // We pass the full remainder so StringInfo sees the complete cluster even for
        // pathological sequences (base + many combining marks).
        ReadOnlyMemory<char> slice = _document.GetTextAsMemory (offset, lineEnd - offset);

        return StringInfo.GetNextTextElementLength (slice.Span);
    }

    /// <summary>
    ///     Returns the length (in UTF-16 code units) of the grapheme cluster ending at
    ///     <paramref name="offset" /> (i.e. the cluster immediately before the offset).
    ///     Returns 0 if <paramref name="offset" /> is at the start of the line.
    /// </summary>
    private int GetGraphemeLengthBackward (int offset)
    {
        DocumentLine line = _document!.GetLineByOffset (offset);
        var lineStart = line.Offset;

        if (offset <= lineStart)
        {
            // At or before the start of this line — delimiter crossing handled by caller.
            return 0;
        }

        // Scan from line start to the caret so TextElementEnumerator sees aligned grapheme
        // boundaries. The last enumerated element is the grapheme immediately before the caret.
        var text = _document.GetText (lineStart, offset - lineStart);
        TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator (text);
        var lastLength = 0;

        while (enumerator.MoveNext ())
        {
            lastLength = enumerator.GetTextElement ().Length;
        }

        return lastLength;
    }

    /// <summary>
    ///     Kill from caret to end of line. If the caret is already at EOL, kills the line delimiter
    ///     (joining the next line). Places killed text on the clipboard; if the immediately preceding
    ///     command was also a kill (consecutive kill), appends instead of replacing.
    /// </summary>
    private bool? CutToEndOfLine ()
    {
        // For keyboard dispatch, _previousCommandWasKill was set by OnKeyDown.
        // For InvokeCommand dispatch, fall back to _lastCommandWasKill (OnKeyDown was not called).
        var consecutiveKill = _previousCommandWasKill || _lastCommandWasKill;
        _lastCommandWasKill = false;

        if (ReadOnly || _document is null)
        {
            return true;
        }

        if (HasSelection)
        {
            ReplaceSelection (string.Empty);

            return true;
        }

        DocumentLine line = _document.GetLineByOffset (CaretOffset);
        var lineEnd = line.Offset + line.Length;

        int start;
        int length;

        if (CaretOffset < lineEnd)
        {
            // Kill from caret to end of line text (not the delimiter).
            start = CaretOffset;
            length = lineEnd - CaretOffset;
        }
        else if (line.DelimiterLength > 0)
        {
            // Caret is at EOL — kill the line delimiter (join with next line).
            start = lineEnd;
            length = line.DelimiterLength;
        }
        else
        {
            // Last line, caret at end — nothing to kill.

            return true;
        }

        var killed = _document.GetText (start, length);

        if (!WriteKillToClipboard (killed, consecutiveKill, false))
        {
            return true;
        }

        using (_document.RunUpdate ())
        {
            _document.Remove (start, length);
        }

        _lastCommandWasKill = true;

        return true;
    }

    /// <summary>
    ///     Kill from line start to caret. Places killed text on the clipboard; if the immediately
    ///     preceding command was also a kill, prepends instead of replacing (so the clipboard
    ///     accumulates text in document order).
    /// </summary>
    private bool? CutToStartOfLine ()
    {
        // See CutToEndOfLine for rationale on the dual-path flag check.
        var consecutiveKill = _previousCommandWasKill || _lastCommandWasKill;
        _lastCommandWasKill = false;

        if (ReadOnly || _document is null)
        {
            return true;
        }

        if (HasSelection)
        {
            ReplaceSelection (string.Empty);

            return true;
        }

        DocumentLine line = _document.GetLineByOffset (CaretOffset);
        var start = line.Offset;
        var length = CaretOffset - start;

        if (length == 0)
        {
            // Already at BOL — nothing to kill.
            return true;
        }

        var killed = _document.GetText (start, length);

        if (!WriteKillToClipboard (killed, false, consecutiveKill))
        {
            return true;
        }

        using (_document.RunUpdate ())
        {
            _document.Remove (start, length);
        }

        _lastCommandWasKill = true;

        return true;
    }

    /// <summary>
    ///     Writes killed text to the clipboard. When <paramref name="append" /> or
    ///     <paramref name="prepend" /> is <see langword="true" />, reads the current clipboard
    ///     content and concatenates rather than replacing.
    /// </summary>
    /// <returns><see langword="true" /> if the clipboard write succeeded; <see langword="false" /> otherwise.</returns>
    private bool WriteKillToClipboard (string killed, bool append, bool prepend)
    {
        IClipboard? clipboard = App?.Clipboard;

        if (clipboard is null)
        {
            return false;
        }

        if ((append || prepend) && clipboard.TryGetClipboardData (out var existing))
        {
            killed = prepend ? killed + existing : existing + killed;
        }

        return clipboard.TrySetClipboardData (killed);
    }

    private bool? InvokeFindRequested ()
    {
        FindRequested?.Invoke (this, EventArgs.Empty);

        return true;
    }

    private bool? InvokeReplaceRequested ()
    {
        ReplaceRequested?.Invoke (this, EventArgs.Empty);

        return true;
    }

    private bool? FindNextCommand ()
    {
        FindNext ();

        return true;
    }

    private bool? FindPreviousCommand ()
    {
        FindPrevious ();

        return true;
    }

    private bool? ToggleFoldUnderCaret ()
    {
        if (FoldingManager is not { } fm || _document is null)
        {
            return true;
        }

        var caretOffset = CaretOffset;
        DocumentLine caretLine = _document.GetLineByOffset (caretOffset);

        // First, try to find a fold starting on this line.
        FoldingSection? fold = fm.GetFoldingAtLine (caretLine.LineNumber);

        // If none, try folds containing the caret.
        if (fold is null)
        {
            foreach (FoldingSection fs in fm.GetFoldingsContaining (caretOffset))
            {
                fold = fs;

                break;
            }
        }

        fold?.IsFolded = !fold.IsFolded;

        return true;
    }
}
