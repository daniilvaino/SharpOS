using Terminal.Gui.Editor.Indentation;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Terminal.Gui.Editor;

/// <summary>
///     Settings tab for editor indentation and tab behavior.
/// </summary>
public sealed class EditorTabSettingsTab : View
{
    private readonly CheckBox _autoIndentCheck;
    private readonly CheckBox _convertTabsCheck;
    private readonly NumericUpDown<int> _indentSize;

    /// <summary>
    ///     Initializes a new <see cref="EditorTabSettingsTab" /> from the current editor settings.
    /// </summary>
    /// <param name="editor">The editor whose tab settings should be displayed.</param>
    public EditorTabSettingsTab (Editor editor)
    {
        ArgumentNullException.ThrowIfNull (editor);

        Title = "_Tab Settings";

        View label = new Label { Text = "_Indent size:" };
        _indentSize = new NumericUpDown<int>
        {
            X = Pos.Right (label) + 1,
            Value = editor.IndentationSize
        };
        _indentSize.ValueChanging += (_, e) =>
        {
            if (e.NewValue < 1)
            {
                e.Handled = true;
            }
        };

        _convertTabsCheck = new CheckBox
        {
            Y = Pos.Bottom (_indentSize),
            Title = "Con_vert Tabs to Spaces",
            Value = editor.ConvertTabsToSpaces ? CheckState.Checked : CheckState.UnChecked
        };

        _autoIndentCheck = new CheckBox
        {
            Y = Pos.Bottom (_convertTabsCheck),
            Title = "_Auto Indent",
            Value = editor.IndentationStrategy is not null ? CheckState.Checked : CheckState.UnChecked
        };

        Add (
            label,
            _indentSize,
            _convertTabsCheck,
            _autoIndentCheck);
    }

    /// <summary>
    ///     Applies the accepted tab settings to the editor.
    /// </summary>
    /// <param name="editor">The editor to update.</param>
    public void ApplyTo (Editor editor)
    {
        ArgumentNullException.ThrowIfNull (editor);

        editor.IndentationSize = Math.Max (1, _indentSize.Value);
        editor.ConvertTabsToSpaces = _convertTabsCheck.Value == CheckState.Checked;
        editor.IndentationStrategy = _autoIndentCheck.Value == CheckState.Checked
            ? new DefaultIndentationStrategy ()
            : null;
    }
}
