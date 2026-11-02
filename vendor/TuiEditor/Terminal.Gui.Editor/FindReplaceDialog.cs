using Terminal.Gui.Editor.Document.Search;
using Terminal.Gui.Resources;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Terminal.Gui.Editor;

/// <summary>
///     A pre-wired Find/Replace dialog for <see cref="Editor" />. Provides tabbed Find and Replace panes
///     with support for case-sensitive, whole-word, and regex search modes.
/// </summary>
public sealed class FindReplaceDialog : Dialog
{
    private readonly TextField _findTextField;
    private readonly CheckBox _matchCaseCheckBox;
    private readonly CheckBox _regexCheckBox;
    private readonly TextField _replaceFindTextField;
    private readonly TextField _replaceWithTextField;
    private readonly Label _statusLabel;
    private readonly CheckBox _wholeWordCheckBox;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FindReplaceDialog" /> class.
    /// </summary>
    /// <param name="editor">The <see cref="Editor" /> instance to search within.</param>
    /// <param name="selectReplaceTab">
    ///     If <see langword="true" />, the Replace tab is focused on open; otherwise the Find tab is focused.
    /// </param>
    public FindReplaceDialog (Editor editor, bool selectReplaceTab)
    {
        ArgumentNullException.ThrowIfNull (editor);

        Title = "Find / Replace";
        Width = Dim.Percent (70);
        Height = Dim.Auto ();

        var initialSearchText = string.Empty;

        if (editor is { HasSelection: true, Document: { } document })
        {
            var start = Math.Clamp (editor.SelectionStart, 0, document.TextLength);
            var end = Math.Clamp (editor.SelectionEnd, start, document.TextLength);
            initialSearchText = document.Text.Substring (start, end - start);
        }

        _findTextField = new TextField { X = 11, Y = 1, Width = Dim.Fill (2), Text = initialSearchText };
        _replaceFindTextField = new TextField { X = 11, Y = 1, Width = Dim.Fill (2), Text = initialSearchText };
        _replaceWithTextField = new TextField { X = 11, Y = 3, Width = Dim.Fill (2) };

        _matchCaseCheckBox = new CheckBox { X = 1, Y = 0, Title = "Match _case" };
        _wholeWordCheckBox = new CheckBox { X = Pos.Right (_matchCaseCheckBox) + 2, Y = 0, Title = "_Whole word" };
        _regexCheckBox = new CheckBox { X = Pos.Right (_wholeWordCheckBox) + 2, Y = 0, Title = "Reg_ex" };
        _statusLabel = new Label { X = 1, Y = 1, Width = Dim.Fill (1) };

        Tabs tabs = new () { X = 0, Y = 0, Width = Dim.Fill (), Height = 11 };
        View findTab = BuildFindTab (editor);
        View replaceTab = BuildReplaceTab (editor);

        tabs.Add (findTab);
        tabs.Add (replaceTab);
        tabs.Value = selectReplaceTab ? replaceTab : findTab;

        _matchCaseCheckBox.Y = Pos.Bottom (tabs);
        _wholeWordCheckBox.Y = Pos.Bottom (tabs);
        _regexCheckBox.Y = Pos.Bottom (tabs);
        _statusLabel.Y = Pos.Bottom (tabs) + 1;

        AddButton (new Button { Text = Strings.cmdClose });
        Add (tabs, _matchCaseCheckBox, _wholeWordCheckBox, _regexCheckBox, _statusLabel);

        if (selectReplaceTab)
        {
            _replaceFindTextField.SetFocus ();

            return;
        }

        _findTextField.SetFocus ();
    }

    private View BuildFindTab (Editor editor)
    {
        View tab = new () { Title = "_Find" };
        Button findNextButton = new () { X = 1, Y = 3, Text = "Find _Next", IsDefault = true };
        Button findPreviousButton = new () { X = Pos.Right (findNextButton) + 1, Y = 3, Text = "Find _Previous" };

        tab.Add (
            new Label { X = 1, Y = 1, Text = "F_ind:" },
            _findTextField,
            findNextButton,
            findPreviousButton);

        findNextButton.Accepting += (_, e) =>
        {
            RunFind (editor, _findTextField.Text, true);
            e.Handled = true;
        };

        findPreviousButton.Accepting += (_, e) =>
        {
            RunFind (editor, _findTextField.Text, false);
            e.Handled = true;
        };

        return tab;
    }

    private View BuildReplaceTab (Editor editor)
    {
        View tab = new () { Title = "_Replace" };

        tab.Add (
            new Label { X = 1, Y = 1, Text = "F_ind:" },
            _replaceFindTextField,
            new Label { X = 1, Y = 3, Text = "Replace:" },
            _replaceWithTextField);

        Button findNextButton = new () { X = 1, Y = 5, Text = "Find _Next", IsDefault = true };
        Button replaceButton = new () { X = Pos.Right (findNextButton) + 1, Y = 5, Text = "R_eplace" };
        Button replaceAllButton = new () { X = Pos.Right (replaceButton) + 1, Y = 5, Text = "Replace _All" };

        findNextButton.Accepting += (_, e) =>
        {
            RunFind (editor, _replaceFindTextField.Text, true);
            e.Handled = true;
        };

        replaceButton.Accepting += (_, e) =>
        {
            RunReplaceNext (editor);
            e.Handled = true;
        };

        replaceAllButton.Accepting += (_, e) =>
        {
            RunReplaceAll (editor);
            e.Handled = true;
        };

        tab.Add (findNextButton, replaceButton, replaceAllButton);

        return tab;
    }

    private void RunFind (Editor editor, string searchText, bool forward)
    {
        if (!TryBuildStrategy (editor, searchText))
        {
            return;
        }

        var found = forward ? editor.FindNext () : editor.FindPrevious ();
        _statusLabel.Text = found ? string.Empty : $"No match for \"{searchText}\".";
    }

    private void RunReplaceNext (Editor editor)
    {
        if (!TryBuildStrategy (editor, _replaceFindTextField.Text))
        {
            return;
        }

        var replaced = editor.ReplaceNext (_replaceWithTextField.Text);
        _statusLabel.Text = replaced ? string.Empty : $"No match for \"{_replaceFindTextField.Text}\".";
    }

    private void RunReplaceAll (Editor editor)
    {
        if (!TryBuildStrategy (editor, _replaceFindTextField.Text))
        {
            return;
        }

        var count = editor.ReplaceAll (_replaceWithTextField.Text);
        _statusLabel.Text = count == 0
            ? $"No match for \"{_replaceFindTextField.Text}\"."
            : $"Replaced {count} occurrence{(count == 1 ? "" : "s")}.";
    }

    /// <summary>
    ///     Builds an <see cref="ISearchStrategy" /> from the dialog's checkbox state and assigns it to
    ///     <see cref="Editor.SearchStrategy" />. Returns <see langword="false" /> (and surfaces the error on the
    ///     status label) when the search text is empty or -- in regex mode -- fails to compile.
    /// </summary>
    private bool TryBuildStrategy (Editor editor, string searchText)
    {
        if (string.IsNullOrEmpty (searchText))
        {
            _statusLabel.Text = string.Empty;

            return false;
        }

        SearchMode mode = _regexCheckBox.Value == CheckState.Checked ? SearchMode.RegEx : SearchMode.Normal;
        var ignoreCase = _matchCaseCheckBox.Value != CheckState.Checked;
        var wholeWord = _wholeWordCheckBox.Value == CheckState.Checked;

        try
        {
            editor.SearchStrategy = SearchStrategyFactory.Create (searchText, ignoreCase, wholeWord, mode);
            _statusLabel.Text = string.Empty;

            return true;
        }
        catch (SearchPatternException ex)
        {
            _statusLabel.Text = $"Regex error: {ex.Message}";

            return false;
        }
    }
}
