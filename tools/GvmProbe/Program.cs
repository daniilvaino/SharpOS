using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Internal.NativeFormat;

// Prints what ILC 8 put into an app image for generic virtual methods.
internal static unsafe class Program
{
    private static byte[] s_file;
    private static ulong s_imageBase;
    private static readonly List<(uint va, uint size, uint raw)> s_sections = new();

    private static int Main(string[] args)
    {
        s_file = File.ReadAllBytes(args[0]);
        s_verbose = args.Length > 1 && args[1] == "-v";
        ParsePe();
        long rtr = FindRtr();
        Console.WriteLine("image base 0x" + s_imageBase.ToString("x") + ", RTR at file 0x" + rtr.ToString("x"));
        var blobs = new Dictionary<int, (long file, uint size, ulong va)>();
        ushort n = BitConverter.ToUInt16(s_file, (int)rtr + 12);
        for (int i = 0; i < n; i++)
        {
            int row = (int)rtr + 16 + i * 24;
            int id = BitConverter.ToInt32(s_file, row);
            ulong start = BitConverter.ToUInt64(s_file, row + 8), end = BitConverter.ToUInt64(s_file, row + 16);
            if (id >= 300 && id < 400)
            {
                blobs[id - 300] = (VaToFile(start), (uint)(end - start), start);
                Console.WriteLine($"  blob {id - 300,3}: {end - start,7} bytes");
            }
        }

        fixed (byte* pinned = s_file)
        {
            byte* f = pinned;
            NativeReader Reader(int blob) => new NativeReader(f + blobs[blob].file, blobs[blob].size);
            NativeReader layout = Reader(30);
            ulong ExtRef(int table, uint index)
            {
                ulong entryVa = blobs[table].va + index * 4;
                int rel = BitConverter.ToInt32(s_file, (int)(blobs[table].file + index * 4));
                return (ulong)((long)entryVa + rel);
            }
            string NameAt(uint offset) { var p = new NativeParser(layout, offset); return p.GetString(); }

            Console.WriteLine("\n== GVM table (18), refs: CommonFixups (8)");
            var gvm = new NativeHashtable(new NativeParser(Reader(18), 0));
            var all = gvm.EnumerateAllEntries();
            for (NativeParser e = all.GetNext(); !e.IsNull; e = all.GetNext())
            {
                uint callingType = e.GetUnsigned(), targetType = e.GetUnsigned();
                uint callNs = e.GetUnsigned(), implNs = e.GetUnsigned();
                Console.WriteLine($"  calling type {Rva(ExtRef(8, callingType))} impl type {Rva(ExtRef(8, targetType))}  " +
                                  $"{NameAt(callNs)}@{callNs:x} -> {NameAt(implNs)}@{implNs:x}");
            }

            if (blobs.ContainsKey(19))
                Console.WriteLine("\n== interface GVM table (19): " + blobs[19].size + " bytes");

            Console.WriteLine("\n== exact method instantiations (36), refs: NativeReferences (31)");
            var exact = new NativeHashtable(new NativeParser(Reader(36), 0));
            all = exact.EnumerateAllEntries();
            for (NativeParser e = all.GetNext(); !e.IsNull; e = all.GetNext())
            {
                uint declaring = e.GetUnsigned();
                uint ns = e.GetUnsigned();            // the placed name+signature's offset in NativeLayout
                uint count = e.GetSequenceCount();
                var a = new StringBuilder();
                for (uint i = 0; i < count; i++) a.Append(' ').Append(Rva(ExtRef(31, e.GetUnsigned())));
                uint fn = e.GetUnsigned();
                Console.WriteLine($"  type {Rva(ExtRef(31, declaring))} {NameAt(ns)}@{ns:x} args[{a} ] -> code {Rva(ExtRef(31, fn))}");
            }

            Console.WriteLine("\n== generic method dictionaries (35)");
            var dicts = new NativeHashtable(new NativeParser(Reader(35), 0));
            all = dicts.EnumerateAllEntries();
            for (NativeParser e = all.GetNext(); !e.IsNull; e = all.GetNext())
            {
                uint dict = e.GetUnsigned(), type = e.GetUnsigned(), ns = e.GetUnsigned();
                uint count = e.GetSequenceCount();
                var a = new StringBuilder();
                for (uint i = 0; i < count; i++) a.Append(' ').Append(Rva(ExtRef(31, e.GetUnsigned())));
                Console.WriteLine($"  type {Rva(ExtRef(31, type))} {NameAt(ns)}@{ns:x} args[{a} ] -> dictionary {Rva(ExtRef(31, dict))}");
            }

            Console.WriteLine("\n== generic method templates (22)");
            var templates = new NativeHashtable(new NativeParser(Reader(22), 0));
            all = templates.EnumerateAllEntries();
            for (NativeParser e = all.GetNext(); !e.IsNull; e = all.GetNext())
            {
                uint entry = e.GetUnsigned(), layoutOffset = e.GetUnsigned();
                var m = new NativeParser(layout, entry);
                uint flags = m.GetUnsigned();
                string fptr = (flags & 4) != 0 ? Rva(ExtRef(31, m.GetUnsigned())) : "-";
                string type = TypeSig(ref m, ExtRef);
                uint nsAt = m.Offset;
                string name = m.GetString();
                uint sig = m.GetParserFromRelativeOffset().Offset;
                var a = new StringBuilder();
                if ((flags & 1) != 0)
                {
                    uint count = m.GetSequenceCount();
                    for (uint i = 0; i < count; i++) a.Append(' ').Append(TypeSig(ref m, ExtRef));
                }
                if (s_verbose) Console.WriteLine($"  flags {flags} type {type} {name} sig@{sig:x} args[{a} ] code {fptr}");
                s_templates++;

                // The dictionary layout: which cells this method's dictionary holds.
                var bag = new NativeParser(layout, layoutOffset);
                NativeParser cells = bag.GetParserForBagElementKind(BagElementKind.DictionaryLayout);
                if (cells.IsNull) continue;
                uint cellCount = cells.GetSequenceCount();
                var kinds = new StringBuilder();
                for (uint i = 0; i < cellCount; i++)
                {
                    uint kind = cells.GetUnsigned();
                    s_cellKinds[kind] = s_cellKinds.TryGetValue(kind, out int c) ? c + 1 : 1;
                    kinds.Append(' ').Append(kind.ToString("x"));
                    SkipCell(ref cells, kind, ExtRef);
                }
                if (s_verbose) Console.WriteLine($"     dictionary: {cellCount} cells, kinds{kinds}");
            }
            Console.WriteLine($"\n{s_templates} templates; dictionary cells by kind:");
            foreach (var kv in s_cellKinds) Console.WriteLine($"  kind 0x{kv.Key:x2}: {kv.Value}");
        }
        return 0;
    }

    private static bool s_verbose;
    private static int s_templates;
    private static readonly SortedDictionary<uint, int> s_cellKinds = new();

    // Past one dictionary cell's signature (FixupSignatureKind payloads, as
    // GenericDictionaryCell.ParseAndCreateCell reads them).
    private static void SkipCell(ref NativeParser p, uint kind, ExtRefFn ext)
    {
        switch (kind)
        {
            case 0x01: case 0x06: case 0x09: case 0x0a:   // TypeHandle, UnwrapNullable, AllocateObject, DefaultConstructor
                TypeSig(ref p, ext); return;
            case 0x02: case 0x05: case 0x0b:               // InterfaceCall (type, slot), StaticData (type, kind), ThreadStaticIndex
                TypeSig(ref p, ext); if (kind != 0x0b) p.GetUnsigned(); return;
            case 0x04: case 0x0d:                          // MethodDictionary, Method
                MethodSig(ref p, ext); return;
            case 0x07: case 0x08:                          // Field/MethodLdToken: a relative offset
                p.GetRelativeOffset(); return;
            case 0x21:                                     // NonGenericStaticConstrainedMethod: type, method
                TypeSig(ref p, ext); MethodSig(ref p, ext); return;
            case 0x22:                                     // GenericStaticConstrainedMethod: type, ldtoken offset
                TypeSig(ref p, ext); p.GetRelativeOffset(); return;
            default:
                throw new InvalidDataException("cell kind 0x" + kind.ToString("x"));
        }
    }

    private static void MethodSig(ref NativeParser p, ExtRefFn ext)
    {
        uint flags = p.GetUnsigned();
        if ((flags & 4) != 0) p.GetUnsigned();
        TypeSig(ref p, ext);
        p.GetString();
        p.GetRelativeOffset();
        if ((flags & 1) != 0)
        {
            uint count = p.GetSequenceCount();
            for (uint i = 0; i < count; i++) TypeSig(ref p, ext);
        }
    }

    private delegate ulong ExtRefFn(int table, uint index);

    // A type signature: External (an MT through NativeReferences) or other kinds by name.
    private static string TypeSig(ref NativeParser p, ExtRefFn ext)
    {
        uint v = p.GetUnsigned();
        uint kind = v & 0xF, data = v >> 4;
        switch (kind)
        {
            case 1:
            {
                NativeParser back = p.GetLookbackParser(data);
                return "lookback:" + TypeSig(ref back, ext);
            }
            case 2: return "mod" + data + "(" + TypeSig(ref p, ext) + ")";
            case 5: return "builtin" + data;
            case 0xA:
            {
                string element = TypeSig(ref p, ext);
                for (uint b = p.GetUnsigned(); b > 0; b--) p.GetUnsigned();
                for (uint b = p.GetUnsigned(); b > 0; b--) p.GetUnsigned();
                return element + "[rank" + data + "]";
            }
            case 0xB:
            {
                p.GetUnsigned();
                uint count = p.GetUnsigned();
                string ret = TypeSig(ref p, ext);
                for (uint i = 0; i < count; i++) TypeSig(ref p, ext);
                return "fnptr(" + ret + ")";
            }
            case 6: return Rva(ext(31, data));
            case 4: return ((data & 1) != 0 ? "!!" : "!") + (data >> 1);
            case 3:
            {
                string def = TypeSig(ref p, ext);
                var a = new StringBuilder(def + "<");
                for (uint i = 0; i < data; i++) a.Append(i > 0 ? "," : "").Append(TypeSig(ref p, ext));
                return a + ">";
            }
            default: return "kind" + kind + ":" + data;
        }
    }

    private static string Rva(ulong va) => "rva:" + (va - s_imageBase).ToString("x");

    private static void ParsePe()
    {
        int pe = BitConverter.ToInt32(s_file, 0x3C);
        int sections = BitConverter.ToUInt16(s_file, pe + 6);
        int optSize = BitConverter.ToUInt16(s_file, pe + 20);
        int opt = pe + 24;
        s_imageBase = BitConverter.ToUInt64(s_file, opt + 24);
        int table = opt + optSize;
        for (int i = 0; i < sections; i++)
        {
            int s = table + i * 40;
            s_sections.Add((BitConverter.ToUInt32(s_file, s + 12), BitConverter.ToUInt32(s_file, s + 8), BitConverter.ToUInt32(s_file, s + 20)));
        }
    }

    private static long VaToFile(ulong va)
    {
        uint rva = (uint)(va - s_imageBase);
        foreach (var (sva, size, raw) in s_sections)
            if (rva >= sva && rva < sva + Math.Max(size, 1)) return raw + (rva - sva);
        throw new InvalidDataException("rva 0x" + rva.ToString("x") + " is in no section");
    }

    private static long FindRtr()
    {
        for (int p = 0; p + 16 < s_file.Length; p += 4)
        {
            if (BitConverter.ToUInt32(s_file, p) != 0x00525452) continue;
            ushort major = BitConverter.ToUInt16(s_file, p + 4);
            if (major < 8 || major > 9 || s_file[p + 14] != 24) continue;
            return p;
        }
        throw new InvalidDataException("no RTR header");
    }
}
