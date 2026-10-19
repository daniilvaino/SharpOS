using System;
using System.Collections.Generic;
using System.IO;
using SharpOS.AppSdk;

namespace DataApps
{
    // Test 26 (step208): a file written anywhere — Seek, a write in the
    // middle, across a cluster's edge, over the end, past it (the gap is
    // zeros), SetLength down and up (up is zeros, not what was cut), reads
    // on the same stream; then a run of random writes, cuts and reads
    // against a model in memory.
    internal static unsafe partial class AppEntry
    {
        private static void InPlace()
        {
            const string Root = "t26";
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
            Directory.CreateDirectory(Root);
            string path = Root + "/data.bin";

            var model = new List<byte>();
            for (int i = 0; i < 20000; i++) model.Add((byte)(i * 7 + 3));
            File.WriteAllBytes(path, model.ToArray());

            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
            {
                Check("26: Open keeps the content: length and position", stream.Length == 20000 && stream.Position == 0 && stream.CanSeek && stream.CanRead);

                WriteAt(stream, model, 5000, 100, 0x58);
                Check("26: a write in the middle", Matches(stream, model));
                WriteAt(stream, model, 4090, 20, 0x41);
                Check("26: across a cluster's edge", Matches(stream, model));
                WriteAt(stream, model, 19990, 10, 0x42);
                Check("26: the last bytes, in the last part-filled cluster", Matches(stream, model));
                WriteAt(stream, model, 19995, 30, 0x43);
                Check("26: over the end: the file grows (" + stream.Length.ToString() + ")", stream.Length == 20025 && Matches(stream, model));
                WriteAt(stream, model, 30000, 5, 0x44);
                Check("26: past the end: the gap is zeros (" + stream.Length.ToString() + ")", stream.Length == 30005 && Matches(stream, model));

                stream.SetLength(10000);
                model.RemoveRange(10000, model.Count - 10000);
                Check("26: SetLength down", stream.Length == 10000 && Matches(stream, model));
                stream.SetLength(15000);
                while (model.Count < 15000) model.Add(0);
                Check("26: SetLength up: zeros, not what was cut", stream.Length == 15000 && Matches(stream, model));

                stream.Position = 100;
                var piece = new byte[50];
                int got = stream.Read(piece, 0, 50);
                bool same = got == 50;
                for (int i = 0; i < 50 && same; i++) same = piece[i] == model[100 + i];
                Check("26: a read at the position, on the same stream", same && stream.Position == 150);

                // Random writes, cuts and reads.
                s_rng = 0x2545F4914F6CDD1DUL;
                int ops = s_quick ? 120 : 300;
                bool all = true;
                string where = null;
                for (int op = 0; op < ops && all; op++)
                {
                    int kind = Below(10);
                    if (kind < 6)
                    {
                        int at = Below(model.Count + 9000);
                        WriteAt(stream, model, at, 1 + Below(kind < 3 ? 300 : 12000), (byte)(op & 0xFF));
                    }
                    else if (kind < 8)
                    {
                        int length = Below(model.Count + 5000);
                        stream.SetLength(length);
                        if (length < model.Count) model.RemoveRange(length, model.Count - length);
                        while (model.Count < length) model.Add(0);
                    }
                    else if (!Matches(stream, model))
                    {
                        all = false;
                        where = "op " + op.ToString();
                    }
                }
                Check("26: " + ops.ToString() + " random writes, cuts and reads" + (where != null ? " (" + where + ")" : ""), all && Matches(stream, model));
            }
            Check("26: closed: the file on the disk is the model (" + new FileInfo(path).Length.ToString() + " bytes)",
                  SameBytes(File.ReadAllBytes(path), model.ToArray()));

            File.WriteAllText(Root + "/digits.txt", "12345");
            using (FileStream w = File.OpenWrite(Root + "/digits.txt"))
                w.Write(new byte[] { (byte)'a', (byte)'b' }, 0, 2);
            Check("26: File.OpenWrite keeps what it does not write", File.ReadAllText(Root + "/digits.txt") == "ab345");
            using (var w = new FileStream(Root + "/w.bin", FileMode.Create, FileAccess.Write))
                Check("26: a stream only written cannot read", !w.CanRead && Throws(e => e is NotSupportedException, () => w.Read(new byte[1], 0, 1)));
            Check("26: FileMode.Open of a missing file: FileNotFoundException; CreateNew of one there: IOException",
                  Throws(e => e is FileNotFoundException, () => new FileStream(Root + "/none.bin", FileMode.Open, FileAccess.ReadWrite))
                  && Throws(e => e is IOException, () => new FileStream(Root + "/digits.txt", FileMode.CreateNew, FileAccess.Write)));

            Directory.Delete(Root, true);
            Check("26: all of it deleted", !Directory.Exists(Root));
        }

        // `count` bytes of `value` written at `at`, in the file and in the model.
        private static void WriteAt(FileStream stream, List<byte> model, int at, int count, byte value)
        {
            var data = new byte[count];
            for (int i = 0; i < count; i++) data[i] = (byte)(value + i);
            stream.Position = at;
            stream.Write(data, 0, count);
            while (model.Count < at) model.Add(0);
            for (int i = 0; i < count; i++)
            {
                if (at + i < model.Count) model[at + i] = data[i];
                else model.Add(data[i]);
            }
        }

        // The stream's whole content against the model, read through the stream.
        private static bool Matches(FileStream stream, List<byte> model)
        {
            if (stream.Length != model.Count) return false;
            long keep = stream.Position;
            stream.Position = 0;
            var all = new byte[model.Count];
            int read = 0;
            while (read < all.Length)
            {
                int n = stream.Read(all, read, all.Length - read);
                if (n == 0) break;
                read += n;
            }
            stream.Position = keep;
            if (read != model.Count) return false;
            for (int i = 0; i < all.Length; i++) if (all[i] != model[i]) return false;
            return true;
        }
    }
}
