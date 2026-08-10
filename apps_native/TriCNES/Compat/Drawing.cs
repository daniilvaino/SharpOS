// App-local stand-ins so the upstream TriCNES sources compile UNCHANGED.
//
// These live in the app, NOT in std: they are not BCL-compatible and have no
// business anywhere the rest of the system can reach them. The naming
// discipline (README / CLAUDE.md invariant 2) reserves canonical namespaces
// for full implementations; this is a deliberate, contained exception whose
// whole purpose is to avoid editing a third-party tree we want to keep pulling
// from upstream.
//
// Why faking these is safe here: the emulator's pixel buffer is a plain
// `Int32[]`, and `Bitmap` is only the WinForms *view* onto it. Nothing in the
// emulation path reads back through Bitmap — it writes ints and we blit them
// ourselves (see GopVideo). So the type needs to exist, not to work.

namespace System.Drawing
{
    // ARGB packed in an int, exactly as Color.ToArgb() defines it.
    public readonly struct Color
    {
        private readonly int _argb;

        private Color(int argb) { _argb = argb; }

        public static Color FromArgb(int argb) => new Color(argb);

        public static Color FromArgb(int red, int green, int blue)
            => new Color(unchecked((int)0xFF000000) | (red << 16) | (green << 8) | blue);

        public static Color FromArgb(int alpha, int red, int green, int blue)
            => new Color((alpha << 24) | (red << 16) | (green << 8) | blue);

        public int ToArgb() => _argb;

        public int R => (_argb >> 16) & 0xFF;
        public int G => (_argb >> 8) & 0xFF;
        public int B => _argb & 0xFF;
        public int A => (_argb >> 24) & 0xFF;
    }

    // Holds geometry and nothing else. The upstream DirectBitmap constructs one
    // over its pinned int[] and disposes it; both are no-ops for us.
    public sealed class Bitmap : IDisposable
    {
        public int Width { get; }
        public int Height { get; }

        public Bitmap(int width, int height, int stride,
                      Imaging.PixelFormat format, IntPtr scan0)
        {
            Width = width;
            Height = height;
            _ = stride; _ = format; _ = scan0;
        }

        public void Dispose() { }
    }
}

namespace System.Drawing.Imaging
{
    public enum PixelFormat
    {
        Format32bppPArgb = 925707,
    }
}
