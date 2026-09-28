// SPDX-License-Identifier: MIT
//
// SharpOS addition, not upstream: the colours every full-screen program here
// uses. One place, because two programs with two copies of a palette drift, and
// the second one to be looked at is the one that looks wrong.

namespace Terminal.Gui
{
    /// <summary>
    /// The look of a SharpOS text interface: dark, with one warm accent.
    /// </summary>
    /// <remarks>
    /// Written after the library's defaults were used as-is for a while and
    /// turned out to be unreadable on a real panel. Two faults, both worth
    /// naming so they are not reintroduced:
    ///
    /// White on blue is the default for <c>Base</c>, and <c>TopLevel.Focus</c>
    /// is white on cyan. Neither was ever chosen - a program that overrode Base,
    /// Menu and Dialog, as the launcher did, still inherited TopLevel wherever a
    /// view had no scheme of its own, so half the screen came out in saturated
    /// blue while the other half was the intended grey. All five schemes are set
    /// here; leaving one out is how that happened.
    ///
    /// Grey body text on a coloured background is the other. Grey is the
    /// library's default foreground and reads as switched-off next to anything
    /// saturated. Body text is white.
    ///
    /// The accent is amber rather than blue or cyan: it is the one hue that
    /// stays legible against black at this font size without competing with the
    /// text, and a selection bar in it needs black text, which is unmistakable.
    /// </remarks>
    internal static class SharpOSTheme
    {
        public static void Apply(SharpOSDriver driver)
        {
            // Everything a Toplevel hands down. Identical to Base on purpose:
            // which of the two a given view resolves to depends on where it sits
            // in the tree, and that is not a thing anyone should have to reason
            // about while reading a layout.
            Set(driver, Colors.TopLevel, Color.White, Color.Black);
            Set(driver, Colors.Base, Color.White, Color.Black);

            // A band, so the menu and a dialog read as sitting above the page
            // rather than being part of it.
            Set(driver, Colors.Menu, Color.White, Color.DarkGray);
            Set(driver, Colors.Dialog, Color.White, Color.DarkGray);

            // Errors keep red, which is the one place a saturated background
            // earns its keep.
            Colors.Error.Normal = driver.MakeColor(Color.White, Color.Red);
            Colors.Error.Focus = driver.MakeColor(Color.Black, Color.BrightRed);
            Colors.Error.HotNormal = driver.MakeColor(Color.BrightYellow, Color.Red);
            Colors.Error.HotFocus = driver.MakeColor(Color.Black, Color.BrightRed);
            Colors.Error.Disabled = driver.MakeColor(Color.Gray, Color.Red);
        }

        // One scheme, given its background: text on it, the amber selection, the
        // hotkey, and what disabled looks like.
        private static void Set(SharpOSDriver driver, ColorScheme scheme,
                                Color text, Color background)
        {
            scheme.Normal = driver.MakeColor(text, background);
            scheme.Focus = driver.MakeColor(Color.Black, Color.Brown);
            scheme.HotNormal = driver.MakeColor(Color.BrightYellow, background);
            scheme.HotFocus = driver.MakeColor(Color.Black, Color.BrightYellow);

            // Dark grey on the page's own background, so something unavailable
            // recedes instead of reading as text in a different colour.
            scheme.Disabled = driver.MakeColor(Color.DarkGray, background);
        }
    }
}
