using System.Collections.Immutable;
using System.Collections.ObjectModel;
using Terminal.Gui.Configuration;
using Terminal.Gui.Editor.Document;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Terminal.Gui.Editor;

/// <summary>
///     A pre-wired <see cref="StatusBar" /> that displays standard editor indicators bound to an
///     <see cref="Editor" /> instance: language name, theme dropdown, load progress spinner,
///     overwrite/insert mode, and line/column position.
/// </summary>
/// <remarks>
///     <para>
///         Consumers can append custom indicators using the standard <see cref="View.Add(View[])" /> API
///         after construction.
///     </para>
///     <para>
///         For multi-file scenarios (TabView, split panes) use the
///         <see cref="EditorStatusBar(Func{Editor})" /> constructor. Call <see cref="SwitchEditor" />
///         when the active editor changes to re-subscribe events.
///     </para>
/// </remarks>
public class EditorStatusBar : StatusBar
{
    private readonly Func<Editor> _editorProvider;
    private Editor? _subscribedEditor;

    /// <summary>Initializes a new <see cref="EditorStatusBar" /> wired to a single <see cref="Editor" />.</summary>
    /// <param name="editor">The editor instance whose state is displayed.</param>
    public EditorStatusBar (Editor editor) : this (() => editor)
    {
        ArgumentNullException.ThrowIfNull (editor);
    }

    /// <summary>
    ///     Initializes a new <see cref="EditorStatusBar" /> for multi-file scenarios where the active
    ///     editor can change (e.g. TabView, split panes).
    /// </summary>
    /// <param name="activeEditorProvider">
    ///     A delegate that returns the currently active <see cref="Editor" />.
    /// </param>
    public EditorStatusBar (Func<Editor> activeEditorProvider)
    {
        ArgumentNullException.ThrowIfNull (activeEditorProvider);
        _editorProvider = activeEditorProvider;

        AlignmentModes = AlignmentModes.IgnoreFirstOrLast;

        LanguageShortcut = new Shortcut (Key.Empty, "Plain Text", null) { MouseHighlightStates = MouseState.None };

        ImmutableList<string> themeNames = ThemeManager.GetThemeNames ();
        ThemeDropDown = new DropDownList
        {
            Source = new ListWrapper<string> (new ObservableCollection<string> (themeNames)),
            Text = ThemeManager.Theme,
            ReadOnly = true
        };

        ThemeDropDown.ValueChanged += (_, _) =>
        {
            var selected = ThemeDropDown.Text;

            if (!string.IsNullOrEmpty (selected) && selected != ThemeManager.Theme)
            {
                ThemeManager.Theme = selected;
            }
        };

        LoadStatusSpinner = new SpinnerView
        {
            Style = new SpinnerStyle.Aesthetic (),
            Width = 8,
            AutoSpin = false,
            Visible = false
        };

        LoadSpinnerShortcut = new Shortcut
        {
            CommandView = LoadStatusSpinner,
            Title = string.Empty,
            MouseHighlightStates = MouseState.None
        };

        OverwriteShortcut = new Shortcut (Key.Empty, "INS", null) { MouseHighlightStates = MouseState.None };
        LocShortcut = new Shortcut (Key.Empty, FormatLoc (1, 1), null) { MouseHighlightStates = MouseState.None };

        BuildShortcuts ();
        SubscribeToEditor (ActiveEditor);
        UpdateLocShortcut ();
        UpdateOverwriteShortcut ();
        UpdateLanguageShortcut ();
    }

    /// <summary>The status-bar shortcut that displays the current syntax-highlighting language name.</summary>
    public Shortcut LanguageShortcut { get; }

    /// <summary>The status-bar dropdown that selects <see cref="ThemeManager.Theme" />.</summary>
    public DropDownList ThemeDropDown { get; }

    /// <summary>The spinner view shown while streaming file load/save is running.</summary>
    public SpinnerView LoadStatusSpinner { get; }

    /// <summary>The status-bar shortcut that hosts <see cref="LoadStatusSpinner" />.</summary>
    public Shortcut LoadSpinnerShortcut { get; }

    /// <summary>
    ///     The status-bar shortcut that shows whether the editor is in insert (INS) or overwrite (OVR)
    ///     mode.
    /// </summary>
    public Shortcut OverwriteShortcut { get; }

    /// <summary>
    ///     The status-bar shortcut that mirrors the editor's caret position. Both line and column are
    ///     1-based.
    /// </summary>
    public Shortcut LocShortcut { get; }

    /// <summary>Gets the currently active <see cref="Editor" /> from the provider delegate.</summary>
    public Editor ActiveEditor => _editorProvider ();

    /// <summary>
    ///     Re-subscribes event handlers to the current active editor. Call when the active editor
    ///     changes in multi-file scenarios.
    /// </summary>
    public void SwitchEditor ()
    {
        UnsubscribeFromEditor ();
        SubscribeToEditor (ActiveEditor);
        UpdateLocShortcut ();
        UpdateOverwriteShortcut ();
        UpdateLanguageShortcut ();
    }

    /// <summary>
    ///     Updates the language indicator text. Call when a file is opened or the
    ///     <see cref="Editor.HighlightingDefinition" /> changes.
    /// </summary>
    public void UpdateLanguageShortcut ()
    {
        LanguageShortcut.Title = ActiveEditor.HighlightingDefinition?.Name ?? "Plain Text";
        LanguageShortcut.SetNeedsDraw ();
    }

    /// <summary>Updates the Ln/Col indicator from the current caret position.</summary>
    public void UpdateLocShortcut ()
    {
        Editor editor = ActiveEditor;
        TextDocument? document = editor.Document;

        if (document is null)
        {
            LocShortcut.Title = FormatLoc (1, 1);
        }
        else
        {
            DocumentLine line = document.GetLineByOffset (editor.CaretOffset);
            var loc = FormatLoc (line.LineNumber, editor.CaretOffset - line.Offset + 1);

            if (editor.HasMultipleCarets)
            {
                loc += $" ({editor.AdditionalCaretOffsets.Count + 1} carets)";
            }

            LocShortcut.Title = loc;
        }

        LocShortcut.SetNeedsDraw ();
    }

    /// <summary>Updates the INS/OVR indicator from the current overwrite mode.</summary>
    public void UpdateOverwriteShortcut ()
    {
        OverwriteShortcut.Title = ActiveEditor.OverwriteMode ? "OVR" : "INS";
        OverwriteShortcut.SetNeedsDraw ();
    }

    /// <inheritdoc />
    protected override void Dispose (bool disposing)
    {
        if (disposing)
        {
            UnsubscribeFromEditor ();
        }

        base.Dispose (disposing);
    }

    private void BuildShortcuts ()
    {
        Add (
            LanguageShortcut,
            new Shortcut { Title = "Theme", CommandView = ThemeDropDown },
            LoadSpinnerShortcut,
            OverwriteShortcut,
            LocShortcut);
    }

    private void SubscribeToEditor (Editor editor)
    {
        _subscribedEditor = editor;
        editor.CaretChanged += OnCaretChanged;
        editor.OverwriteModeChanged += OnOverwriteModeChanged;
    }

    private void UnsubscribeFromEditor ()
    {
        if (_subscribedEditor is null)
        {
            return;
        }

        _subscribedEditor.CaretChanged -= OnCaretChanged;
        _subscribedEditor.OverwriteModeChanged -= OnOverwriteModeChanged;
        _subscribedEditor = null;
    }

    private void OnCaretChanged (object? sender, EventArgs e)
    {
        UpdateLocShortcut ();
    }

    private void OnOverwriteModeChanged (object? sender, EventArgs e)
    {
        UpdateOverwriteShortcut ();
    }

    private static string FormatLoc (int line, int column)
    {
        return $"Ln {line}, Col {column}";
    }
}
