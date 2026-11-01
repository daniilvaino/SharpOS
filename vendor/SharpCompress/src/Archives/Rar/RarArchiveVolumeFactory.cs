using System;
using System.IO;
#if SHARPOS_REGEX
using System.Text.RegularExpressions;
#endif
using SharpCompress.Common;

namespace SharpCompress.Archives.Rar;

internal static class RarArchiveVolumeFactory
{
    internal static FileInfo? GetFilePart(int index, FileInfo part1) //base the name on the first part
    {
        FileInfo? item = null;
        // SharpOS cut: volume names by Regex, until std has Regex (release.md, section 2); without it the next volume is not found
#if SHARPOS_REGEX

        //new style rar - ..part1 | /part01 | part001 ....
        var m = Regex.Match(part1.Name, @"^(.*\.part)([0-9]+)(\.rar)$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            item = new FileInfo(
                Path.Combine(
                    part1.DirectoryName!,
                    String.Concat(
                        m.Groups[1].Value,
                        (index + 1)
                            .ToString(Constants.DefaultCultureInfo)
                            .PadLeft(m.Groups[2].Value.Length, '0'),
                        m.Groups[3].Value
                    )
                )
            );
        }
        else
        {
            //old style - ...rar, .r00, .r01 ...
            m = Regex.Match(part1.Name, @"^(.*\.)([r-z{])(ar|[0-9]+)$", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                item = new FileInfo(
                    Path.Combine(
                        part1.DirectoryName!,
                        String.Concat(
                            m.Groups[1].Value,
                            index == 0
                                ? m.Groups[2].Value + m.Groups[3].Value
                                : (char)(m.Groups[2].Value[0] + ((index - 1) / 100))
                                    + (index - 1)
                                        .ToString(
                                            "D4",
                                            global::SharpCompress
                                                .Common
                                                .Constants
                                                .DefaultCultureInfo
                                        )
                                        .Substring(2)
                        )
                    )
                );
            }
            else //split .001, .002 ....
            {
                return ArchiveVolumeFactory.GetFilePart(index, part1);
            }
        }

#endif
        return item is null ? null : ArchiveVolumeFactory.ResolveFilePart(item);
    }
}
