// System.IO.Path, the app tier's part (release 0.1): what asks the file
// system. The rest is std's (std-no-runtime/Bcl/Path.cs).

namespace System.IO
{
    public static partial class Path
    {
        /// <summary>Whether a file or a directory is at <paramref name="path"/>; false for null or empty, as in BCL.</summary>
        public static bool Exists(string? path)
            => !string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path));
    }
}
