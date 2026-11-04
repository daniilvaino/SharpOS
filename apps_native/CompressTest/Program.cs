using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime;
using System.Security.Cryptography;
using System.Text;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;
using SharpCompress.Writers;
using SharpOS.AppSdk;

namespace CompressCheckApp
{
    // COMPRESSTEST.EXE — SharpCompress (vendor/SharpCompress) and BCL's
    // System.IO.Compression over its zlib, on the app tier; and the
    // cryptography the encrypted formats need. Archives in /tests/compress
    // were made on the build machine (bsdtar 3.8, Python 3.14; see
    // CompressTest/esp/compress), so each format is read as another
    // implementation wrote it. The exit code is the number of checks passed.
    internal static unsafe class AppEntry
    {
        private const string Samples = "/tests/compress/";
        private static int s_passed;
        private static int s_failed;

        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            return AppRuntime.Finish(Main());
        }

        private static void Check(string name, bool ok)
        {
            if (ok) s_passed++; else s_failed++;
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + name);
        }

        private static void Run(string name, Action check)
        {
            try
            {
                check();
            }
            catch (Exception e)
            {
                Check(name + ": " + e.GetType().Name + ": " + e.Message, false);
                Console.WriteLine(e.StackTrace);
            }
        }

        private static int Main()
        {
            Console.WriteLine("COMPRESSTEST: SharpCompress, System.IO.Compression, System.Security.Cryptography");
            Run("crypto", CheckCrypto);
            Run("bcl streams", CheckBclStreams);
            Run("bcl gzip from python", CheckGZipFromPython);
            Run("bcl zip", CheckBclZip);
            Run("sharpcompress deflate", CheckSharpDeflate);
            foreach (string name in new[] { "sample.zip", "sample.7z" })
                Run(name, () => CheckArchive(name));
            // Compressed tars are a reader's job in SharpCompress: the archive
            // API knows GZip, Tar, Rar, Zip and 7Zip, the reader API all of them.
            foreach (string name in new[] { "sample.tar.gz", "sample.tar.bz2", "sample.tar.xz", "sample.tar.zst", "sample.zip" })
                Run(name, () => CheckReader(name));
            foreach (string name in new[] { "Zip.deflate.WinzipAES.zip", "7Zip.LZMA2.Aes.7z", "7Zip.LZMA.Aes.7z", "7Zip.LZMA2.7z", "Rar5.encrypted_filesOnly.rar" })
                Run(name, () => CheckEncrypted(name));
            Run("sharpcompress zip writer", CheckSharpZipWriter);
            Run("code pages", CheckCodePages);
            Console.WriteLine("COMPRESSTEST: " + s_passed + " passed, " + s_failed + " failed");
            return s_passed;
        }

        // What make_samples.py put in every archive.
        private static byte[] SampleData()
        {
            var bytes = new byte[20000];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)(((i * 31 + 7) ^ (i >> 5)) & 0xFF);
            return bytes;
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static byte[] ReadAll(Stream s)
        {
            var copy = new MemoryStream();
            s.CopyTo(copy);
            return copy.ToArray();
        }

        private static string Hex(byte[] bytes)
        {
            const string digits = "0123456789abcdef";
            var chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                chars[2 * i] = digits[bytes[i] >> 4];
                chars[2 * i + 1] = digits[bytes[i] & 15];
            }
            return new string(chars);
        }

        private static byte[] FromHex(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)Convert.ToInt32(hex.Substring(2 * i, 2), 16);
            return bytes;
        }

        // The vectors each standard publishes: the port against the
        // algorithm, not against itself.
        private static void CheckCrypto()
        {
            byte[] abc = Encoding.ASCII.GetBytes("abc");
            Check("crypto: SHA1(abc) (FIPS 180)", Hex(SHA1.HashData(abc)) == "a9993e364706816aba3e25717850c26c9cd0d89d");
            Check("crypto: SHA256(abc) (FIPS 180)", Hex(SHA256.HashData(abc)) == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
            using (var sha = SHA256.Create())
            {
                sha.TransformBlock(abc, 0, 1, null, 0);
                sha.TransformFinalBlock(abc, 1, 2);
                Check("crypto: SHA256 in two blocks is the one-shot", Hex(sha.Hash!) == "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
            }

            // RFC 4231 test case 2 and RFC 2202 test case 2: key "Jefe".
            byte[] jefe = Encoding.ASCII.GetBytes("Jefe");
            byte[] what = Encoding.ASCII.GetBytes("what do ya want for nothing?");
            Check("crypto: HMAC-SHA256 (RFC 4231 #2)", Hex(HMACSHA256.HashData(jefe, what)) == "5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843");
            using (var hmac = new HMACSHA1(jefe))
                Check("crypto: HMAC-SHA1 (RFC 2202 #2)", Hex(hmac.ComputeHash(what)) == "effcdf6ae5eb2fa2d27416d5f184df9c259a7c79");

            // RFC 6070 test case 2: "password", "salt", 2 iterations.
            byte[] dk = Rfc2898DeriveBytes.Pbkdf2(Encoding.ASCII.GetBytes("password"), Encoding.ASCII.GetBytes("salt"), 2, HashAlgorithmName.SHA1, 20);
            Check("crypto: PBKDF2-SHA1 (RFC 6070 #2)", Hex(dk) == "ea6c014dc72d6f8ccd1ed92ace1d41f0d8de8957");

            // FIPS-197 appendix C.1 (AES-128) and C.3 (AES-256), one block, ECB.
            using (Aes aes = Aes.Create())
            {
                aes.Key = FromHex("000102030405060708090a0b0c0d0e0f");
                byte[] plain = FromHex("00112233445566778899aabbccddeeff");
                byte[] cipher = aes.EncryptEcb(plain, PaddingMode.None);
                Check("crypto: AES-128 (FIPS-197 C.1) " + Hex(cipher), Hex(cipher) == "69c4e0d86a7b0430d8cdb78070b4c55a");
                Check("crypto: AES-128 decrypts back", Hex(aes.DecryptEcb(cipher, PaddingMode.None)) == Hex(plain));
                aes.Key = FromHex("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
                Check("crypto: AES-256 (FIPS-197 C.3)", Hex(aes.EncryptEcb(plain, PaddingMode.None)) == "8ea2b7ca516745bfeafc49904b496089");
            }

            // CBC with PKCS7 through the transform and a CryptoStream, back.
            using (Aes aes = Aes.Create())
            {
                aes.Key = FromHex("2b7e151628aed2a6abf7158809cf4f3c");
                aes.IV = FromHex("000102030405060708090a0b0c0d0e0f");
                byte[] message = SampleData();
                var encrypted = new MemoryStream();
                using (var cs = new CryptoStream(encrypted, aes.CreateEncryptor(), CryptoStreamMode.Write, leaveOpen: true))
                    cs.Write(message, 0, message.Length);
                long encryptedLength = encrypted.Length;
                encrypted.Position = 0;
                byte[] back;
                using (var cs = new CryptoStream(encrypted, aes.CreateDecryptor(), CryptoStreamMode.Read))
                    back = ReadAll(cs);
                Check("crypto: AES-CBC/PKCS7 through CryptoStream, " + encryptedLength + " bytes, and back",
                      encryptedLength == 20016 && Same(back, message));
                // NIST SP 800-38A F.2.1, the first block of CBC-AES128.
                byte[] first = aes.EncryptCbc(FromHex("6bc1bee22e409f96e93d7e117393172a"), aes.IV, PaddingMode.None);
                Check("crypto: AES-CBC (SP 800-38A F.2.1)", Hex(first) == "7649abac8119b246cee98e9b12e9197d");
            }

            // NIST SP 800-38A F.2.6, CBC-AES256.Decrypt, the way 7-Zip's
            // AesDecoderStream drives it: CreateDecryptor(key, iv), no padding,
            // the algorithm object disposed, blocks in place one call at a time.
            {
                ICryptoTransform decryptor;
                using (Aes aes = Aes.Create())
                {
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.None;
                    decryptor = aes.CreateDecryptor(
                        FromHex("603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4"),
                        FromHex("000102030405060708090a0b0c0d0e0f"));
                }
                byte[] blocks = FromHex("f58c4c04d6e5f1ba779eabfb5f7bfbd69cfc4e967edb808d679f777bc6702c7d");
                decryptor.TransformBlock(blocks, 0, 16, blocks, 0);
                decryptor.TransformBlock(blocks, 16, 16, blocks, 16);
                Check("crypto: AES-256-CBC decrypt in place (SP 800-38A F.2.6) " + Hex(blocks),
                      Hex(blocks) == "6bc1bee22e409f96e93d7e117393172aae2d8a571e03ac9c9eb76fac45af8e51");
            }

            // 7-Zip's key derivation itself: no salt, "test", 2^19 rounds —
            // the reference value is Python hashlib's on the build machine.
            using (var sha = SHA256.Create())
            {
                byte[] pass = Encoding.Unicode.GetBytes("test");
                byte[] counter = new byte[8];
                for (long round = 0; round < (1L << 19); round++)
                {
                    sha.TransformBlock(Array.Empty<byte>(), 0, 0, null, 0);
                    sha.TransformBlock(pass, 0, pass.Length, null, 0);
                    sha.TransformBlock(counter, 0, 8, null, 0);
                    for (int i = 0; i < 8; i++) if (++counter[i] != 0) break;
                }
                sha.TransformFinalBlock(counter, 0, 0);
                Check("crypto: 7-Zip key derivation, 2^19 rounds " + Hex(sha.Hash!),
                      Hex(sha.Hash!) == "886660203c30b116ac07bc8d24066697f35e476e7f07d6118ea9f27fbfb5d27b");
            }

            // 7-Zip's key derivation shape: many small TransformBlocks, then
            // an empty final block — the same bytes hashed in one go.
            using (var sha = SHA256.Create())
            {
                byte[] salt = new byte[] { 1, 2, 3 };
                byte[] pass = Encoding.Unicode.GetBytes("test");
                byte[] counter = new byte[8];
                var all = new MemoryStream();
                for (int round = 0; round < 5000; round++)
                {
                    sha.TransformBlock(salt, 0, salt.Length, null, 0);
                    sha.TransformBlock(pass, 0, pass.Length, null, 0);
                    sha.TransformBlock(counter, 0, 8, null, 0);
                    all.Write(salt); all.Write(pass); all.Write(counter);
                    for (int i = 0; i < 8; i++) if (++counter[i] != 0) break;
                }
                sha.TransformFinalBlock(counter, 0, 0);
                Check("crypto: SHA256 over 15000 TransformBlocks is the one-shot", Hex(sha.Hash!) == Hex(SHA256.HashData(all.ToArray())));
            }
        }

        // BCL's three streams, both ways, at each level; GZip and ZLib
        // checked against the framing their RFCs fix.
        private static void CheckBclStreams()
        {
            byte[] input = SampleData();
            foreach (CompressionLevel level in new[] { CompressionLevel.Optimal, CompressionLevel.Fastest, CompressionLevel.NoCompression, CompressionLevel.SmallestSize })
            {
                var packed = new MemoryStream();
                using (var z = new DeflateStream(packed, level, leaveOpen: true))
                    z.Write(input, 0, input.Length);
                long packedLength = packed.Length;
                packed.Position = 0;
                byte[] back;
                using (var z = new DeflateStream(packed, CompressionMode.Decompress))
                    back = ReadAll(z);
                Check("bcl: DeflateStream " + level.ToString() + ", " + packedLength + " bytes, and back", Same(back, input));
            }

            var gz = new MemoryStream();
            using (var z = new GZipStream(gz, CompressionMode.Compress, leaveOpen: true))
            {
                z.Write(input, 0, 1000);
                z.Flush();
                z.Write(input, 1000, input.Length - 1000);
            }
            byte[] gzBytes = gz.ToArray();
            Check("bcl: GZipStream writes the gzip magic (RFC 1952)", gzBytes.Length > 18 && gzBytes[0] == 0x1F && gzBytes[1] == 0x8B && gzBytes[2] == 8);
            uint size = (uint)(gzBytes[^4] | gzBytes[^3] << 8 | gzBytes[^2] << 16 | gzBytes[^1] << 24);
            uint crc = (uint)(gzBytes[^8] | gzBytes[^7] << 8 | gzBytes[^6] << 16 | gzBytes[^5] << 24);
            Check("bcl: GZipStream trailer: CRC-32 and size of the input",
                  size == (uint)input.Length && crc == System.IO.Hashing.Crc32.HashToUInt32(input));
            gz.Position = 0;
            using (var z = new GZipStream(gz, CompressionMode.Decompress))
                Check("bcl: GZipStream, with a flush in the middle, and back", Same(ReadAll(z), input));

            var zl = new MemoryStream();
            using (var z = new ZLibStream(zl, CompressionLevel.Optimal, leaveOpen: true))
                z.Write(input, 0, input.Length);
            byte[] zlBytes = zl.ToArray();
            Check("bcl: ZLibStream header (RFC 1950: CM 8, FCHECK)", (zlBytes[0] & 0x0F) == 8 && ((zlBytes[0] << 8) | zlBytes[1]) % 31 == 0);
            zl.Position = 0;
            using (var z = new ZLibStream(zl, CompressionMode.Decompress))
                Check("bcl: ZLibStream and back", Same(ReadAll(z), input));

            // Corrupt data is an InvalidDataException, as BCL's zlib makes it.
            byte[] broken = (byte[])gzBytes.Clone();
            broken[broken.Length - 6] ^= 0xFF;
            bool threw = false;
            try
            {
                using var z = new GZipStream(new MemoryStream(broken), CompressionMode.Decompress);
                ReadAll(z);
            }
            catch (InvalidDataException)
            {
                threw = true;
            }
            Check("bcl: a bad gzip CRC is InvalidDataException", threw);
        }

        // gzip(1)'s optional header fields: FNAME and MTIME, as Python writes them.
        private static void CheckGZipFromPython()
        {
            using var file = File.OpenRead(Samples + "hello.txt.gz");
            using var z = new GZipStream(file, CompressionMode.Decompress);
            Check("bcl: GZipStream reads Python's gzip (header with FNAME)", Same(ReadAll(z), SampleData()));
        }

        private static void CheckBclZip()
        {
            using (ZipArchive zip = ZipFile.OpenRead(Samples + "python.zip"))
            {
                var names = new List<string>();
                foreach (ZipArchiveEntry e in zip.Entries) names.Add(e.FullName);
                ZipArchiveEntry? data = zip.GetEntry("ctest/data.bin");
                byte[] bytes = data == null ? new byte[0] : ReadAll(data.Open());
                Check("bcl zip: Python's archive, three entries (" + string.Join(", ", names) + ")", names.Count == 3);
                Check("bcl zip: ctest/data.bin inflated, CRC checked", Same(bytes, SampleData()));
                Check("bcl zip: the entry's time is 2024-01-02 03:04:04", data != null && data.LastWriteTime.DateTime == new DateTime(2024, 1, 2, 3, 4, 4));
            }

            // Written by ZipArchive, read back by SharpCompress: two
            // implementations agree on the format, not one with itself.
            var archive = new MemoryStream();
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (Stream s = zip.CreateEntry("one.txt").Open()) s.Write(Encoding.UTF8.GetBytes("one"));
                using (Stream s = zip.CreateEntry("dir/data.bin", CompressionLevel.SmallestSize).Open()) s.Write(SampleData());
            }
            archive.Position = 0;
            var read = new Dictionary<string, byte[]>();
            using (IArchive sharp = ArchiveFactory.OpenArchive(archive, new ReaderOptions { LeaveStreamOpen = true }))
            {
                foreach (IArchiveEntry e in sharp.Entries)
                    if (!e.IsDirectory) read[e.Key!] = ReadAll(e.OpenEntryStream());
            }
            Check("bcl zip: ZipArchive's archive read by SharpCompress",
                  read.Count == 2 && Encoding.UTF8.GetString(read["one.txt"]) == "one" && Same(read["dir/data.bin"], SampleData()));

            // ZipFile through the file system and back.
            string root = "/home/compresstest-" + Environment.ProcessId.ToString();
            if (Directory.Exists(root)) Directory.Delete(root, true);
            Directory.CreateDirectory(root + "/in/sub");
            File.WriteAllText(root + "/in/a.txt", "alpha");
            File.WriteAllBytes(root + "/in/sub/b.bin", SampleData());
            ZipFile.CreateFromDirectory(root + "/in", root + "/in.zip");
            ZipFile.ExtractToDirectory(root + "/in.zip", root + "/out");
            Check("bcl zip: ZipFile.CreateFromDirectory and ExtractToDirectory",
                  File.ReadAllText(root + "/out/a.txt") == "alpha" && Same(File.ReadAllBytes(root + "/out/sub/b.bin"), SampleData()));
            Directory.Delete(root, true);
        }

        private static void CheckSharpDeflate()
        {
            byte[] input = SampleData();
            var packed = new MemoryStream();
            using (var z = new SharpCompress.Compressors.Deflate.DeflateStream(packed, SharpCompress.Compressors.CompressionMode.Compress, SharpCompress.Compressors.Deflate.CompressionLevel.Default, leaveOpen: true))
                z.Write(input, 0, input.Length);
            long packedLength = packed.Length;
            packed.Position = 0;
            using (var z = new SharpCompress.Compressors.Deflate.DeflateStream(packed, SharpCompress.Compressors.CompressionMode.Decompress, SharpCompress.Compressors.Deflate.CompressionLevel.Default))
                Check("sharpcompress: DeflateStream, " + packedLength + " bytes, and back", Same(ReadAll(z), input));
        }

        // Every sample holds ctest/{hello.txt, data.bin, sub/deep.txt}.
        private static void CheckArchive(string name)
        {
            var files = new Dictionary<string, byte[]>();
            using (Stream file = File.OpenRead(Samples + name))
            using (IArchive archive = ArchiveFactory.OpenArchive(file))
            {
                foreach (IArchiveEntry e in archive.Entries)
                {
                    if (e.IsDirectory) continue;
                    using Stream s = e.OpenEntryStream();
                    files[e.Key!.Replace('\\', '/')] = ReadAll(s);
                }
            }
            Check("sharpcompress archive: " + name + " — three files, contents (" + string.Join(", ", files.Keys) + ")", SampleFiles(files));
        }

        private static void CheckReader(string name)
        {
            var files = new Dictionary<string, byte[]>();
            using (Stream file = File.OpenRead(Samples + name))
            using (IReader reader = ReaderFactory.OpenReader(file))
            {
                while (reader.MoveToNextEntry())
                {
                    if (reader.Entry.IsDirectory) continue;
                    using Stream s = reader.OpenEntryStream();
                    files[reader.Entry.Key!.Replace('\\', '/')] = ReadAll(s);
                }
            }
            Check("sharpcompress reader: " + name + " — three files, contents (" + string.Join(", ", files.Keys) + ")", SampleFiles(files));
        }

        private static bool SampleFiles(Dictionary<string, byte[]> files) =>
            files.Count == 3
            && files.TryGetValue("ctest/hello.txt", out byte[]? hello) && Encoding.ASCII.GetString(hello) == "Hello, archive!\n"
            && files.TryGetValue("ctest/sub/deep.txt", out byte[]? deep) && Encoding.ASCII.GetString(deep) == "deep\n"
            && files.TryGetValue("ctest/data.bin", out byte[]? data) && Same(data, SampleData());

        // SharpCompress's own encrypted test archives (its tests' passwords:
        // "testpassword" for 7z, "test" for the others): AES-256
        // with WinZip's PBKDF2-HMAC-SHA1 and HMAC-SHA1, 7-Zip's SHA-256 key
        // derivation, RAR5's PBKDF2-HMAC-SHA256. The three files' sizes and
        // CRC-32s are the originals', taken on the build machine.
        private static void CheckEncrypted(string name)
        {
            var expected = new Dictionary<string, (int Size, uint Crc)>
            {
                ["test.exe"] = (45056, 0xCFB109C8u),
                ["test.jpg"] = (40372, 0x088814E3u),
                ["тест.txt"] = (15498, 0x9BD160FAu),
            };
            int matched = 0;
            var seen = new List<string>();
            string password = name.StartsWith("7Zip.") ? "testpassword" : "test";
            using (Stream file = File.OpenRead(Samples + name))
            using (IArchive archive = ArchiveFactory.OpenArchive(file, new ReaderOptions { Password = password }))
            {
                foreach (IArchiveEntry e in archive.Entries)
                {
                    if (e.IsDirectory) continue;
                    string key = e.Key!.Replace('\\', '/');
                    string leaf = key.Substring(key.LastIndexOf('/') + 1);
                    using Stream s = e.OpenEntryStream();
                    byte[] bytes = ReadAll(s);
                    seen.Add(leaf + ":" + bytes.Length);
                    if (expected.TryGetValue(leaf, out var want) && bytes.Length == want.Size && System.IO.Hashing.Crc32.HashToUInt32(bytes) == want.Crc)
                        matched++;
                }
            }
            Check("sharpcompress encrypted: " + name + " — three files by size and CRC-32 (" + string.Join(", ", seen) + ")", matched == 3);
        }

        // SharpCompress writes; BCL's ZipArchive reads.
        private static void CheckSharpZipWriter()
        {
            var archive = new MemoryStream();
            using (IWriter writer = WriterFactory.OpenWriter(archive, ArchiveType.Zip, new WriterOptions(CompressionType.Deflate) { LeaveStreamOpen = true }))
            {
                writer.Write("x/hello.txt", new MemoryStream(Encoding.UTF8.GetBytes("hello")), new DateTime(2024, 1, 2, 3, 4, 4));
                writer.Write("x/data.bin", new MemoryStream(SampleData()), null);
            }
            archive.Position = 0;
            using var zip = new ZipArchive(archive, ZipArchiveMode.Read);
            ZipArchiveEntry? hello = zip.GetEntry("x/hello.txt");
            ZipArchiveEntry? data = zip.GetEntry("x/data.bin");
            Check("sharpcompress: ZipWriter's archive read by ZipArchive",
                  hello != null && data != null
                  && Encoding.UTF8.GetString(ReadAll(hello.Open())) == "hello"
                  && Same(ReadAll(data.Open()), SampleData()));
        }

        // System.Text.Encoding.CodePages over std's BCL Encoding: bytes each
        // code page's table gives, both ways; then a zip whose entry name is in
        // CP437 (no UTF-8 flag), written by BCL's ZipArchive and read back by
        // SharpCompress told to read names without the flag as CP437 (its
        // default is Encoding.Default, UTF-8, as upstream on .NET Core).
        private static void CheckCodePages()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            CheckCodePage(1251, "Привет", "cff0e8e2e5f2");
            CheckCodePage(866, "Привет", "8fe0a8a2a5e2");
            CheckCodePage(437, "╔═╗", "c9cdbb");
            CheckCodePage(932, "日本", "93fa967b");
            CheckCodePage(54936, "中文", "d6d0cec4");
            Check("codepages: windows-1252 0x80 is the euro sign", Encoding.GetEncoding("windows-1252").GetString(new byte[] { 0x80 }) == "€");
            Check("codepages: koi8-r by name is 20866", Encoding.GetEncoding("koi8-r").CodePage == 20866);

            Encoding cp437 = Encoding.GetEncoding(437);
            var archive = new MemoryStream();
            using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: cp437))
            {
                using Stream entry = zip.CreateEntry("Äpfel.txt").Open();
                entry.Write(Encoding.UTF8.GetBytes("apples"));
            }
            byte[] bytes = archive.ToArray();
            Check("codepages: zip entry name stored as CP437 (0x8e for Ä)", IndexOf(bytes, new byte[] { 0x8E, (byte)'p', (byte)'f' }) >= 0);
            archive.Position = 0;
            var names = new List<string>();
            using (IArchive sharp = ArchiveFactory.OpenArchive(archive, new ReaderOptions { LeaveStreamOpen = true, ArchiveEncoding = new ArchiveEncoding { Default = cp437 } }))
                foreach (var e in sharp.Entries)
                    names.Add(e.Key ?? "");
            Check("codepages: SharpCompress reads the CP437 name (" + string.Join(", ", names) + ")", names.Count == 1 && names[0] == "Äpfel.txt");
        }

        private static void CheckCodePage(int codePage, string text, string hex)
        {
            Encoding e = Encoding.GetEncoding(codePage);
            byte[] bytes = e.GetBytes(text);
            Check("codepages: " + codePage + " " + text + " is " + hex + " (" + Hex(bytes) + ")", Hex(bytes) == hex && e.GetString(bytes) == text);
        }

        private static int IndexOf(byte[] data, byte[] what)
        {
            for (int i = 0; i + what.Length <= data.Length; i++)
            {
                int j = 0;
                while (j < what.Length && data[i + j] == what[j]) j++;
                if (j == what.Length) return i;
            }
            return -1;
        }
    }
}
