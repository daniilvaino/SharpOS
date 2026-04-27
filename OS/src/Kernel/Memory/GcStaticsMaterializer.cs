using OS.Hal;
using SharpOS.Std.NoRuntime;

namespace OS.Kernel.Memory
{
    // GC statics materialization at boot.
    //
    // Stock NativeAOT runtime (StartupCodeHelpers.InitializeModules →
    // InitializeGlobalTablesForModule → InitializeStatics) walks the
    // ReadyToRunSectionType.GCStaticRegion (201) section and for each entry:
    //
    //   1. block = *entry           — pointer to a 1- or 2-qword block
    //   2. blockAddr = *block        — first qword: tagged value
    //   3. If (blockAddr & Uninitialized):
    //        eetype = blockAddr & ~Mask
    //        obj    = RhAllocateNewObject(eetype, PINNED_OBJECT_HEAP)
    //        if (blockAddr & HasPreInitializedData):
    //           preInitBlob = *(block + 1)   — second qword
    //           bulk-copy preInitBlob → obj.GetRawData() for raw-data-size bytes
    //        *block = obj   — replace tagged tag with materialized object ref
    //
    // After this runs, every `__GetGCStaticBase_*` helper returns a real
    // object reference, and `static readonly T x = new T()` works for any
    // type ILC chose to preinitialize (which is most types in practice).
    //
    // GCStaticRegionConstants source (snapshot doesn't include tools/, so
    // values inferred from the algorithm — verified empirically by walker
    // before materialization runs).
    internal static unsafe class GcStaticsMaterializer
    {
        // Section ID per ModuleHeaders.h.
        private const int SectionId_GCStaticRegion = 201;

        // Tagged-pointer flags. Pointers to GCStaticEEType are 8-byte
        // aligned, so low 3 bits are available for flags.
        private const nint Uninitialized          = 0x1;
        private const nint HasPreInitializedData  = 0x2;
        private const nint Mask                   = 0x3;

        private static bool s_initialized;

        public static bool IsInitialized => s_initialized;

        // Walk and dump descriptor cells without modifying anything.
        // Confirms section layout, flag values, address validity before
        // we commit to materialization.
        public static void Diagnose()
        {
            byte* tm = NativeAotModuleInit.TypeManager;
            if (tm == null)
            {
                Log.Write(LogLevel.Warn, "gcstatics: TypeManager not initialized");
                return;
            }

            // ReadyToRunHeader pointer is at TypeManager + 8 (m_pHeader).
            byte* rtr = *(byte**)(tm + 8);
            if (rtr == null)
            {
                Log.Write(LogLevel.Warn, "gcstatics: rtr pointer null");
                return;
            }

            byte* section = FindSection(rtr, SectionId_GCStaticRegion, out int length);
            if (section == null)
            {
                Log.Write(LogLevel.Info, "gcstatics: GCStaticRegion section absent");
                return;
            }

            int entryCount = length / sizeof(nint);

            Log.Begin(LogLevel.Info);
            Console.Write("gcstatics: section=0x");
            Console.WriteHexRaw((ulong)section, 16);
            Console.Write(" length=");
            Console.WriteUIntRaw((uint)length);
            Console.Write(" entries=");
            Console.WriteUIntRaw((uint)entryCount);
            Log.EndLine();

            // Dump first few entries with their decoded shape.
            int dumpLimit = entryCount < 8 ? entryCount : 8;
            nint* entries = (nint*)section;
            for (int i = 0; i < dumpLimit; i++)
            {
                nint blockPtr = entries[i];
                Log.Begin(LogLevel.Info);
                Console.Write("  entry[");
                Console.WriteUIntRaw((uint)i);
                Console.Write("] blockPtr=0x");
                Console.WriteHexRaw((ulong)blockPtr, 16);
                if (blockPtr != 0)
                {
                    nint blockAddr = *(nint*)blockPtr;
                    Console.Write(" *block=0x");
                    Console.WriteHexRaw((ulong)blockAddr, 16);
                    Console.Write(" flags=");
                    if ((blockAddr & Uninitialized) != 0) Console.Write("U");
                    if ((blockAddr & HasPreInitializedData) != 0) Console.Write("P");
                    if ((blockAddr & Mask) == 0) Console.Write("-");
                    if ((blockAddr & Uninitialized) != 0)
                    {
                        nint eetype = blockAddr & ~Mask;
                        Console.Write(" eetype=0x");
                        Console.WriteHexRaw((ulong)eetype, 16);
                    }
                }
                Log.EndLine();
            }
        }

        // Materialize all uninitialized GC static blocks. After this runs,
        // every `__GetGCStaticBase_*` helper returns a real GC-allocated
        // object reference, and `static readonly T x = new T()` works.
        public static bool Materialize()
        {
            if (s_initialized) return true;

            byte* tm = NativeAotModuleInit.TypeManager;
            if (tm == null) return false;

            byte* rtr = *(byte**)(tm + 8);
            if (rtr == null) return false;

            byte* section = FindSection(rtr, SectionId_GCStaticRegion, out int length);
            if (section == null)
            {
                // No GC statics in this module — nothing to do.
                s_initialized = true;
                return true;
            }

            int entryCount = length / sizeof(nint);
            nint* entries = (nint*)section;
            int materialized = 0;
            int alreadyInit = 0;
            int failed = 0;

            for (int i = 0; i < entryCount; i++)
            {
                nint blockPtr = entries[i];
                if (blockPtr == 0) continue;

                nint blockAddr = *(nint*)blockPtr;
                if ((blockAddr & Uninitialized) == 0)
                {
                    // Already materialized (shared block initialized by
                    // an earlier entry via deduplication).
                    alreadyInit++;
                    continue;
                }

                // Decode tagged pointer.
                GcMethodTable* eetype = (GcMethodTable*)(blockAddr & ~Mask);
                if (eetype == null)
                {
                    failed++;
                    continue;
                }

                // Allocate fresh object of this type via our standard GC path.
                void* obj = AllocateObject(eetype);
                if (obj == null)
                {
                    failed++;
                    continue;
                }

                // If preinit data exists, bulk-copy it into the object's
                // raw data area (everything after the 8-byte MT header).
                if ((blockAddr & HasPreInitializedData) != 0)
                {
                    byte* preInitBlob = *(byte**)(blockPtr + sizeof(nint));
                    if (preInitBlob != null)
                    {
                        uint rawDataSize = eetype->BaseSize - 8;
                        byte* objData = (byte*)obj + 8;
                        for (uint b = 0; b < rawDataSize; b++)
                            objData[b] = preInitBlob[b];
                    }
                }

                // Replace the tagged pointer with the materialized object ref.
                *(nint*)blockPtr = (nint)obj;
                materialized++;
            }

            Log.Begin(LogLevel.Info);
            Console.Write("gcstatics: materialized=");
            Console.WriteUIntRaw((uint)materialized);
            Console.Write(" already=");
            Console.WriteUIntRaw((uint)alreadyInit);
            Console.Write(" failed=");
            Console.WriteUIntRaw((uint)failed);
            Log.EndLine();

            s_initialized = true;
            return failed == 0;
        }

        // Allocate a GC object with the given MethodTable, mirroring what
        // RhpNewFast does. Pinned-object semantics: our GC is single-region
        // mark-sweep without compaction, so all objects are effectively
        // pinned. No special PINNED_OBJECT_HEAP needed.
        private static void* AllocateObject(GcMethodTable* mt)
        {
            uint size = mt->BaseSize;
            void* obj = GcHeap.AllocateRaw(size);
            if (obj == null) return null;
            *(GcMethodTable**)obj = mt;
            return obj;
        }

        private static byte* FindSection(byte* rtr, int sectionId, out int length)
        {
            length = 0;
            ushort numSections = *(ushort*)(rtr + 12);
            byte* row = rtr + 16; // first ModuleInfoRow

            for (int i = 0; i < numSections; i++)
            {
                int id = *(int*)row;
                int flags = *(int*)(row + 4);
                byte* start = *(byte**)(row + 8);
                byte* end = *(byte**)(row + 16);
                if (id == sectionId)
                {
                    length = (int)(end - start);
                    return start;
                }
                row += 24;
            }
            return null;
        }
    }
}
