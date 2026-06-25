// step 121 — minimal COFF object file emitter (AMD64, Windows).
//
// Один файл = один или несколько data symbols в одной .data/.rdata/.bss
// section'е. Не нацелен на purpose-built linker input replacements для
// kernel CRT stub'ов. НЕ поддерживает: relocations, multiple sections,
// COMDAT, debug info, big-obj layout, line numbers, other architectures.
// Если потребуется — расширим.
//
// Reference: dotnet-runtime-sharpos/src/coreclr/tools/aot/ILCompiler.Compiler/
// Compiler/ObjectWriter/CoffObjectWriter.cs (PE/COFF spec mapping). Из
// него взяли только концептуальные структуры — без зависимостей на
// ILCompiler.DependencyAnalysis / Internal.TypeSystem.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BootAsm.CoffStub
{
    /// <summary>One data symbol entry inside an emitted .obj.</summary>
    public sealed class CoffDataEntry
    {
        public string SymbolName { get; set; }
        public byte[] Payload { get; set; }
        public int Alignment { get; set; }
    }

    /// <summary>
    /// Writes a minimal COFF .obj for AMD64 Windows with one section
    /// holding N data symbols. All symbols are externally visible
    /// (IMAGE_SYM_CLASS_EXTERNAL) so the kernel linker can resolve
    /// references against them.
    /// </summary>
    public static class CoffWriter
    {
        private const ushort MACHINE_AMD64 = 0x8664;

        // SectionCharacteristics flags (from PE/COFF spec).
        private const uint IMAGE_SCN_CNT_CODE              = 0x00000020;
        private const uint IMAGE_SCN_CNT_INITIALIZED_DATA  = 0x00000040;
        private const uint IMAGE_SCN_CNT_UNINITIALIZED_DATA = 0x00000080;
        private const uint IMAGE_SCN_MEM_READ              = 0x40000000;
        private const uint IMAGE_SCN_MEM_WRITE             = 0x80000000;
        // ALIGN_xBYTES = 0x00x00000 where x = log2(n)+1. 1B=0x100000, 2B=0x200000,
        // 4B=0x300000, 8B=0x400000, 16B=0x500000, ... 8192B=0xE00000.
        private static uint AlignFlag(int alignment)
        {
            int log = 0;
            int n = alignment;
            while (n > 1) { n >>= 1; log++; }
            return (uint)((log + 1) << 20);
        }

        // Symbol storage class.
        private const byte IMAGE_SYM_CLASS_EXTERNAL = 2;

        // COFF header layout sizes.
        private const int COFF_HEADER_SIZE  = 20;
        private const int SECTION_HEADER_SIZE = 40;
        private const int SYMBOL_RECORD_SIZE = 18;

        public static byte[] BuildObject(string sectionName, bool writable, bool initialized,
                                          int sectionAlignment, IList<CoffDataEntry> entries)
        {
            if (entries == null || entries.Count == 0)
                throw new ArgumentException("at least one entry required");
            if (sectionName.Length > 8)
                throw new ArgumentException("section name must be ≤ 8 ASCII chars: " + sectionName);

            // Lay out the section payload (concatenated entries with
            // per-entry alignment). Track each entry's offset for symbol
            // Value field.
            byte[] sectionPayload;
            var entryOffsets = new uint[entries.Count];
            using (var ms = new MemoryStream())
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    var e = entries[i];
                    int align = e.Alignment > 0 ? e.Alignment : 1;
                    long pad = (-ms.Length) & (align - 1);
                    for (long p = 0; p < pad; p++) ms.WriteByte(0);
                    entryOffsets[i] = (uint)ms.Length;
                    ms.Write(e.Payload, 0, e.Payload.Length);
                }
                sectionPayload = ms.ToArray();
            }

            // Build string table. Symbol names > 8 chars go in string
            // table; section name is always ≤ 8 chars (validated above).
            var stringTable = new StringTableBuilder();
            foreach (var e in entries)
            {
                if (Encoding.UTF8.GetByteCount(e.SymbolName) > 8)
                    stringTable.Reserve(e.SymbolName);
            }

            // File layout: header → section header → section payload →
            // symbol table → string table.
            uint sectionDataOffset = (uint)(COFF_HEADER_SIZE + SECTION_HEADER_SIZE);
            uint symbolTableOffset = sectionDataOffset + (uint)sectionPayload.Length;
            uint stringTableOffset = symbolTableOffset + (uint)(entries.Count * SYMBOL_RECORD_SIZE);

            byte[] result = new byte[stringTableOffset + stringTable.TotalSize];

            // ─── COFF header (20 bytes) ─────────────────────────────────
            WriteU16(result,  0, MACHINE_AMD64);
            WriteU16(result,  2, 1);                          // 1 section
            WriteU32(result,  4, 0);                          // timestamp
            WriteU32(result,  8, symbolTableOffset);
            WriteU32(result, 12, (uint)entries.Count);
            WriteU16(result, 16, 0);                          // optional header size
            WriteU16(result, 18, 0);                          // characteristics

            // ─── Section header (40 bytes) ──────────────────────────────
            WriteSectionName(result, COFF_HEADER_SIZE, sectionName);
            WriteU32(result, COFF_HEADER_SIZE +  8, 0);                                // VirtualSize
            WriteU32(result, COFF_HEADER_SIZE + 12, 0);                                // VirtualAddress
            WriteU32(result, COFF_HEADER_SIZE + 16, (uint)sectionPayload.Length);      // SizeOfRawData
            WriteU32(result, COFF_HEADER_SIZE + 20, sectionDataOffset);                // PointerToRawData
            WriteU32(result, COFF_HEADER_SIZE + 24, 0);                                // PointerToRelocations
            WriteU32(result, COFF_HEADER_SIZE + 28, 0);                                // PointerToLineNumbers
            WriteU16(result, COFF_HEADER_SIZE + 32, 0);                                // NumberOfRelocations
            WriteU16(result, COFF_HEADER_SIZE + 34, 0);                                // NumberOfLineNumbers

            uint charFlags = AlignFlag(sectionAlignment)
                | (initialized ? IMAGE_SCN_CNT_INITIALIZED_DATA : IMAGE_SCN_CNT_UNINITIALIZED_DATA)
                | IMAGE_SCN_MEM_READ
                | (writable ? IMAGE_SCN_MEM_WRITE : 0);
            WriteU32(result, COFF_HEADER_SIZE + 36, charFlags);

            // ─── Section payload ────────────────────────────────────────
            Array.Copy(sectionPayload, 0, result, (int)sectionDataOffset, sectionPayload.Length);

            // ─── Symbol table ───────────────────────────────────────────
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                int symOff = (int)symbolTableOffset + i * SYMBOL_RECORD_SIZE;
                WriteSymbolName(result, symOff, e.SymbolName, stringTable);
                WriteU32(result, symOff +  8, entryOffsets[i]);     // Value
                WriteU16(result, symOff + 12, 1);                   // SectionNumber (1-based)
                WriteU16(result, symOff + 14, 0);                   // Type
                result[symOff + 16] = IMAGE_SYM_CLASS_EXTERNAL;     // StorageClass
                result[symOff + 17] = 0;                            // NumberOfAuxSymbols
            }

            // ─── String table ───────────────────────────────────────────
            stringTable.WriteTo(result, (int)stringTableOffset);

            return result;
        }

        private static void WriteU16(byte[] buf, int off, ushort value)
        {
            buf[off    ] = (byte)(value      );
            buf[off + 1] = (byte)(value >>  8);
        }
        private static void WriteU32(byte[] buf, int off, uint value)
        {
            buf[off    ] = (byte)(value      );
            buf[off + 1] = (byte)(value >>  8);
            buf[off + 2] = (byte)(value >> 16);
            buf[off + 3] = (byte)(value >> 24);
        }
        private static void WriteSectionName(byte[] buf, int off, string name)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(name);
            for (int i = 0; i < 8; i++)
                buf[off + i] = i < bytes.Length ? bytes[i] : (byte)0;
        }
        private static void WriteSymbolName(byte[] buf, int off, string name, StringTableBuilder st)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(name);
            if (bytes.Length <= 8)
            {
                for (int i = 0; i < 8; i++)
                    buf[off + i] = i < bytes.Length ? bytes[i] : (byte)0;
            }
            else
            {
                // Long name: 4 bytes of zero + 4 bytes offset into string table.
                WriteU32(buf, off    , 0);
                WriteU32(buf, off + 4, st.GetOffset(name));
            }
        }

        /// <summary>
        /// String table for COFF symbol names &gt; 8 ASCII chars. First 4 bytes
        /// hold the total size (including the size field itself); strings
        /// are then concatenated null-terminated. Offsets start at 4 (first
        /// string lives immediately after the size field).
        /// </summary>
        private sealed class StringTableBuilder
        {
            private readonly Dictionary<string, uint> _offsets = new Dictionary<string, uint>();
            private readonly List<byte> _bytes = new List<byte>();

            public StringTableBuilder()
            {
                // 4-byte size field at offset 0; strings start at offset 4.
                // We'll fill the size at WriteTo time.
            }

            public void Reserve(string s)
            {
                if (_offsets.ContainsKey(s)) return;
                _offsets[s] = (uint)(_bytes.Count + 4);
                foreach (byte b in Encoding.UTF8.GetBytes(s)) _bytes.Add(b);
                _bytes.Add(0);
            }

            public uint GetOffset(string s)
            {
                if (!_offsets.TryGetValue(s, out uint o))
                    throw new InvalidOperationException("string not reserved: " + s);
                return o;
            }

            public int TotalSize => 4 + _bytes.Count;

            public void WriteTo(byte[] buf, int off)
            {
                uint size = (uint)TotalSize;
                buf[off    ] = (byte)(size      );
                buf[off + 1] = (byte)(size >>  8);
                buf[off + 2] = (byte)(size >> 16);
                buf[off + 3] = (byte)(size >> 24);
                for (int i = 0; i < _bytes.Count; i++) buf[off + 4 + i] = _bytes[i];
            }
        }
    }
}
