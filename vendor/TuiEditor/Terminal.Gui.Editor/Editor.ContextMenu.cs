using System.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Terminal.Gui.Editor;

public partial class Editor
{
    /// <summary>
    ///     Gets or sets the built-in context menu shown on right-click or <see cref="Command.Context" />.
    ///     Defaults to a standard editing menu (Undo, Redo, Cut, Copy, Paste, Select All) whose items
    ///     are state-aware — mutating items are disabled when <see cref="ReadOnly" /> is <see langword="true" />,
    ///     Copy / Cut are disabled when there is no selection, and Undo / Redo reflect the undo stack.
    ///     Set to <see langword="null" /> to suppress the context menu entirely; set to a custom
    ///     <see cref="PopoverMenu" /> to replace it.
    /// </summary>
    public PopoverMenu? ContextMenu
    {
        get;
        set
        {
            field = value;

            field?.Target = new WeakReference<View> (this);
        }
    }

    /// <summary>Creates the default editing context menu items using declarative command binding.</summary>
    /// <remarks>
    ///     Each <see cref="MenuItem" /> is constructed with <c>this</c> as the target view and a
    ///     <see cref="Command" />. The framework resolves title, help text, and key from
    ///     <c>GlobalResources</c> and routes the command to this <see cref="Editor" /> via command
    ///     bubbling — no explicit <see cref="MenuItem.Action" /> delegates are needed.
    /// </remarks>
    private View[] CreateDefaultContextMenuItems ()
    {
        return
        [
            new MenuItem (this, Command.Undo),
            new MenuItem (this, Command.Redo),
            new Line (),
            new MenuItem (this, Command.Cut),
            new MenuItem (this, Command.Copy),
            new MenuItem (this, Command.Paste),
            new Line (),
            new MenuItem (this, Command.SelectAll)
        ];
    }

    /// <summary>
    ///     Updates the <see cref="View.Enabled" /> state of the context menu items to reflect the current
    ///     editor state (ReadOnly, selection, clipboard, undo/redo).
    /// </summary>
    private void UpdateContextMenuState ()
    {
        if (ContextMenu?.Root is null)
        {
            return;
        }

        var hasSelection = HasSelection;
        var canUndo = !ReadOnly && _document is { UndoStack.CanUndo: true };
        var canRedo = !ReadOnly && _document is { UndoStack.CanRedo: true };
        var canPaste = !ReadOnly;
        var canCut = !ReadOnly && hasSelection;

        foreach (View child in ContextMenu.Root.SubViews)
        {
            if (child is not MenuItem menuItem)
            {
                continue;
            }

            menuItem.Enabled = menuItem.Command switch
            {
                Command.Undo => canUndo,
                Command.Redo => canRedo,
                Command.Cut => canCut,
                Command.Copy => hasSelection,
                Command.Paste => canPaste,
                _ => menuItem.Enabled
            };
        }
    }

    /// <summary>
    ///     Shows the context menu at the given screen position, after updating item state.
    /// </summary>
    private void ShowContextMenu (Point? screenPosition = null)
    {
        if (ContextMenu is null)
        {
            return;
        }

        UpdateContextMenuState ();
        ContextMenu.MakeVisible (screenPosition);
    }

    /// <summary>Builds and assigns the default context menu.</summary>
    private void InitializeDefaultContextMenu ()
    {
        ContextMenu = new PopoverMenu (CreateDefaultContextMenuItems ());
    }
}
