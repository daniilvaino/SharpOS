// ZipFile's directory walk — in place of ZipFile.Create.Windows.cs, which
// builds a FileSystemEnumerable (System.IO.Enumeration is not here; the Tar
// port walks the same way). FAT has no links, so every directory is a real
// one and every other entry a file.

using System.Collections.Generic;

namespace System.IO.Compression
{
    public static partial class ZipFile
    {
        private static IEnumerable<(string, CreateEntryType)> CreateEnumerableForCreate(string directoryFullPath)
        {
            foreach (string fullPath in Directory.GetFileSystemEntries(directoryFullPath))
            {
                if (Directory.Exists(fullPath))
                {
                    yield return (fullPath, CreateEntryType.Directory);
                    foreach ((string, CreateEntryType) inner in CreateEnumerableForCreate(fullPath))
                        yield return inner;
                }
                else
                {
                    yield return (fullPath, CreateEntryType.File);
                }
            }
        }
    }
}
