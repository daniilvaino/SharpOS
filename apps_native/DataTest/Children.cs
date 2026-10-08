using System;
using System.IO;
using SharpOS.AppSdk;
using SharpOS.Std.Pipes;

namespace DataApps
{
    // The other side of a test: DATATEST.EXE --child ROLE ... as a stage of a
    // pipeline the tests start.
    internal static unsafe partial class AppEntry
    {
        private static int Child(string[] args)
        {
            string role = args.Length > 1 ? args[1] : "";
            string a2 = args.Length > 2 ? args[2] : "";
            switch (role)
            {
                // Reads n messages of any type, then leaves: the stage before
                // it finds its output broken.
                case "take":
                {
                    int n = int.Parse(a2), seen = 0;
                    foreach (View v in Pipe.Read())
                        if (++seen == n) break;
                    return 0;
                }

                // Writes three records and fails: the stages after it find
                // their input broken.
                case "fail":
                {
                    using var o = Pipe.Write<LogEntry>();
                    for (int i = 0; i < 3; i++) o.Copy(new LogEntry { Level = i, Text = "before the failure" });
                    throw new InvalidOperationException("the stage fails on purpose");
                }

                // The bytes of the input (Pipe.ReadBytes): their count and a
                // checksum, as one string to the output.
                case "bytes-sum":
                {
                    long count = 0;
                    ulong sum = 0;
                    var piece = new byte[7000];
                    using (Stream input = Pipe.ReadBytes())
                    {
                        int n;
                        while ((n = input.Read(piece, 0, piece.Length)) > 0)
                        {
                            for (int i = 0; i < n; i++) sum = sum * 31 + piece[i];
                            count += n;
                        }
                    }
                    using var o = Pipe.Write<string>();
                    o.Copy(count.ToString() + " " + sum.ToString());
                    return 0;
                }

                // n bytes of a pattern through Pipe.WriteBytes, in uneven writes.
                case "bytes-gen":
                {
                    long n = long.Parse(a2);
                    var piece = new byte[1024];     // a write is at most 996 + 13 bytes
                    using Stream output = Pipe.WriteBytes();
                    long written = 0;
                    int step = 1;
                    while (written < n)
                    {
                        int k = (int)Math.Min(n - written, step);
                        for (int i = 0; i < k; i++) piece[i] = Pattern(written + i);
                        output.Write(piece, 0, k);
                        written += k;
                        step = step % 997 + 13;
                    }
                    return 0;
                }

                // n rows (test 5).
                case "gen":
                {
                    long n = long.Parse(a2);
                    using var o = Pipe.Write<Row>();
                    // About 112 bytes of JSON a row: a million rows are over 100 MiB.
                    var row = new Row { Name = "a row of the hundred-megabyte file, one of a million, number" };
                    for (long i = 0; i < n; i++)
                    {
                        row.Id = i;
                        row.Level = (int)(i % 7);
                        row.Value = i * 0.5;
                        o.Copy(row);
                    }
                    return 0;
                }

                // Counts messages of any type; the count goes out as a string.
                case "count":
                {
                    long seen = 0;
                    foreach (View v in Pipe.Read()) seen++;
                    using var o = Pipe.Write<string>();
                    o.Copy(seen.ToString());
                    return 0;
                }

                // Lines of text through Pipe.WriteText.
                case "text":
                {
                    using TextWriter o = Pipe.WriteText();
                    o.WriteLine("first line");
                    o.Write("second ");
                    o.Write("line\nthird line");
                    o.Flush();
                    return 0;
                }
            }
            Console.WriteLine("DATATEST: unknown role " + role);
            return 2;
        }

        internal static byte Pattern(long i) => (byte)(i * 7 + (i >> 9));
    }
}
