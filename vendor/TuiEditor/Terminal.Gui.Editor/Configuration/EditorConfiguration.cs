using Microsoft.Extensions.Configuration;
using Terminal.Gui.Input;

namespace Terminal.Gui.Editor.Configuration;

/// <summary>
///     Applies Microsoft.Extensions.Configuration values to <see cref="EditorSettings" />.
/// </summary>
public static class EditorConfiguration
{
    private const string LegacyDefaultKeyBindingsSectionName = "Terminal.Gui.Editor.Editor.DefaultKeyBindings";

    /// <summary>The default configuration section for <see cref="Editor" /> settings.</summary>
    public const string SectionName = "Editor";

    /// <summary>
    ///     Applies editor settings from <paramref name="configuration" /> to
    ///     <see cref="EditorSettings.Defaults" />.
    /// </summary>
    /// <param name="configuration">The configuration root to read.</param>
    /// <param name="sectionName">The section containing editor settings.</param>
    public static void Apply (IConfiguration configuration, string sectionName = SectionName)
    {
        ArgumentNullException.ThrowIfNull (configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace (sectionName);

        EditorSettings settings = new ();
        ApplyDefaultKeyBindings (configuration.GetSection (LegacyDefaultKeyBindingsSectionName), settings);
        ApplyDefaultKeyBindings (
            configuration.GetSection (sectionName).GetSection (nameof (EditorSettings.DefaultKeyBindings)), settings);
        EditorSettings.Defaults = settings;
    }

    private static void ApplyDefaultKeyBindings (IConfigurationSection section, EditorSettings settings)
    {
        if (!section.Exists ())
        {
            return;
        }

        settings.DefaultKeyBindings ??= [];

        foreach (IConfigurationSection commandSection in section.GetChildren ())
        {
            if (!Enum.TryParse (commandSection.Key, true, out Command command))
            {
                throw new FormatException ($"Unknown editor command in configuration: '{commandSection.Key}'.");
            }

            settings.DefaultKeyBindings[command] = ReadPlatformKeyBinding (commandSection);
        }
    }

    private static PlatformKeyBinding ReadPlatformKeyBinding (IConfigurationSection section)
    {
        return new PlatformKeyBinding
        {
            All = ReadKeys (section.GetSection (nameof (PlatformKeyBinding.All))),
            Windows = ReadKeys (section.GetSection (nameof (PlatformKeyBinding.Windows))),
            Linux = ReadKeys (section.GetSection (nameof (PlatformKeyBinding.Linux))),
            Macos = ReadKeys (section.GetSection (nameof (PlatformKeyBinding.Macos)))
        };
    }

    private static Key[]? ReadKeys (IConfigurationSection section)
    {
        if (!section.Exists ())
        {
            return null;
        }

        List<Key> keys = [];

        foreach (IConfigurationSection keySection in section.GetChildren ())
        {
            var keyText = keySection.Value;

            if (string.IsNullOrWhiteSpace (keyText))
            {
                throw new FormatException ($"Empty key in configuration section '{section.Path}'.");
            }

            if (!Key.TryParse (keyText, out Key key))
            {
                throw new FormatException ($"Invalid key '{keyText}' in configuration section '{section.Path}'.");
            }

            keys.Add (key);
        }

        return keys.Count == 0 ? null : keys.ToArray ();
    }
}
