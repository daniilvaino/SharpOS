using System.Reflection;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;

namespace Terminal.Gui.Editor;

internal static class AnsiInputProcessorState
{
    private const string PendingPrintableSuppressionFieldName = "_pendingPrintableSuppression";

    public static void ClearPendingPrintableSuppression (IApplication? app)
    {
        if (app?.Driver?.GetInputProcessor () is not AnsiInputProcessor processor)
        {
            return;
        }

        // Terminal.Gui suppresses the next printable fallback key after parsing
        // ANSI Shift+Tab (ESC [ Z) because Shift+Tab reports Tab as printable text. Until TG exposes
        // public input-processor state for this, clear that one-shot suppression after the editor
        // handles Unindent so the user's next Tab reaches us. If TG renames this private field, the
        // type checks below intentionally no-op; the only consequence is the original Tab suppression.
        FieldInfo? field = typeof (AnsiInputProcessor).GetField (
            PendingPrintableSuppressionFieldName,
            BindingFlags.Instance | BindingFlags.NonPublic);

        if (field?.FieldType != typeof (string))
        {
            return;
        }

        field.SetValue (processor, string.Empty);
    }
}
