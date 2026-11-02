using Terminal.Gui.Input;

namespace Terminal.Gui.Editor.Configuration;

/// <summary>
///     MEC-friendly settings POCO for <see cref="Editor" /> defaults.
/// </summary>
public class EditorSettings
{
    /// <summary>
    ///     Gets or sets editor-specific default key bindings layered on top of
    ///     <see cref="Terminal.Gui.ViewBase.View.DefaultKeyBindings" />.
    /// </summary>
    public Dictionary<Command, PlatformKeyBinding>? DefaultKeyBindings { get; set; } =
        EditorKeyBindingDefaults.Create ();

    /// <summary>
    ///     The static facade instance. Applications update this from Microsoft.Extensions.Configuration
    ///     before constructing <see cref="Editor" /> instances.
    /// </summary>
    public static EditorSettings Defaults { get; set; } = new ();
}
