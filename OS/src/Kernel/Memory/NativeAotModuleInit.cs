using OS.Hal;
using SharpOS.Std.NoRuntime;

namespace OS.Kernel.Memory
{
    // One-shot NativeAOT module initialization.
    //
    // In a regular NativeAOT binary, Bootstrap/main.cpp calls:
    //   InitializeModules(osModule, __modules_a, count, classlibFns, nFns)
    // which walks per-module ReadyToRunHeaders and for each:
    //   1. allocates a TypeManager,
    //   2. writes its pointer into the module's TypeManagerIndirection
    //      section slots (one per module),
    //   3. runs GC-statics + frozen-object init and eager cctors.
    //
    // Our UEFI kernel has no C++ bootstrap, so none of this runs. The
    // consequence that blocks us: MT->TypeManager indirection reads a slot
    // that's zero-filled → MT->DispatchMap returns null → interface dispatch
    // can't resolve past the shellcode slow path.
    //
    // This file performs a minimal version of (1)-(2): finds the single
    // ReadyToRunHeader in our image via signature scan, allocates a tiny
    // TypeManager in the kernel heap with m_pDispatchMapTable pointing at
    // the InterfaceDispatchTable section, and writes its pointer into all
    // TypeManagerIndirection slots. (3) is skipped — we don't run cctors
    // (existing code already lives with that constraint).
    //
    // Required BEFORE any shared-generic interface dispatch resolves, but
    // since we only need the DispatchMap chain then, we lazily initialize
    // on the first call into the resolver.
    internal static unsafe class NativeAotModuleInit
    {
        private const uint ReadyToRunSignature = 0x00525452;  // 'RTR'
        private const ushort ReadyToRunMajorVersion = 8;

        // ReadyToRunSectionType values from ModuleHeaders.h.
        private const int SectionId_InterfaceDispatchTable = 203;
        private const int SectionId_TypeManagerIndirection = 204;

        // TypeManager struct layout (Runtime/TypeManager.h, release/7.0):
        //   HANDLE                m_osModule;                  // +0
        //   ReadyToRunHeader*     m_pHeader;                   // +8
        //   DispatchMap**         m_pDispatchMapTable;         // +16
        //   uint8_t*              m_pStaticsGCDataSection;     // +24
        //   uint8_t*              m_pThreadStaticsDataSection; // +32
        //   void**                m_pClasslibFunctions;        // +40
        //   uint32_t              m_nClasslibFunctions;        // +48
        // (We only need +16; rest stays zeroed.)
        private const uint TypeManagerStructSize = 56;

        // ReadyToRunHeader layout:
        //   uint32 Signature        // +0  ('RTR')
        //   uint16 MajorVersion     // +4  (== 8)
        //   uint16 MinorVersion     // +6
        //   uint32 Flags            // +8
        //   uint16 NumberOfSections // +12
        //   uint8  EntrySize        // +14 (== sizeof(ModuleInfoRow) = 24)
        //   uint8  EntryType        // +15
        //   // ModuleInfoRow rows[NumberOfSections] follow at +16
        //
        // ModuleInfoRow:
        //   int32 SectionId         // +0
        //   int32 Flags             // +4
        //   void* Start             // +8
        //   void* End               // +16
        private const int RtrSectionIdOffset = 0;
        private const int RtrFlagsOffset = 4;
        private const int RtrStartOffset = 8;
        private const int RtrEndOffset = 16;
        private const int RtrEntrySize = 24;

        // Scan radius around the anchor MT. Image is a few MB; 32 MB covers
        // any reasonable NativeAOT output plus slack either way.
        private const long ScanRadius = 32L * 1024 * 1024;
        private const long ScanStride = 4;  // RTR sig is 4-byte aligned

        private static bool s_initialized;
        private static byte* s_typeManager;
        private static byte* s_rtr;
        private static ushort s_majorVersion;
        private static ushort s_minorVersion;

        public static bool IsInitialized => s_initialized;
        public static byte* TypeManager => s_typeManager;
        public static byte* ReadyToRunHeader => s_rtr;
        public static ushort ReadyToRunMajor => s_majorVersion;
        public static ushort ReadyToRunMinor => s_minorVersion;

        // Locates ReadyToRunHeader near `anchorInRdata`, allocates a
        // TypeManager, fills its DispatchMapTable field, and writes the
        // TypeManager pointer into every TypeManagerIndirection slot.
        //
        // Returns true on success. Idempotent.
        public static bool TryInitialize(GcMethodTable* anchorInRdata)
        {
            if (s_initialized) return true;
            if (anchorInRdata == null) return false;

            byte* rtr = FindReadyToRunHeader((byte*)anchorInRdata);
            if (rtr == null)
            {
                Log.Write(LogLevel.Warn, "naot-init: ReadyToRunHeader not found");
                return false;
            }

            // Find the two sections we need.
            byte* dispatchMapTable = GetSection(rtr, SectionId_InterfaceDispatchTable, out int dmLen);
            byte* typeManagerIndir = GetSection(rtr, SectionId_TypeManagerIndirection, out int indirLen);

            if (typeManagerIndir == null)
            {
                Log.Write(LogLevel.Warn, "naot-init: TypeManagerIndirection section missing");
                return false;
            }

            // Allocate TypeManager and fill fields.
            byte* tm = (byte*)KernelHeap.Alloc(TypeManagerStructSize);
            if (tm == null)
            {
                Log.Write(LogLevel.Warn, "naot-init: TypeManager alloc failed");
                return false;
            }

            OS.Kernel.Util.Memory.Zero(tm, TypeManagerStructSize);
            *(byte**)(tm + 8) = rtr;                 // m_pHeader
            *(byte**)(tm + 16) = dispatchMapTable;   // m_pDispatchMapTable

            // Walk every TypeManagerSlot and publish the TM pointer.
            // Slot layout: TypeManagerHandle (8 bytes) + int ModuleIndex (4 bytes).
            // Section length is typically one slot per linked module; our
            // kernel is one module, so loop cap of 8 is plenty.
            int slotSize = 12;
            int slotCount = indirLen / slotSize;
            if (slotCount < 1) slotCount = 1;
            if (slotCount > 8) slotCount = 8;

            for (int i = 0; i < slotCount; i++)
            {
                byte* slot = typeManagerIndir + i * slotSize;
                *(byte**)slot = tm;
                *(int*)(slot + 8) = i;
            }

            s_typeManager = tm;
            s_rtr = rtr;
            s_majorVersion = *(ushort*)(rtr + 4);
            s_minorVersion = *(ushort*)(rtr + 6);
            s_initialized = true;

            Log.Begin(LogLevel.Info);
            Console.Write("naot-init: rtr=0x");
            Console.WriteHexRaw((ulong)rtr, 16);
            Console.Write(" tm=0x");
            Console.WriteHexRaw((ulong)tm, 16);
            Console.Write(" dmTable=0x");
            Console.WriteHexRaw((ulong)dispatchMapTable, 16);
            Console.Write(" indir=0x");
            Console.WriteHexRaw((ulong)typeManagerIndir, 16);
            Console.Write(" slots=");
            Console.WriteULongRaw((ulong)slotCount);
            Log.EndLine();

            return true;
        }

        // Scans outward from `anchor` for the ReadyToRunHeader signature,
        // with a round of validation. Returns null if not found within
        // ±ScanRadius bytes.
        private static byte* FindReadyToRunHeader(byte* anchor)
        {
            // Align anchor to 4 bytes before searching.
            byte* alignedAnchor = (byte*)((nint)anchor & ~(nint)3);

            // Alternate outward: anchor-4, anchor+4, anchor-8, anchor+8, …
            // This finds the nearest candidate regardless of direction.
            for (long offset = 0; offset <= ScanRadius; offset += ScanStride)
            {
                byte* candidate = alignedAnchor - offset;
                if (IsValidRtrHeader(candidate)) return candidate;

                if (offset == 0) continue;

                candidate = alignedAnchor + offset;
                if (IsValidRtrHeader(candidate)) return candidate;
            }

            return null;
        }

        private static bool IsValidRtrHeader(byte* p)
        {
            // Signature + MajorVersion gate. False matches for a single
            // uint32 are possible but the combo with a stable major == 8
            // makes them astronomically unlikely.
            if (*(uint*)p != ReadyToRunSignature) return false;
            if (*(ushort*)(p + 4) != ReadyToRunMajorVersion) return false;

            // Sanity: NumberOfSections > 0 and < 100, EntrySize == 24.
            ushort numSections = *(ushort*)(p + 12);
            byte entrySize = p[14];
            if (numSections == 0 || numSections > 100) return false;
            if (entrySize != RtrEntrySize) return false;

            return true;
        }

        // Linear search over the section table following the RTR header.
        private static byte* GetSection(byte* rtr, int sectionId, out int length)
        {
            length = 0;
            ushort numSections = *(ushort*)(rtr + 12);
            byte* rows = rtr + 16;

            for (int i = 0; i < numSections; i++)
            {
                byte* row = rows + i * RtrEntrySize;
                int id = *(int*)(row + RtrSectionIdOffset);
                if (id != sectionId) continue;

                int flags = *(int*)(row + RtrFlagsOffset);
                byte* start = *(byte**)(row + RtrStartOffset);
                byte* end = *(byte**)(row + RtrEndOffset);

                // HasEndPointer flag (0x1) — if set, length is End-Start.
                // Otherwise the section is a single pointer-sized slot.
                if ((flags & 0x1) != 0)
                    length = (int)(end - start);
                else
                    length = sizeof(nint);

                return start;
            }

            return null;
        }
    }
}
