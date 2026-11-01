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
