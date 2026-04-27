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
        private static bool s_diagnosticPrinted;

        // Snapshot of section info so DumpMaterializedSummary can re-walk
        // the section after Materialize is done and dump every entry's
        // (final) MT + obj header. Set in Materialize, read on demand.
        private static byte* s_sectionStart;
        private static int s_sectionLength;

        public static bool IsInitialized => s_initialized;

        // Toggle: false = allocate in KernelHeap (safe, never swept, but
        // inner refs aren't traced by GC), true = allocate in GcHeap
        // (proper GC walk, but exposes any latent bug in mt[-1] / GCDesc
        // reading or non-array offset+8 interpretation).
        // Sage 2 path: flip to true once the diagnostic confirms the MT
        // shape and we have correctness audits in place.
        private const bool UseGcHeapForMaterialized = true;

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

            s_sectionStart = section;
            s_sectionLength = length;

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

                // One-shot diagnostic dump for the first uninitialized entry.
                // Sage 2 asked us to confirm: MT shape (ComponentSize/Flags/
                // BaseSize/RelatedType), the GCDesc region (mt-32..mt+24),
                // and the first 32 bytes of the resulting object. If
                // ComponentSize is non-zero or seriesCount looks insane,
                // that pinpoints the layout assumption to revisit.
                if (!s_diagnosticPrinted)
                {
                    s_diagnosticPrinted = true;
                    DumpEETypeShape(eetype, blockAddr);
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

                // First materialized object: dump first 32 bytes for the
                // diagnostic. This shows whether the static fields landed
                // at offset +8 as expected, and whether the MT pointer
                // we wrote at +0 matches what we computed from the tag.
                if (materialized == 0)
                    DumpObjectHeader(obj, eetype);

                // Replace the tagged pointer with the materialized object ref.
                *(nint*)blockPtr = (nint)obj;

                // Register block as GC root. ILC emits `__GetGCStaticBase_*`
                // helpers that read `*blockPtr`, so the GC must keep these
                // objects alive across collections; without root registration
                // the next mark/sweep pass would reclaim them.
                GcRoots.RegisterRawSlot((nint*)blockPtr);

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

        // Allocate a materialized GC-statics object.
        //
        // Two paths controlled by UseGcHeapForMaterialized:
        //
        //  - GcHeap.AllocateRaw (target): the canonical NativeAOT placement
        //    (real runtime puts these in PinnedObjectHeap which GC marks but
        //    never compacts). Inner refs are traced via GCDesc. Requires
        //    that the synthetic GCStaticEEType has a valid mt[-1] series
        //    count and series blob — sage 2 confirmed it's a normal
        //    CanonicalEEType with ComponentSize=0, so heap walks via
        //    ComputeSize stay on BaseSize.
        //
        //  - KernelHeap.Alloc (fallback): block allocator, never swept;
        //    inner refs aren't traced. Used as a workaround if GcHeap
        //    placement exposes a latent layout bug. Materialized object
        //    holds external refs only via primitive frozen data.
        private static void* AllocateObject(GcMethodTable* mt)
        {
            uint size = mt->BaseSize;
            void* obj;

            if (UseGcHeapForMaterialized)
            {
                obj = GcHeap.AllocateRaw(size);
                if (obj == null) return null;
                // GcHeap.AllocateRaw zero-fills.
                *(GcMethodTable**)obj = mt;
            }
            else
            {
                obj = KernelHeap.Alloc(size);
                if (obj == null) return null;
                byte* p = (byte*)obj;
                for (uint i = 0; i < size; i++) p[i] = 0;
                *(GcMethodTable**)obj = mt;
            }

            return obj;
        }

        // Diagnostic: dump MT layout fields and the GCDesc region in the
        // negative space before MT (mt-32..mt+24). Sage 2 noted this is
        // where mt[-1] (series count) and mt[-2..-(n+1)] (GCDescSeries)
        // live for canonical types. If series count looks bogus, the
        // mark phase will walk garbage.
        private static void DumpEETypeShape(GcMethodTable* eetype, nint taggedBlockAddr)
        {
            Log.Begin(LogLevel.Info);
            Console.Write("gcstatics-diag: blockAddrTag=0x");
            Console.WriteHexRaw((ulong)taggedBlockAddr, 16);
            Console.Write(" eetype=0x");
            Console.WriteHexRaw((ulong)eetype, 16);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("  componentSize=");
            Console.WriteUIntRaw(eetype->ComponentSize);
            Console.Write(" flags=0x");
            Console.WriteHexRaw(eetype->Flags, 4);
            Console.Write(" baseSize=");
            Console.WriteUIntRaw(eetype->BaseSize);
            Console.Write(" hasPointers=");
            Console.Write(eetype->HasPointers ? "Y" : "N");
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("  related=0x");
            Console.WriteHexRaw((ulong)eetype->RelatedType, 16);
            Console.Write(" elementType=0x");
            Console.WriteHexRaw((ulong)(uint)eetype->ElementType, 2);
            Console.Write(" kind=0x");
            Console.WriteHexRaw((ulong)(uint)eetype->Kind, 2);
            Log.EndLine();

            // Dump the four 8-byte qwords before MT (mt[-4]..mt[-1]) +
            // first three qwords at MT (mt[0..2]). For canonical types
            // with HasPointers, mt[-1] is series count; mt[-2] downward
            // are GcDescSeries entries.
            nint* mt = (nint*)eetype;
            Log.Begin(LogLevel.Info);
            Console.Write("  mt[-4..-1]:");
            for (int i = -4; i <= -1; i++)
            {
                Console.Write(" 0x");
                Console.WriteHexRaw((ulong)(nuint)mt[i], 16);
            }
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("  mt[0..2]:  ");
            for (int i = 0; i <= 2; i++)
            {
                Console.Write(" 0x");
                Console.WriteHexRaw((ulong)(nuint)mt[i], 16);
            }
            Log.EndLine();
        }

        // Diagnostic: dump first four qwords of the materialized object.
        // We expect: [+0] = MT pointer (must equal eetype param),
        // [+8..+24] = static field data (preinit blob copy or zeros).
        private static void DumpObjectHeader(void* obj, GcMethodTable* expectedMt)
        {
            nint* p = (nint*)obj;
            Log.Begin(LogLevel.Info);
            Console.Write("  obj=0x");
            Console.WriteHexRaw((ulong)obj, 16);
            Console.Write(" expectedMt=0x");
            Console.WriteHexRaw((ulong)expectedMt, 16);
            Log.EndLine();
            Log.Begin(LogLevel.Info);
            Console.Write("  obj[0..3]: ");
            for (int i = 0; i <= 3; i++)
            {
                Console.Write(" 0x");
                Console.WriteHexRaw((ulong)p[i], 16);
            }
            Log.EndLine();
        }

        // After materialization, walk every entry and dump its current MT
        // + obj header. Cheap (typically a handful of entries) and gives
        // us a visible snapshot of all materialized objects right before
        // a test runs — so even if the test crashes, the latest dump
        // stays in the scrollback.
        public static void DumpMaterializedSummary()
        {
            if (s_sectionStart == null || s_sectionLength == 0)
            {
                Log.Write(LogLevel.Info, "gcstatics-summary: no section");
                return;
            }

            int entryCount = s_sectionLength / sizeof(nint);
            nint* entries = (nint*)s_sectionStart;

            Log.Begin(LogLevel.Info);
            Console.Write("gcstatics-summary: entries=");
            Console.WriteUIntRaw((uint)entryCount);
            Log.EndLine();

            for (int i = 0; i < entryCount; i++)
            {
                nint blockPtr = entries[i];
                if (blockPtr == 0) continue;
                nint blockVal = *(nint*)blockPtr;

                Log.Begin(LogLevel.Info);
                Console.Write("  e[");
                Console.WriteUIntRaw((uint)i);
                Console.Write("] block=0x");
                Console.WriteHexRaw((ulong)(nuint)blockPtr, 16);
                Console.Write(" *block=0x");
                Console.WriteHexRaw((ulong)(nuint)blockVal, 16);
                Log.EndLine();

                if ((blockVal & Uninitialized) != 0)
                {
                    Log.Write(LogLevel.Info, "    (still uninitialized)");
                    continue;
                }

                // *block is the obj pointer.
                GcObject* obj = (GcObject*)blockVal;
                if (obj == null)
                {
                    Log.Write(LogLevel.Info, "    (null obj)");
                    continue;
                }

                GcMethodTable* mt = obj->MethodTable;
                Log.Begin(LogLevel.Info);
                Console.Write("    mt=0x");
                Console.WriteHexRaw((ulong)(nuint)mt, 16);
                Console.Write(" cs=");
                Console.WriteUIntRaw(mt->ComponentSize);
                Console.Write(" fl=0x");
                Console.WriteHexRaw(mt->Flags, 4);
                Console.Write(" bs=");
                Console.WriteUIntRaw(mt->BaseSize);
                Console.Write(" hp=");
                Console.Write(mt->HasPointers ? "Y" : "N");
                Log.EndLine();

                nint* mtRaw = (nint*)mt;
                Log.Begin(LogLevel.Info);
                Console.Write("    mt[-2..-1]:");
                for (int k = -2; k <= -1; k++)
                {
                    Console.Write(" 0x");
                    Console.WriteHexRaw((ulong)(nuint)mtRaw[k], 16);
                }
                Log.EndLine();

                nint* op = (nint*)obj;
                Log.Begin(LogLevel.Info);
                Console.Write("    obj[0..3]:");
                for (int k = 0; k <= 3; k++)
                {
                    Console.Write(" 0x");
                    Console.WriteHexRaw((ulong)(nuint)op[k], 16);
                }
                Log.EndLine();
            }
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
