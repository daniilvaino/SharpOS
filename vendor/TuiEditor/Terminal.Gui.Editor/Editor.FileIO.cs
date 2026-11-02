using System.Text;
using Terminal.Gui.Editor.Document;

namespace Terminal.Gui.Editor;

public partial class Editor
{
    // Chars buffered before the very first paint. Small so the first screenful appears almost immediately —
    // the whole point of progressive load.
    private const int FirstFlushChars = 16 * 1024;

    // Chars buffered between subsequent flushes. AppendOnUiThread pins the caret to offset 0, so the
    // viewport stays on the first screenful and every subsequent chunk streams in *below the fold,
    // off-screen* — it is never visually "chunky". The only per-flush cost is one marshalled round-trip
    // (the consumer's marshal hops to its UI thread) plus the full render frame that SetNeedsDraw
    // triggers there — a frame that repaints the unchanged top screen, i.e. pure waste. So subsequent
    // flushes want to be as large as practical: at 64 KiB a 10 MiB file cost ~160 of those wasted
    // round-trips + frames (~8 s wall in ted vs ~0.75 s of actual document work); 1 MiB cuts that to
    // ~10. Larger still helps less and inflates the transient per-flush allocation (string + snapshot
    // + undo entry), so 1 MiB is the balance. The tiny FirstFlushChars keeps first paint instant.
    private const int SubsequentFlushChars = 1024 * 1024;

    /// <summary>
    ///     Streams text from <paramref name="stream" /> into <see cref="Document" /> without ever materializing the
    ///     whole file as a single string (resolves OPEN-003 / DEC-009).
    ///     <para>
    ///         When <paramref name="marshal" /> is supplied (UI consumers such as ted), an empty document is
    ///         installed and painted <b>immediately</b> and decoded chunks are appended on the UI thread as they
    ///         arrive, so the editor fills in top-down while staying responsive. When <paramref name="marshal" /> is
    ///         <see langword="null" /> (synchronous callers / tests) the file is read in chunks and the document is
    ///         installed once at the end.
    ///     </para>
    /// </summary>
    /// <param name="stream">The source stream.</param>
    /// <param name="encoding">Fallback encoding when no BOM is detected. Defaults to UTF-8 (no BOM).</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="cancellationToken">Cancels the load between chunks.</param>
    /// <param name="marshal">
    ///     Marshals an action onto the UI thread and completes when it has run. UI consumers pass their
    ///     application-invoke helper so the read happens on a background thread while every document mutation
    ///     happens on the UI thread.
    /// </param>
    public async Task LoadAsync (
        Stream stream,
        Encoding? encoding = null,
        IProgress<TextDocumentProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<Action, Task>? marshal = null)
    {
        ArgumentNullException.ThrowIfNull (stream);

        if (marshal is null)
        {
            await LoadNonProgressiveAsync (stream, encoding, progress, cancellationToken).ConfigureAwait (false);

            return;
        }

        await LoadProgressiveAsync (stream, encoding, progress, marshal, cancellationToken).ConfigureAwait (false);
    }

    // Synchronous / non-UI path: build the whole document off-thread in chunks (no giant string), then install
    // it once. The document's owner thread is released at the end so the next single consumer (the caller, a
    // test, the UI) claims it on first access — matches the pre-progressive idiom and TextDocument's affinity.
    private async Task LoadNonProgressiveAsync (
        Stream stream,
        Encoding? encoding,
        IProgress<TextDocumentProgress>? progress,
        CancellationToken cancellationToken)
    {
        Document?.SetOwnerThread (null);

        TextDocument document =
            await TextDocument.LoadAsync (stream, encoding, progress, cancellationToken).ConfigureAwait (false);

        document.SetOwnerThread (Thread.CurrentThread);
        Document = document;
        CaretOffset = 0;
        document.SetOwnerThread (null);
    }

    // UI path: install an empty document immediately so the editor paints, then append decoded chunks on the UI
    // thread as they stream in. The read/decode runs on a background thread; the document is only ever touched
    // on the UI thread (assign here, every append + to finalize marshalled).
    private async Task LoadProgressiveAsync (
        Stream stream,
        Encoding? encoding,
        IProgress<TextDocumentProgress>? progress,
        Func<Action, Task> marshal,
        CancellationToken cancellationToken)
    {
        Document?.SetOwnerThread (null);
        TextDocument document = new ();
        document.SetOwnerThread (Thread.CurrentThread);

        var priorReadOnly = ReadOnly;
        Document = document;
        CaretOffset = 0;

        // Read-only while streaming: the background reader appends at the end; user edits at arbitrary offsets
        // would race the appends. The buffer becomes editable the moment the load completes.
        ReadOnly = true;

        Encoding detected = encoding ?? new UTF8Encoding (false);
        var firstFlushDone = false;
        StringBuilder pending = new (SubsequentFlushChars + FirstFlushChars);

        void AppendOnUiThread (string text)
        {
            // The user opened a different file (or LoadAsync was called again) while this load was in flight —
            // stop feeding a document the editor no longer shows.
            if (!ReferenceEquals (_document, document))
            {
                throw new OperationCanceledException ();
            }

            document.Insert (document.TextLength, text);

            // Keep the caret pinned to the top so the viewport stays put on the first screenful while the rest
            // streams in below, off-screen. Without this the AfterInsertion caret anchor rides every tail append
            // and EnsureCaretVisible follows it, scrolling the view for the whole load. Tail-follow is a host
            // policy (a host can scroll / set CaretOffset on progress); the editor's default is a stable viewport.
            CaretOffset = 0;
            SetNeedsDraw ();
        }

        async ValueTask OnChunk (ReadOnlyMemory<char> chunk, CancellationToken token)
        {
            pending.Append (chunk.Span);

            var threshold = firstFlushDone ? SubsequentFlushChars : FirstFlushChars;

            if (pending.Length < threshold)
            {
                return;
            }

            var text = pending.ToString ();
            pending.Clear ();
            firstFlushDone = true;
            await marshal (() => AppendOnUiThread (text)).ConfigureAwait (false);
        }

        try
        {
            // Read + decode off the UI thread; OnChunk marshals each flush back onto it.
            await Task.Run (
                () => TextDocument.StreamAsync (
                    stream,
                    encoding,
                    OnChunk,
                    enc => detected = enc,
                    progress,
                    cancellationToken),
                cancellationToken).ConfigureAwait (false);

            await marshal (() =>
            {
                if (!ReferenceEquals (_document, document))
                {
                    return;
                }

                if (pending.Length > 0)
                {
                    document.Insert (document.TextLength, pending.ToString ());
                    pending.Clear ();
                }

                document.Encoding = detected;

                // Loading is not an undoable edit (matches every editor); discard the transient append
                // history and mark the freshly loaded content as the pristine on-disk state.
                document.UndoStack.ClearAll ();
                document.UndoStack.MarkAsOriginalFile ();

                ReadOnly = priorReadOnly;
                CaretOffset = 0;
                SetNeedsDraw ();
            }).ConfigureAwait (false);
        }
        catch
        {
            // Restore editability even on cancel/failure; keep whatever streamed in so far.
            await marshal (() =>
            {
                if (ReferenceEquals (_document, document))
                {
                    ReadOnly = priorReadOnly;
                }
            }).ConfigureAwait (false);

            throw;
        }
    }

    /// <summary>
    ///     Streams <see cref="Document" /> to <paramref name="stream" /> by delegating to
    ///     <see cref="TextDocument.SaveAsync" />.
    /// </summary>
    public Task SaveAsync (
        Stream stream,
        IProgress<TextDocumentProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        TextDocument document = Document
                                ?? throw new InvalidOperationException (
                                    "Cannot save because the editor has no document.");

        return document.SaveAsync (stream, progress, cancellationToken);
    }
}
