namespace SharpOS.AppSdk
{
    // What the app can learn about the screen it draws on.
    //
    // The size comes from the framebuffer and the font, both of which live on
    // the kernel side, so an app cannot work it out for itself — and guessing
    // 80x25 would be wrong on every machine we actually run on.
    internal static unsafe class AppConsole
    {
        /// <summary>
        /// The console size in cells. False when the kernel publishes no size,
        /// which means there is no terminal front-end to draw on — a caller
        /// must treat that as "cannot draw", not as a small screen.
        /// </summary>
        public static bool TryGetSize(out int columns, out int rows)
        {
            columns = 0;
            rows = 0;

            var services = AppRuntime.Services;
            if (services == null || services->ConsoleSizeAddress == 0) return false;

            var query = (delegate* unmanaged<uint>)services->ConsoleSizeAddress;
            uint packed = query();
            if (packed == 0) return false;

            columns = (int)(packed & 0xFFFFu);
            rows = (int)((packed >> 16) & 0xFFFFu);
            return columns > 0 && rows > 0;
        }
    }
}
