using Terminal.Gui.Editor.Document;
using Terminal.Gui.Editor.Document.Folding;

namespace Terminal.Gui.Editor;

/// <summary>
///     Automatic folding orchestration. When <see cref="FoldingStrategy" /> is assigned and
///     <see cref="AutomaticFolding" /> is <see langword="true" />, the editor creates a
///     <see cref="Document.Folding.FoldingManager" />, subscribes to document changes, and
///     re-scans foldings only when the strategy reports a structural change.
/// </summary>
public partial class Editor
{
    private IFoldingStrategy? _foldingStrategy;
    private TextDocument? _foldingDocument;
    private bool _foldingUpdateNeeded;
    private bool _automaticFolding;
    private int _maximumAutomaticFoldingDocumentLength = 1_000_000;

    // Tracks whether the current FoldingManager was created by automatic folding.
    // When true, InstallAutomaticFolding is allowed to clear it; when false, a consumer
    // set FoldingManager directly and automatic folding must not wipe it.
    private bool _automaticFoldingOwnsFoldingManager;

    /// <summary>
    ///     Gets or sets the folding strategy. When non-null and <see cref="AutomaticFolding" />
    ///     is <see langword="true" />, the editor automatically creates a <see cref="Document.Folding.FoldingManager" />,
    ///     subscribes to document changes, and refreshes foldings when structural characters change.
    /// </summary>
    public IFoldingStrategy? FoldingStrategy
    {
        get => _foldingStrategy;
        set
        {
            if (ReferenceEquals (_foldingStrategy, value))
            {
                return;
            }

            var wasNull = _foldingStrategy is null;
            _foldingStrategy = value;

            // Auto-enable automatic folding when a strategy is first assigned.
            if (wasNull && _foldingStrategy is not null && !_automaticFolding)
            {
                _automaticFolding = true;
            }

            InstallAutomaticFolding ();
        }
    }

    /// <summary>
    ///     Gets or sets whether the editor automatically runs the <see cref="FoldingStrategy" />
    ///     in response to document changes. Defaults to <see langword="true" /> when
    ///     <see cref="FoldingStrategy" /> is assigned.
    /// </summary>
    public bool AutomaticFolding
    {
        get => _automaticFolding;
        set
        {
            if (_automaticFolding == value)
            {
                return;
            }

            _automaticFolding = value;
            InstallAutomaticFolding ();
        }
    }

    /// <summary>
    ///     Gets or sets the maximum document length (in characters) for which automatic
    ///     folding is active. Documents exceeding this threshold skip fold re-scanning
    ///     to avoid UI-thread hangs. Defaults to 1,000,000.
    /// </summary>
    public int MaximumAutomaticFoldingDocumentLength
    {
        get => _maximumAutomaticFoldingDocumentLength;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan (value, 0);

            if (_maximumAutomaticFoldingDocumentLength == value)
            {
                return;
            }

            _maximumAutomaticFoldingDocumentLength = value;
            InstallAutomaticFolding ();
        }
    }

    /// <summary>
    ///     Installs or tears down automatic folding based on the current state of
    ///     <see cref="FoldingStrategy" />, <see cref="AutomaticFolding" />, and the document.
    /// </summary>
    private void InstallAutomaticFolding ()
    {
        if (_foldingStrategy is null || !_automaticFolding || Document is null)
        {
            SetFoldingDocument (null);

            if (!_automaticFoldingOwnsFoldingManager)
            {
                return;
            }

            FoldingManager = null;
            _automaticFoldingOwnsFoldingManager = false;

            return;
        }

        if (Document.TextLength > _maximumAutomaticFoldingDocumentLength)
        {
            SetFoldingDocument (null);

            if (!_automaticFoldingOwnsFoldingManager)
            {
                return;
            }

            FoldingManager = null;
            _automaticFoldingOwnsFoldingManager = false;

            return;
        }

        FoldingManager fm = new (Document);
        FoldingManager = fm;
        _automaticFoldingOwnsFoldingManager = true;
        _foldingStrategy.UpdateFoldings (fm, Document);
        SetFoldingDocument (Document);
    }

    private void SetFoldingDocument (TextDocument? document)
    {
        if (ReferenceEquals (_foldingDocument, document))
        {
            return;
        }

        if (_foldingDocument is not null)
        {
            _foldingDocument.Changed -= OnFoldingDocumentChanged;
            _foldingDocument.UpdateFinished -= OnFoldingDocumentUpdateFinished;
        }

        _foldingDocument = document;
        _foldingUpdateNeeded = false;

        if (_foldingDocument is null)
        {
            return;
        }

        _foldingDocument.Changed += OnFoldingDocumentChanged;
        _foldingDocument.UpdateFinished += OnFoldingDocumentUpdateFinished;
    }

    private void OnFoldingDocumentChanged (object? sender, DocumentChangeEventArgs e)
    {
        if (_foldingStrategy is not null)
        {
            _foldingUpdateNeeded |= _foldingStrategy.ChangeMayAffectFoldings (e);
        }
    }

    private void OnFoldingDocumentUpdateFinished (object? sender, EventArgs e)
    {
        if (!_foldingUpdateNeeded)
        {
            return;
        }

        _foldingUpdateNeeded = false;
        UpdateAutomaticFoldings ();
    }

    private void UpdateAutomaticFoldings ()
    {
        if (FoldingManager is null || Document is null || _foldingStrategy is null)
        {
            return;
        }

        if (Document.TextLength > _maximumAutomaticFoldingDocumentLength)
        {
            SetFoldingDocument (null);

            if (!_automaticFoldingOwnsFoldingManager)
            {
                return;
            }

            FoldingManager = null;
            _automaticFoldingOwnsFoldingManager = false;

            return;
        }

        _foldingStrategy.UpdateFoldings (FoldingManager, Document);
    }
}
