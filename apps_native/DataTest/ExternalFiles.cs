using System;
using System.Collections.Generic;
using System.Formats.Cbor;
using System.IO;
using System.Text;
using SharpOS.AppSdk;
using SharpOS.Std.Pipes;

namespace DataApps
{
    // Tests 16 and 17 (step206): a stream of objects kept in a file as it is
    // (externalpipe), and CBOR.
    internal static unsafe partial class AppEntry
    {
        // ---- 16 ----

        private static void ExternalPipeFiles()
        {
            // A class: > keeps it, READ gives the same type and the same values back.
            using (PipeWriter<Record> w = Pipe.To("WRITE t16.pipe").Write<Record>())
                for (int i = 0; i < 3; i++) w.Copy(MakeRecord(i));
            byte[] file = File.ReadAllBytes("t16.pipe");
            Check("16: the file starts with the signature", ExternalPipe.StartsWithSignature(file));

            int records = 0, others = 0;
            foreach (View v in Pipe.From("READ t16.pipe"))
            {
                if (v.TypeName == "DataApps.Record") records++;
                else others++;
            }
            Check("16: READ gives three DataApps.Record messages", records == 3 && others == 0);
            Check("16: equal field by field" + RecordsBack("READ t16.pipe", 3), RecordsBack("READ t16.pipe", 3) == "");

            // >> of the same type adds; of another is refused and changes nothing.
            using (PipeWriter<Record> w = Pipe.To("WRITE t16.pipe --append").Write<Record>())
                for (int i = 0; i < 2; i++) w.Copy(MakeRecord(i));
            Check("16: >> of the same type: five messages" + RecordsBack("READ t16.pipe", 5), RecordsBack("READ t16.pipe", 5) == "");
            int five = File.ReadAllBytes("t16.pipe").Length;
            int other = Sh("PIPEGEN 2 >> t16.pipe");
            Check("16: >> of another type is refused (exit " + other.ToString() + ")", other == 1);
            Check("16: and the file is as it was", File.ReadAllBytes("t16.pipe").Length == five);

            // CONVERT --to externalpipe writes the same bytes; --from externalpipe reads them.
            Check("16: READ | CONVERT --to externalpipe > b", Sh("READ t16.pipe | CONVERT --to externalpipe > t16b.pipe") == 0);
            Check("16: the same bytes as the file", SameBytes(File.ReadAllBytes("t16.pipe"), File.ReadAllBytes("t16b.pipe")));
            Check("16: READ --bytes | CONVERT --from externalpipe" + RecordsBack("READ t16b.pipe --bytes | CONVERT --from externalpipe", 5),
                  RecordsBack("READ t16b.pipe --bytes | CONVERT --from externalpipe", 5) == "");

            // An Expando: JSON → Expando → file → Expando → JSON is the same text.
            Sh("PIPEGEN 5 | CONVERT --to json > t16in.json");
            Check("16: Expando into a file", Sh("READ t16in.json | CONVERT --from json > t16x.pipe") == 0);
            Check("16: and out of it", Sh("READ t16x.pipe | CONVERT --to json > t16x.json") == 0);
            Check("16: the JSON is the same", Text("t16x.json") == Text("t16in.json"));

            // A query binds over the file's description, as over a live writer.
            Sh("PIPEGEN 5 > t16g.pipe");
            int levels = 0, count = 0;
            foreach (View v in Pipe.From("READ t16g.pipe | KQL 'where Level > 2'"))
            {
                levels += (int)v["Level"];
                count++;
            }
            Check("16: READ | KQL 'where Level > 2': levels 3 and 4", count == 2 && levels == 7);

            // A cut file: the whole messages, then a failure naming READ.
            byte[] whole = File.ReadAllBytes("t16.pipe");
            byte[] cut = new byte[whole.Length - 10];
            Array.Copy(whole, cut, cut.Length);
            File.WriteAllBytes("t16cut.pipe", cut);
            int got = 0;
            PipelineException failed = null;
            try { foreach (View v in Pipe.From("READ t16cut.pipe")) got++; }
            catch (PipelineException e) { failed = e; }
            Check("16: a cut file: four messages, then READ fails with 1 (" + got.ToString() + ")",
                  got == 4 && failed != null && failed.Stage == "READ" && failed.ExitCode == 1);

            // Strings and bytes stay what they are: no header, nothing added.
            Sh("PIPEGEN 2 | CONVERT --to lines > t16s.txt");
            Check("16: a stream of strings is text", !ExternalPipe.StartsWithSignature(File.ReadAllBytes("t16s.txt"))
                  && Text("t16s.txt").StartsWith("PipeApps.LogEntry"));

            foreach (string f in new[] { "t16.pipe", "t16b.pipe", "t16x.pipe", "t16g.pipe", "t16cut.pipe" })
                File.WriteAllBytes(f, new byte[0]);
        }

        // "" when READ's records are the test's, numbered from 0 again after the first three; otherwise why not.
        private static string RecordsBack(string pipeline, int expected)
        {
            int n = 0;
            string differs = null;
            foreach (Record r in Pipe.From(pipeline).Into<Record>())
            {
                string d = Differs(MakeRecord(n < 3 ? n : n - 3), r);
                if (d != null && differs == null) differs = "record " + n.ToString() + ", " + d;
                n++;
            }
            if (n != expected) return " (" + n.ToString() + " records)";
            return differs == null ? "" : " (differs: " + differs + ")";
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        // ---- 17 ----

        private static void CborRoundTrip()
        {
            // A class through CBOR and back by name.
            using (PipeWriter<Record> w = Pipe.To("CONVERT --to cbor | WRITE t17.cbor").Write<Record>())
                for (int i = 0; i < 3; i++) w.Copy(MakeRecord(i));
            byte[] doc = File.ReadAllBytes("t17.cbor");
            Check("17: --to cbor: the self-described tag, an array, the break",
                  doc.Length > 5 && doc[0] == 0xD9 && doc[1] == 0xD9 && doc[2] == 0xF7 && doc[3] == 0x9F && doc[doc.Length - 1] == 0xFF);
            Check("17: --from cbor | Into<Record>" + RecordsBack("READ t17.cbor | CONVERT --from cbor", 3),
                  RecordsBack("READ t17.cbor | CONVERT --from cbor", 3) == "");

            using (PipeWriter<Record> w = Pipe.To("CONVERT --to cborseq | WRITE t17s.cbor").Write<Record>())
                for (int i = 0; i < 3; i++) w.Copy(MakeRecord(i));
            Check("17: --to cborseq: a map first", (File.ReadAllBytes("t17s.cbor")[0] & 0xE0) == 0xA0);
            Check("17: --from cborseq" + RecordsBack("READ t17s.cbor | CONVERT --from cborseq", 3),
                  RecordsBack("READ t17s.cbor | CONVERT --from cborseq", 3) == "");
            Check("17: --from cbor takes a sequence too" + RecordsBack("READ t17s.cbor | CONVERT --from cbor", 3),
                  RecordsBack("READ t17s.cbor | CONVERT --from cbor", 3) == "");

            // Test 2's JSON through CBOR: the same values.
            File.WriteAllText("t17in.json", Tricky);
            Check("17: JSON → CBOR → JSON",
                  Sh("READ t17in.json | CONVERT --from json | CONVERT --to cbor | CONVERT --from cbor | CONVERT --to json > t17out.json") == 0);
            string differ = JsonValues.Differ(JsonValues.ParseAll(Encoding.UTF8.GetBytes(Tricky)),
                                              JsonValues.ParseAll(File.ReadAllBytes("t17out.json")), "$");
            Check("17: the values are the same" + (differ != null ? " (" + differ + ")" : ""), differ == null);

            // Written elsewhere: a definite array, tags 1 and 37, a byte string, a number as a key, a half.
            var cbor = new CborWriter(CborConformanceMode.Lax);
            cbor.WriteStartArray(2);
            cbor.WriteStartMap(3);
            cbor.WriteTextString("t");
            cbor.WriteTag(CborTag.UnixTimeSeconds);
            cbor.WriteInt64(1700000000);
            cbor.WriteTextString("u");
            cbor.WriteTag((CborTag)37);
            cbor.WriteByteString(new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF, 0x01, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF });
            cbor.WriteTextString("b");
            cbor.WriteByteString(new byte[] { 1, 2, 3 });
            cbor.WriteEndMap();
            cbor.WriteStartMap(2);
            cbor.WriteInt64(1);
            cbor.WriteTextString("one");
            cbor.WriteTextString("h");
            cbor.WriteTextString("HALF");
            cbor.WriteEndMap();
            cbor.WriteEndArray();
            byte[] made = cbor.Encode();
            // The text "HALF" (64 48 41 4C 46) becomes the half 1.5 (F9 3E 00) and two spare bytes dropped.
            byte[] bytes = Half(made);
            File.WriteAllBytes("t17d.cbor", bytes);
            List<string> json = Lines("READ t17d.cbor | CONVERT --from cbor | CONVERT --to jsonl");
            string expected = "{\"t\":\"2023-11-14T22:13:20.0000000Z\",\"u\":\"01234567-89ab-cdef-0123-456789abcdef\",\"b\":\"AQID\"}\n"
                              + "{\"1\":\"one\",\"h\":1.5}\n";
            Check("17: tags, bytes, a number key, a half", Join(json) == expected);
            if (Join(json) != expected) Console.WriteLine(Join(json));

            // Errors: an element that is not a map; an array without its break.
            File.WriteAllBytes("t17e1.cbor", new byte[] { 0x82, 0xA0, 0x01 });
            File.WriteAllBytes("t17e2.cbor", new byte[] { 0x9F, 0xA1, 0x61, 0x61, 0x01 });
            Check("17: an element that is not a map: CONVERT fails with 1", FailsIn("READ t17e1.cbor | CONVERT --from cbor", "CONVERT", 1));
            Check("17: an array cut before its break: CONVERT fails with 1", FailsIn("READ t17e2.cbor | CONVERT --from cbor", "CONVERT", 1));
        }

        private static byte[] Half(byte[] made)
        {
            for (int i = 0; i + 5 <= made.Length; i++)
            {
                if (made[i] != 0x64 || made[i + 1] != (byte)'H' || made[i + 2] != (byte)'A' || made[i + 3] != (byte)'L' || made[i + 4] != (byte)'F')
                    continue;
                var result = new byte[made.Length - 2];
                Array.Copy(made, result, i);
                result[i] = 0xF9;
                result[i + 1] = 0x3E;
                result[i + 2] = 0x00;
                Array.Copy(made, i + 5, result, i + 3, made.Length - i - 5);
                return result;
            }
            return made;
        }

        private static bool FailsIn(string pipeline, string stage, int code)
        {
            try { foreach (View v in Pipe.From(pipeline)) { } }
            catch (PipelineException e) { return e.Stage == stage && e.ExitCode == code; }
            return false;
        }
    }
}
