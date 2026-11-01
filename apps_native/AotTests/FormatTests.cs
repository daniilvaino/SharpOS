using System;
using System.IO;
using System.IO.Hashing;
using SharpOS.AppSdk;

namespace AotTests
{
    // The standard formats std carries (release 0.1): hashes first — the
    // check values each algorithm's specification gives, so the port is
    // compared with the algorithm, not with itself.
    internal static unsafe partial class AppEntry
    {
        // ILC's intrinsics (release 0.1): SSE2 is the baseline, so true in the
        // code itself; PCLMULQDQ is optimistic — read from g_cpuFeatures, which
        // AppRuntime fills with the kernel's flags.
        private static void CheckIntrinsics()
        {
            Check("intrinsics: Sse2.IsSupported (the baseline)", System.Runtime.Intrinsics.X86.Sse2.IsSupported);
            ulong flags = AppRuntime.Services == null ? 0 : AppRuntime.Services->IntrinsicFlags;
            bool pclmul = (flags & (ulong)SharpOS.Std.NoRuntime.HardwareIntrinsicFlags.Pclmulqdq) != 0;
            Console.Write("  intrinsic flags 0x" + flags.ToString("x") + "\n");
            Check("intrinsics: Pclmulqdq.IsSupported follows the kernel's flags (" + (pclmul ? "present" : "absent") + ")",
                  System.Runtime.Intrinsics.X86.Pclmulqdq.IsSupported == pclmul);
            if (System.Runtime.Intrinsics.X86.Pclmulqdq.IsSupported)
            {
                ulong* io = stackalloc ulong[2];
                io[0] = 3; io[1] = 0;
                var three = System.Runtime.Intrinsics.Vector128.Load(io);
                System.Runtime.Intrinsics.Vector128.Store(System.Runtime.Intrinsics.X86.Pclmulqdq.CarrylessMultiply(three, three, 0), io);
                Check("intrinsics: PCLMULQDQ 3 x 3 carry-less is 5", io[0] == 5ul && io[1] == 0ul);
            }
        }

        private static void CheckFormats()
        {
            CheckIntrinsics();
            CheckTar();
            byte[] check = new byte[] { (byte)'1', (byte)'2', (byte)'3', (byte)'4', (byte)'5', (byte)'6', (byte)'7', (byte)'8', (byte)'9' };

            // CRC-32 (IEEE 802.3) and CRC-64 (ECMA-182) of "123456789".
            Check("hashing: Crc32 of 123456789 is 0xCBF43926", Crc32.HashToUInt32(check) == 0xCBF43926u);
            Check("hashing: Crc64 of 123456789 is 0x6C40DF5F0B497347", Crc64.HashToUInt64(check) == 0x6C40DF5F0B497347ul);

            // xxHash of nothing, seed 0: the reference implementation's values.
            Check("hashing: XxHash32 of nothing is 0x02CC5D05", XxHash32.HashToUInt32(ReadOnlySpan<byte>.Empty) == 0x02CC5D05u);
            Check("hashing: XxHash64 of nothing is 0xEF46DB3751D8E999", XxHash64.HashToUInt64(ReadOnlySpan<byte>.Empty) == 0xEF46DB3751D8E999ul);
            Check("hashing: XxHash3 of nothing is 0x2D06800538D394C2", XxHash3.HashToUInt64(ReadOnlySpan<byte>.Empty) == 0x2D06800538D394C2ul);
            byte[] x128 = XxHash128.Hash(ReadOnlySpan<byte>.Empty);
            Check("hashing: XxHash128 of nothing is 99AA06D3014798D86001C324468D497F",
                  x128.Length == 16 && Hex(x128) == "99AA06D3014798D86001C324468D497F");

            // Appending in pieces, and from a stream, ends where one call does.
            var crc = new Crc32();
            crc.Append(new ReadOnlySpan<byte>(check, 0, 4));
            crc.Append(new ReadOnlySpan<byte>(check, 4, 5));
            Check("hashing: Crc32 appended in two pieces is the one-shot value", crc.GetCurrentHashAsUInt32() == 0xCBF43926u);

            var big = new byte[100_000];
            for (int i = 0; i < big.Length; i++) big[i] = (byte)(i * 31 + 7);
            var fromStream = new XxHash64();
            fromStream.Append(new MemoryStream(big));
            Check("hashing: XxHash64 over a stream is the one-shot value",
                  fromStream.GetCurrentHashAsUInt64() == XxHash64.HashToUInt64(big));
            var fromAsync = new Crc64();
            fromAsync.AppendAsync(new MemoryStream(big)).Wait();
            Check("hashing: Crc64 AppendAsync (Stream.CopyToAsync) is the one-shot value",
                  fromAsync.GetCurrentHashAsUInt64() == Crc64.HashToUInt64(big));
        }

        // System.Formats.Tar: archives GNU tar wrote on the build machine (its
        // two formats; a path past ustar's 100 bytes), one written and read
        // back here, and a directory through the file system and back.
        private static void CheckTar()
        {
            var when = new DateTimeOffset(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);
            const string longDir = "tartest/a-directory-with-a-rather-long-name-to-push-past-the-ustar-limit-of-one-hundred-bytes/";
            foreach (string format in new[] { "gnu", "pax" })
            {
                string path = "/tests/sample-" + format + ".tar";
                if (!System.IO.File.Exists(path)) { Check("tar: " + path + " is on the image", false); continue; }
                var names = new System.Collections.Generic.List<string>();
                string hello = null, deep = null;
                bool timesRight = true;
                using (var reader = new System.Formats.Tar.TarReader(System.IO.File.OpenRead(path)))
                {
                    System.Formats.Tar.TarEntry entry;
                    while ((entry = reader.GetNextEntry()) != null)
                    {
                        names.Add(entry.Name);
                        if (entry.ModificationTime != when) timesRight = false;
                        if (entry.DataStream == null) continue;
                        string text = new System.IO.StreamReader(entry.DataStream).ReadToEnd();
                        if (entry.Name == "tartest/hello.txt") hello = text;
                        if (entry.Name == longDir + "file-with-a-long-name-too.txt") deep = text;
                    }
                }
                Check("tar " + format + ": four entries, the long path whole (" + string.Join(", ", names) + ")",
                      names.Count == 4 && names.Contains(longDir) && names.Contains("tartest/hello.txt"));
                Check("tar " + format + ": the files' contents", hello == "Hello, tar!\n" && deep == "deep\n");
                Check("tar " + format + ": every entry's time is 2024-01-02 03:04:05 UTC", timesRight);
            }

            // Written here, read back.
            var archive = new System.IO.MemoryStream();
            using (var writer = new System.Formats.Tar.TarWriter(archive, System.Formats.Tar.TarEntryFormat.Pax, leaveOpen: true))
            {
                var dir = new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.Directory, "made/");
                writer.WriteEntry(dir);
                var file = new System.Formats.Tar.PaxTarEntry(System.Formats.Tar.TarEntryType.RegularFile, "made/data.bin");
                byte[] payload = new byte[3000];
                for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 7);
                file.DataStream = new System.IO.MemoryStream(payload);
                file.ModificationTime = when;
                writer.WriteEntry(file);
            }
            archive.Position = 0;
            int count = 0;
            bool same = false;
            using (var reader = new System.Formats.Tar.TarReader(archive))
            {
                System.Formats.Tar.TarEntry entry;
                while ((entry = reader.GetNextEntry()) != null)
                {
                    count++;
                    if (entry.Name != "made/data.bin") continue;
                    var copy = new System.IO.MemoryStream();
                    entry.DataStream.CopyTo(copy);
                    byte[] back = copy.ToArray();
                    same = back.Length == 3000 && entry.ModificationTime == when;
                    for (int i = 0; same && i < back.Length; i++) same = back[i] == (byte)(i * 7);
                }
            }
            Check("tar: written and read back — two entries, the 3000 bytes and the time", count == 2 && same);

            // A directory to an archive and out again, through the file system.
            // One per process: PROCTEST runs two AOTTESTS at once.
            string root = "/home/aottests-tar-" + Environment.ProcessId.ToString();
            if (System.IO.Directory.Exists(root)) System.IO.Directory.Delete(root, true);
            System.IO.Directory.CreateDirectory(root + "/in/sub");
            System.IO.File.WriteAllText(root + "/in/one.txt", "one");
            System.IO.File.WriteAllText(root + "/in/sub/two.txt", "two two");
            System.Formats.Tar.TarFile.CreateFromDirectory(root + "/in", root + "/in.tar", includeBaseDirectory: false);
            System.IO.Directory.CreateDirectory(root + "/out");
            System.Formats.Tar.TarFile.ExtractToDirectory(root + "/in.tar", root + "/out", overwriteFiles: false);
            Check("tar: a directory archived and extracted through the file system",
                  System.IO.File.Exists(root + "/out/one.txt") && System.IO.File.ReadAllText(root + "/out/one.txt") == "one"
                  && System.IO.File.ReadAllText(root + "/out/sub/two.txt") == "two two");
            System.IO.Directory.CreateDirectory(root + "/async");
            System.Formats.Tar.TarFile.ExtractToDirectoryAsync(root + "/in.tar", root + "/async", overwriteFiles: false).Wait();
            Check("tar: ExtractToDirectoryAsync, the same files",
                  System.IO.File.Exists(root + "/async/sub/two.txt") && System.IO.File.ReadAllText(root + "/async/sub/two.txt") == "two two");

            // The file system keeps a time set on it (FAT: local, to two seconds).
            var set = new DateTime(2020, 5, 6, 7, 8, 10);
            System.IO.File.SetLastWriteTime(root + "/in/one.txt", set);
            Check("file: SetLastWriteTime is what GetLastWriteTime reads back (" + System.IO.File.GetLastWriteTime(root + "/in/one.txt").ToString("s") + ")",
                  System.IO.File.GetLastWriteTime(root + "/in/one.txt") == set);
            // Extraction gives a file the time its entry has — to FAT's two
            // seconds, rounded down (03:04:05 is kept as 03:04:04).
            System.IO.Directory.CreateDirectory(root + "/timed");
            System.Formats.Tar.TarFile.ExtractToDirectory("/tests/sample-gnu.tar", root + "/timed", overwriteFiles: false);
            DateTime extracted = System.IO.File.GetLastWriteTimeUtc(root + "/timed/tartest/hello.txt");
            DateTime expected = when.UtcDateTime.AddSeconds(-(when.Second % 2));
            Check("tar: an extracted file has its entry's time, to FAT's two seconds (" + extracted.ToString("s") + "Z)",
                  extracted == expected);
            System.IO.Directory.Delete(root, true);
        }

        private static string Hex(byte[] bytes)
        {
            const string digits = "0123456789ABCDEF";
            var chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                chars[2 * i] = digits[bytes[i] >> 4];
                chars[2 * i + 1] = digits[bytes[i] & 15];
            }
            return new string(chars);
        }
    }
}
