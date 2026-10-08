using System;
using System.Collections.Generic;
using System.Runtime;
using SharpOS.AppSdk;
using SharpOS.Std.Exchange;
using SharpOS.Std.Pipes;

namespace PipeApps
{
    // SYNTH [n] — n objects of a type that exists in no program (step197).
    //
    // Each run makes up a type: a random name ("Ghost.Q7xk2"), the fields
    // Level (int), Weight (double), Name (string) and one more with a random
    // name. There is no class for it here or anywhere — no `new` of it is
    // compiled. What a pipe needs is only what a pipe carries: a description
    // of the type (key, name, size, fields with types and offsets) and, per
    // message, a block laid out by it — records of [header][key][fields], the
    // string a record of its own, a reference the offset of its key word.
    // Both are written here byte by byte.
    //
    //     SYNTH 5                         the objects on the screen
    //     SYNTH 20 | VIEWFILT 3           a stage that never heard of the type filters it
    //     SYNTH 3 | CONVERT --to json     and it becomes JSON like any other
    //     SYNTH 3 | PIPEFILT 0            a stage with a class refuses it by type
    internal static unsafe class AppEntry
    {
        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            return Run(AppHost.Arguments);
        }

        [RuntimeExport("SharpAppBootstrap")]
        private static int SharpAppBootstrap(ulong startupPointer)
        {
            RuntimeImports.ManagedStartup();
            return SharpAppEntry(startupPointer);
        }

        private static int Main() => Run(AppHost.Arguments);

        // The made-up type, as offsets from its key word (where a class's
        // table pointer would be). BaseSize counts the header word before it.
        private const int LevelAt = 8, WeightAt = 16, NameAt = 24, ExtraAt = 32, GhostBaseSize = 48;

        private static ulong s_random;

        private static int Run(string[] args)
        {
            int n = args.Length > 0 ? int.Parse(args[0]) : 5;
            s_random = (ulong)System.Diagnostics.Stopwatch.GetTimestamp() | 1;

            string typeName = "Ghost." + Word(1) + Word(5);
            string extraName = Word(1) + Word(4);

            // The string's description is std's own: a real type every reader knows.
            MessageCatalog.Ensure();
            TypeKeys.Description text = null;
            foreach (TypeKeys.Description d in TypeKeys.Declared)
                if (d.Name == "System.String") text = d;
            if (text == null) { Console.WriteLine("SYNTH: no System.String in the catalog"); return 1; }

            var ghost = new TypeKeys.Description
            {
                Key = MadeUpKey(typeName),
                Name = typeName,
                BaseSize = GhostBaseSize,
                Fields = new[]
                {
                    new TypeKeys.Field("Level", "System.Int32", LevelAt),
                    new TypeKeys.Field("Weight", "System.Double", WeightAt),
                    new TypeKeys.Field("Name", "System.String", NameAt),
                    new TypeKeys.Field(extraName, "System.Int64", ExtraAt),
                },
            };
            byte[] schema = RegionSchema.Build(new List<TypeKeys.Description> { ghost, text });

            AppHost.WriteString("\u001b[90mSYNTH: type " + typeName + ", key 0x" + ghost.Key.ToString("X")
                                + ", fields Level Weight Name " + extraName + " — in no program\u001b[0m\n");

            int output = Pipe.TakeOutput();
            RegionShapes shapes = null;
            if (output != 0)
            {
                PipeStatus opened = PipeTransport.OpenEnd(output, schema, ghost.Key, out string error);
                if (opened != PipeStatus.Ok) { Console.WriteLine("SYNTH: " + (error ?? "cannot open the output")); return 1; }
            }
            else
            {
                // No output: read our own blocks back the way a reader would.
                shapes = RegionShapes.Parse(schema, out string bad);
                if (shapes == null) { Console.WriteLine("SYNTH: " + bad); return 1; }
            }

            try
            {
                for (int i = 0; i < n; i++)
                {
                    string name = "ghost " + i.ToString() + " " + Word(3);
                    ulong stringSize = Round8(text.BaseSize + (ulong)name.Length * text.ComponentSize);
                    ulong size = GhostBaseSize + stringSize;
                    byte* block = (byte*)PipeTransport.Allocate(size);
                    if (block == null) { Console.WriteLine("SYNTH: no exchange memory"); return 1; }
                    for (ulong b = 0; b < size; b++) block[b] = 0;

                    // Record 0, the root: [header][key][Level][Weight][Name → record 1][extra].
                    byte* o = block + Region.HeaderSize;
                    *(ulong*)o = ghost.Key;
                    *(int*)(o + LevelAt) = (int)(Next() % 5);
                    *(double*)(o + WeightAt) = (Next() % 1000) / 8.0;
                    *(ulong*)(o + NameAt) = GhostBaseSize + Region.HeaderSize;   // the string's key word
                    *(long*)(o + ExtraAt) = (long)(Next() % 100000);

                    // Record 1, the string: [header][key][length][chars].
                    byte* s = block + GhostBaseSize + Region.HeaderSize;
                    *(ulong*)s = text.Key;
                    *(int*)(s + 8) = name.Length;
                    for (int c = 0; c < name.Length; c++) ((char*)(s + 12))[c] = name[c];

                    if (output != 0)
                    {
                        PipeStatus sent = PipeTransport.Send(output, block, size);
                        if (sent != PipeStatus.Ok)
                        {
                            PipeTransport.Free(block);
                            return sent == PipeStatus.Broken ? 141 : 1;
                        }
                        continue;
                    }

                    if (!shapes.Validate(block, size, out string complaint))
                    {
                        PipeTransport.Free(block);
                        Console.WriteLine("SYNTH: my own block fails the reader's check: " + complaint);
                        return 1;
                    }
                    var region = new RawRegion(block, size, schema, 0, shapes);
                    try { ScreenText.Print(region.Root); }
                    finally { region.Dispose(); }
                }
            }
            finally
            {
                if (output != 0) PipeTransport.Close(output);
            }
            return 0;
        }

        // Odd and non-canonical, as every key (TypeKeys): bit 0 set, bit 62
        // set with bit 63 clear — never a table pointer, never a marked one.
        private static ulong MadeUpKey(string name)
        {
            ulong h = 14695981039346656037UL;
            foreach (char c in name) { h ^= c; h *= 1099511628211UL; }
            return (h & 0x3FFF_FFFF_FFFF_FFFEUL) | 0x4000_0000_0000_0001UL;
        }

        private static ulong Round8(ulong v) => (v + 7) & ~7UL;

        // xorshift64: enough to make up names.
        private static ulong Next()
        {
            s_random ^= s_random << 13;
            s_random ^= s_random >> 7;
            s_random ^= s_random << 17;
            return s_random;
        }

        private static string Word(int length)
        {
            const string upper = "BCDFGHJKLMNPQRSTVWXZ", lower = "aeioubcdfghklmnprstvz";
            var chars = new char[length];
            for (int i = 0; i < length; i++)
                chars[i] = i == 0 && length == 1 ? upper[(int)(Next() % (ulong)upper.Length)]
                                                 : lower[(int)(Next() % (ulong)lower.Length)];
            return new string(chars);
        }
    }
}
