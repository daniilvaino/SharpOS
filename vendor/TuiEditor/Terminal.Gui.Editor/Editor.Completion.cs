using System.Collections.ObjectModel;
using System.Drawing;
using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Editor.Completion;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Terminal.Gui.Editor;

public partial class Editor
{
    private IReadOnlyList<CompletionItem> _completionItems = [];
    private ListView? _completionListView;
    private Popover<ListView, CompletionItem?>? _completionPopover;
    private int _completionPrefixStart;

    /// <summary>
    ///     Gets or sets the completion provider that supplies suggestions for the in-editor
    ///     autocomplete popup. Set to <see langword="null" /> to disable completion.
    /// </summary>
    public IEditorCompletionProvider? CompletionProvider
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            // Dismiss stale completion when the provider changes — prevents accepting
            // suggestions from the previous provider after a swap.
            if (IsCompletionActive)
            {
                DismissCompletion ();
            }

            field = value;

            if (value is null)
            {
                DismissCompletion ();
            }
        }
    }

    /// <summary>Whether the completion session is currently active (items are available).</summary>
    public bool IsCompletionActive => _completionItems.Count > 0;

    /// <summary>Gets the zero-based index of the currently selected completion item.</summary>
    internal int CompletionSelectedIndex { get; private set; }

    /// <summary>
    ///     Extracts the word-prefix immediately before the caret (letters, digits, underscores)
    ///     for use as the completion filter string. Returns empty when the caret follows
    ///     whitespace or punctuation.
    /// </summary>
    internal string GetCompletionPrefix ()
    {
        return GetCompletionPrefix (out _);
    }

    /// <summary>
    ///     Extracts the word-prefix immediately before the caret and also returns the document
    ///     offset where the prefix starts.
    /// </summary>
    internal string GetCompletionPrefix (out int prefixStart)
    {
        if (_document is null)
        {
            prefixStart = 0;

            return string.Empty;
        }

        var offset = CaretOffset;
        var start = offset;

        while (start > 0)
        {
            var ch = _document.GetCharAt (start - 1);

            if (!char.IsLetterOrDigit (ch) && ch != '_')
            {
                break;
            }

            start--;
        }

        prefixStart = start;

        return start < offset ? _document.GetText (start, offset - start) : string.Empty;
    }

    /// <summary>
    ///     Called before normal key dispatch. Returns <see langword="true" /> when the completion
    ///     popup consumed the key (navigation / accept / dismiss). This runs
    ///     in <see cref="OnKeyDown" />, which fires before command bindings.
    /// </summary>
    internal bool HandleCompletionKey (Key key)
    {
        if (CompletionProvider is null)
        {
            return false;
        }

        // An active popup gets first crack at navigation keys.
        if (IsCompletionActive)
        {
            // Esc (the application's Command.Quit key — the Editor binds no Quit, so this
            // must come from the app, not the Editor-scoped KeyMatches) or a horizontal
            // caret move (Left/Right) dismisses the popup. Up/Down are intentionally absent:
            // the focused popup ListView consumes them to move the selection.
            if (key == Application.GetDefaultKey (Command.Quit) || KeyMatches (Command.Left) ||
                KeyMatches (Command.Right))
            {
                DismissCompletion ();

                return false;
            }

            // Accept on the keys bound to NewLine (Enter) / InsertTab (Tab). SPACE is
            // deliberately NOT an accept key: it falls through, inserts a space, and the
            // now-empty prefix dismisses the popup (so "this is a test." stays intact).
            if (KeyMatches (Command.NewLine) || KeyMatches (Command.InsertTab))
            {
                AcceptCompletion ();

                return true;
            }

            // Printable keys and Backspace edit through the same canonical path the
            // editor uses without the popup open (multi-caret / selection / overwrite
            // aware), then re-filter. The popup is focused, so these never reach
            // OnKeyDownNotHandled on their own — they must be handled here.
            if (key is { IsCtrl: false, IsAlt: false, AsRune: var rune } && !Rune.IsControl (rune))
            {
                return InsertTypedText (rune.ToString ());
            }

            if (KeyMatches (Command.DeleteCharLeft))
            {
                DeleteCharLeftAndRefresh ();

                return true;
            }
        }

        // Check provider-specific triggers (e.g. Ctrl+Space).
        if (!CompletionProvider.ShouldTrigger (key))
        {
            return false;
        }

        ShowCompletion ();

        return true;

        // Resolve a key against the Editor's own bindings instead of hardcoding literals,
        // so completion follows any rebinding of these commands.
        bool KeyMatches (Command command)
        {
            return KeyBindings.GetFirstFromCommands (command) is { } bound && key == bound;
        }
    }

    /// <summary>
    ///     Handles mouse events for the completion popup: a single click inside the popup
    ///     area selects and accepts the clicked item; a click outside dismisses the popup.
    ///     The hit-test maps screen Y to an item index and does not account for a scrolled
    ///     list (only the first <see cref="ShowCompletionPopup" /> page is addressable).
    /// </summary>
    internal bool HandleCompletionMouse (Mouse mouse)
    {
        if (!IsCompletionActive || _completionPopover is null || _completionListView is null)
        {
            return false;
        }

        if (!mouse.IsSingleClicked)
        {
            return false;
        }

        // Map the click's screen position to the popup's content area. Terminal.Gui 2.5's
        // Popover is a screen-filling transparent overlay; the visible popup rectangle is
        // the ContentView (the ListView), positioned in screen coordinates.
        Rectangle popoverScreenFrame = _completionListView.FrameToScreen ();

        if (mouse.ScreenPosition.X < popoverScreenFrame.X
            || mouse.ScreenPosition.X >= popoverScreenFrame.Right
            || mouse.ScreenPosition.Y < popoverScreenFrame.Y
            || mouse.ScreenPosition.Y >= popoverScreenFrame.Bottom)
        {
            // Click is outside the popup — dismiss.
            DismissCompletion ();

            return false;
        }

        // Determine which item was clicked by Y offset within the Popover.
        var clickedIdx = mouse.ScreenPosition.Y - popoverScreenFrame.Y;

        if (clickedIdx < 0 || clickedIdx >= _completionItems.Count)
        {
            return false;
        }

        CompletionSelectedIndex = clickedIdx;
        AcceptCompletion ();

        return true;
    }

    /// <summary>
    ///     Called after a character is inserted into the document. Refreshes or opens the
    ///     completion popup if a provider is active.
    /// </summary>
    internal void NotifyCompletionAfterInsert ()
    {
        if (CompletionProvider is null)
        {
            return;
        }

        var prefix = GetCompletionPrefix (out var prefixStart);

        // Filter-as-you-type: an empty prefix means the caret moved off the word
        // (e.g. Backspace past it), so close instead of re-querying for everything.
        // This is the one intentional difference from ShowCompletion.
        if (prefix.Length == 0)
        {
            DismissCompletion ();

            return;
        }

        QueryAndShowCompletion (prefix, prefixStart);
    }

    /// <summary>Opens the completion popup, querying the provider for items.</summary>
    internal void ShowCompletion ()
    {
        if (CompletionProvider is null || _document is null)
        {
            return;
        }

        // Explicit trigger (e.g. Ctrl+Space): query even on an empty prefix so a
        // provider can offer a full list — unlike the filter-as-you-type path.
        var prefix = GetCompletionPrefix (out var prefixStart);

        QueryAndShowCompletion (prefix, prefixStart);
    }

    /// <summary>
    ///     Shared body of <see cref="NotifyCompletionAfterInsert" /> and
    ///     <see cref="ShowCompletion" />: query the provider, dismiss if it returns
    ///     nothing, otherwise capture state and (re)show the popup. Callers guard
    ///     <see cref="CompletionProvider" /> / <see cref="_document" /> first.
    /// </summary>
    private void QueryAndShowCompletion (string prefix, int prefixStart)
    {
        IReadOnlyList<CompletionItem> items =
            CompletionProvider!.GetCompletions (_document!, CaretOffset, prefix);

        if (items.Count == 0)
        {
            DismissCompletion ();

            return;
        }

        _completionPrefixStart = prefixStart;
        _completionItems = items;
        CompletionSelectedIndex = 0;
        ShowCompletionPopup ();
    }

    /// <summary>Tears down the completion session: disposes the popup and clears the item list.</summary>
    internal void DismissCompletion ()
    {
        DisposeCompletionPopover ();
        _completionItems = [];
    }

    /// <summary>
    ///     Disposes the popover and its ListView (if any) without clearing
    ///     <see cref="_completionItems" /> — used both to dismiss and to swap in a fresh popup.
    /// </summary>
    /// <remarks>
    ///     Null-out-then-dispose order plus unsubscribing first makes this reentrant-safe:
    ///     disposing the popover flips <c>Visible</c>, but the handler is already detached and
    ///     the field already <see langword="null" />, so re-entry is a no-op.
    /// </remarks>
    private void DisposeCompletionPopover ()
    {
        Popover<ListView, CompletionItem?>? popover = _completionPopover;
        _completionPopover = null;
        _completionListView = null;

        if (popover is null)
        {
            return;
        }

        popover.VisibleChanged -= OnCompletionPopoverVisibleChanged;
        popover.Visible = false;
        popover.Dispose ();
    }

    /// <summary>
    ///     Terminal.Gui auto-hides the popover on Esc, a click outside it, focus change, or
    ///     another popover opening — it just flips <c>Visible</c> and never tells the Editor.
    ///     Without this, <see cref="IsCompletionActive" /> would stay <see langword="true" />
    ///     and a subsequent Enter/click would fire a phantom <see cref="AcceptCompletion" />.
    /// </summary>
    private void OnCompletionPopoverVisibleChanged (object? sender, EventArgs e)
    {
        if (_completionPopover is { Visible: false })
        {
            DismissCompletion ();
        }
    }

    /// <summary>
    ///     Accepts the currently selected completion item: replaces the prefix with the
    ///     item's insert text inside a single undo group.
    /// </summary>
    internal void AcceptCompletion ()
    {
        if (!IsCompletionActive || _document is null || _completionItems.Count == 0)
        {
            return;
        }

        if (CompletionSelectedIndex < 0 || CompletionSelectedIndex >= _completionItems.Count)
        {
            DismissCompletion ();

            return;
        }

        CompletionItem selected = _completionItems[CompletionSelectedIndex];
        var insertText = selected.TextToInsert;
        var replaceLength = CaretOffset - _completionPrefixStart;

        DismissCompletion ();

        // Single undo step for the replacement.
        using (_document.RunUpdate ())
        {
            if (replaceLength > 0)
            {
                _document.Replace (_completionPrefixStart, replaceLength, insertText);
            }
            else
            {
                _document.Insert (CaretOffset, insertText);
            }
        }
    }

    private void ShowCompletionPopup ()
    {
        if (_completionItems.Count == 0)
        {
            return;
        }

        ObservableCollection<string> labels = new (_completionItems.Select (i => i.Label));

        // Cap visible height at 10 items to avoid oversized popups.
        var visibleCount = Math.Min (_completionItems.Count, 10);

        // Width in display columns, not char count — wide/CJK graphemes are 2 cells.
        var width = _completionItems.Max (i => Math.Max (0, i.Label.GetColumns ())) + 2;

        // Filter-as-you-type refresh: reuse the live popover/ListView rather than
        // disposing and rebuilding (Popover + ListView + 3 event subscriptions) on
        // every keystroke — that flickered and churned allocations.
        if (_completionListView is not null && _completionPopover is not null)
        {
            _completionListView.Source = new ListWrapper<string> (labels);
            _completionListView.Width = width;
            _completionListView.Height = visibleCount;
            _completionListView.SelectedItem = CompletionSelectedIndex;

            Point caret = GetCaretScreenPosition ();
            _completionPopover.MakeVisible (caret with { Y = caret.Y + 1 });

            return;
        }

        // First show — build the ListView + Popover and wire events once.
        _completionListView = new ListView
        {
            Source = new ListWrapper<string> (labels),
            Width = width,
            Height = visibleCount,
            TabStop = TabBehavior.NoStop
        };
        // The ListView owns no accept/dismiss semantics: HandleCompletionKey resolves
        // accept (Enter/Tab) and dismiss (Esc/Left/Right) at the Editor level, while the
        // focused ListView's own Up/Down move the selection. Binding Space->Accept here
        // would silently re-introduce the "this is a test." accept-on-space bug. Disable
        // type-ahead so a stray letter can't hijack the list instead of reaching the editor.
        _completionListView.KeystrokeNavigator = null;
        _completionListView.MouseBindings.ReplaceCommands (MouseFlags.LeftButtonClicked, Command.Accept);
        _completionListView.SelectedItem = CompletionSelectedIndex;

        _completionPopover = new Popover<ListView, CompletionItem?> (_completionListView)
        {
            Target = new WeakReference<View> (this),

            // Reference the live _completionItems (not a captured snapshot) so the
            // in-place refresh above stays consistent.
            ResultExtractor = lv =>
            {
                if (lv.SelectedItem is not { } idx || idx < 0 || idx >= _completionItems.Count)
                {
                    return null;
                }

                return _completionItems[idx];
            },
            TabStop = TabBehavior.NoStop
        };

        // Tear the session down when TG auto-hides the popover (Esc / click-outside /
        // focus change), so IsCompletionActive can't go stale and trigger a phantom accept.
        _completionPopover.VisibleChanged += OnCompletionPopoverVisibleChanged;

        // Position the popup just below the caret.
        Point caretScreen = GetCaretScreenPosition ();
        _completionPopover.MakeVisible (caretScreen with { Y = caretScreen.Y + 1 });

        // The focused ListView's Up/Down move its selection; mirror that into
        // CompletionSelectedIndex so AcceptCompletion inserts the right item.
        // Accept/dismiss keys are resolved separately in HandleCompletionKey.
        _completionListView.ValueChanged += (_, args) =>
        {
            if (args.NewValue is not null)
            {
                CompletionSelectedIndex = args.NewValue.Value;
            }
        };

        // Accepting fires on BOTH Enter and mouse-click. Key-driven acceptance is handled
        // explicitly by HandleCompletionKey (Enter/Tab) — calling AcceptCompletion here for
        // keys double-handled Enter and leaked a trailing newline — so keys only sync the
        // selected index (like ValueChanged above). Mouse clicks are different in TG 2.5:
        // the screen-filling Popover overlay routes popup clicks to the ListView, so the
        // Editor's OnMouseEvent/HandleCompletionMouse never sees them — accept here when
        // the Accept came from a mouse binding. Mark Handled so Popover does not bridge
        // Command.Accept to the Editor Target (that would submit a hosting dialog).
        _completionListView.Accepting += (sender, args) =>
        {
            if (args.Context?.Value is int idx)
            {
                CompletionSelectedIndex = idx;
            }

            if (args.Context?.Binding is not MouseBinding { MouseEvent: { Position: { } clickPosition } })
            {
                return;
            }

            // The click's Position is ListView-viewport-relative; honor scroll via Viewport.Y.
            var clickedIdx = clickPosition.Y + ((ListView)sender!).Viewport.Y;

            if (clickedIdx >= 0 && clickedIdx < _completionItems.Count)
            {
                CompletionSelectedIndex = clickedIdx;
            }

            AcceptCompletion ();
            args.Handled = true;
        };
    }

    /// <summary>
    ///     Computes the screen position of the caret (for popup anchoring).
    /// </summary>
    private Point GetCaretScreenPosition ()
    {
        if (_document is null)
        {
            return Point.Empty;
        }

        Rectangle viewport = Viewport;
        var caretLine = GetCaretVisibleLineIndex ();
        var caretCol = GetCaretColumn ();
        var row = caretLine - viewport.Y;
        var col = caretCol - viewport.X;

        return ViewportToScreen (new Point (col, row));
    }
}
