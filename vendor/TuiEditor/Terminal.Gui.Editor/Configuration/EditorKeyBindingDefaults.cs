using Terminal.Gui.Input;

namespace Terminal.Gui.Editor.Configuration;

internal static class EditorKeyBindingDefaults
{
    internal static Dictionary<Command, PlatformKeyBinding> Create ()
    {
        return new Dictionary<Command, PlatformKeyBinding>
        {
            [Command.Start] = Bind.All (Key.Home.WithCtrl),
            [Command.End] = Bind.All (Key.End.WithCtrl),
            [Command.NewLine] = Bind.All (Key.Enter),
            [Command.DeleteCharLeft] = Bind.All (Key.Backspace),
            [Command.DeleteCharRight] = Bind.All (Key.Delete),
            [Command.Undo] = Bind.All (Key.Z.WithCtrl),
            [Command.Redo] = Bind.All (Key.Y.WithCtrl, Key.Z.WithCtrl.WithShift),
            [Command.Cut] = Bind.All (Key.X.WithCtrl),
            [Command.Copy] = Bind.All (Key.C.WithCtrl),
            [Command.Paste] = Bind.All (Key.V.WithCtrl),
            [Command.Collapse] = Bind.All (Key.M.WithCtrl),
            [Command.InsertTab] = Bind.All (Key.Tab),
            [Command.Unindent] = Bind.All (Key.Tab.WithShift),
            [Command.FindNext] = Bind.All (Key.F3),
            [Command.FindPrevious] = Bind.All (Key.F3.WithShift),
            [Command.Find] = Bind.All (Key.F.WithCtrl),
            [Command.Replace] = Bind.All (Key.H.WithCtrl),

            // Vertical multi-caret — VS Code parity (Ctrl+Alt+Up/Down). A PlatformKeyBinding, so a
            // user whose terminal/WM grabs the chord overrides it via View.ViewKeyBindings config;
            // no editor-specific fallback chord. macOS uses the same chord pending real-terminal
            // validation (specs/decisions.md DEC-006).
            [Command.InsertCaretAbove] = Bind.All (Key.CursorUp.WithCtrl.WithAlt),
            [Command.InsertCaretBelow] = Bind.All (Key.CursorDown.WithCtrl.WithAlt),
            [Command.WordLeft] = Bind.All (Key.CursorLeft.WithCtrl),
            [Command.WordRight] = Bind.All (Key.CursorRight.WithCtrl),
            [Command.WordLeftExtend] = Bind.All (Key.CursorLeft.WithCtrl.WithShift),
            [Command.WordRightExtend] = Bind.All (Key.CursorRight.WithCtrl.WithShift),
            [Command.KillWordLeft] = Bind.All (Key.Backspace.WithCtrl),
            [Command.KillWordRight] = Bind.All (Key.Delete.WithCtrl),
            [Command.ToggleOverwrite] = Bind.All (Key.InsertChar)
        };
    }
}
