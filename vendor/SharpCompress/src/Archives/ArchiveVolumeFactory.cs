using System;
using System.IO;
#if SHARPOS_REGEX
using System.Text.RegularExpressions;
#endif
using SharpCompress.Common;

namespace SharpCompress.Archives;

internal abstract class ArchiveVolumeFactory
{
    internal static FileInfo? GetFilePart(int index, FileInfo part1) //base the name on the first part
    {
        FileInfo? item = null;
        // SharpOS cut: volume names by Regex, until std has Regex (release.md, section 2); without it the next volume is not found
#if SHARPOS_REGEX

        //split 001, 002 ...
        var m = Regex.Match(part1.Name, @"^(.*\.)([0-9]+)$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            item = new FileInfo(
                Path.Combine(
                    part1.DirectoryName!,
                    String.Concat(
                        m.Groups[1].Value,
                        (index + 1)
                            .ToString(Constants.DefaultCultureInfo)
                            .PadLeft(m.Groups[2].Value.Length, '0')
                    )
                )
            );
        }

#endif
        return item is null ? null : ResolveFilePart(item);
    }

    internal static FileInfo? ResolveFilePart(FileInfo item)
    {
        if (item.Exists)
        {
            return item;
        }
        if (!item.Directory!.Exists)
        {
            return null;
        }
        FileInfo? match = null;
        foreach (var file in item.Directory.EnumerateFiles())
        {
            if (!string.Equals(file.Name, item.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (match is not null)
            {
                throw new InvalidFormatException($"Ambiguous archive volume name: {item.Name}");
            }
            match = file;
        }
        return match;
    }
}
