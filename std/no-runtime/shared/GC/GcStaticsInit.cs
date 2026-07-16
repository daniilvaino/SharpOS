// GC statics materialization for freestanding apps (step142).
//
// Stock NativeAOT startup (StartupCodeHelpers.InitializeStatics) walks the
// ReadyToRunSectionType.GCStaticRegion (201) section: every uninitialized
// entry is a tagged EEType pointer; materialization allocates the statics
// block object, copies the preinit blob if present, and replaces the tag
// with the object reference so `__GetGCStaticBase_*` helpers return a real
// object. Without it any `static readonly T x = ...` (ILC TypePreinit)
// #GPs on the sentinel.
//
// The kernel runs its own diagnostic-rich copy at boot
// (OS/src/Kernel/Memory/GcStaticsMaterializer.cs, step 40-41); this is the
// same walk cut down for the app tier: the app finds the ReadyToRun header
// in its OWN image (base 0x100000000 fixed — FreestandingPe.props /BASE +
// /FIXED, honored by the kernel PeLoader) and allocates from its own
// GcHeap. Called from AppRuntime.Initialize right after GcHeap.Init.
// Interim duplication rule per donext.md UNWIND-style debt: a fix to the
// walk here must answer whether the kernel copy needs it too.
//
// Entry/tag formats are RTR-major-dependent (verified in the kernel copy):
//   major 8: 8-byte absolute pointers; tag qword at block+0, preinit
//            pointer at block+8.
//   major 9: 4-byte self-relative entries; block+0 = eeRel (tag in low
//            bits, self-relative to block), block+4 = dataRel (self-
//            relative to block+4).

namespace SharpOS.Std.NoRuntime
{
    // TypeManager backing store — a fixed .bss block. NOT GcHeap (a raw
    // block without a MethodTable header would trip the heap walker) and
    // the app tier has no KernelHeap. Same pattern as GcMarkStackStorage.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Size = AppTypeManagerInit.TypeManagerStructSize)]
    internal struct AppTypeManagerStorage { }

    // App-image TypeManager wiring (step142). The kernel resolver handles
    // foreign (app) MethodTables purely for DIRECT interface-map matches
    // (step139), but the shared-generic/variant fallback dereferences
    // MT -> TypeManagerIndirection slot -> TypeManager -> DispatchMapTable —
    // and nobody fills the app image's indirection slots (the kernel's
    // NativeAotModuleInit does it only for the kernel image at boot).
    // Symptom: `iface-resolve fail (no match in inheritance chain)` with
    // `tm=0x0 dmTable=0x0` on the first generic-instantiation dispatch
    // (ManagedDoom map load, done/step142). Mirrors NativeAotModuleInit:
    // allocate a TypeManager (only m_pHeader/+8 and m_pDispatchMapTable/+16
    // matter), publish it into every TypeManagerSlot.
    internal static unsafe class AppTypeManagerInit
    {
        internal const int TypeManagerStructSize = 56;

        private const int SectionId_InterfaceDispatchTable = 203;
        private const int SectionId_TypeManagerIndirection = 204;

        private static AppTypeManagerStorage s_typeManager;
        private static bool s_initialized;

        public static bool Initialize()
        {
            if (s_initialized) return true;
            s_initialized = true;

            byte* rtr = GcStaticsInit.FindReadyToRunHeader();
            if (rtr == null) return false;

            byte* dispatchMapTable = GcStaticsInit.FindSection(rtr, SectionId_InterfaceDispatchTable, out _);
            byte* typeManagerIndir = GcStaticsInit.FindSection(rtr, SectionId_TypeManagerIndirection, out int indirLen);
            if (typeManagerIndir == null) return false;

            fixed (AppTypeManagerStorage* store = &s_typeManager)
            {
                byte* tm = (byte*)store;
                for (int i = 0; i < TypeManagerStructSize; i++) tm[i] = 0;
                *(byte**)(tm + 8) = rtr;                // m_pHeader
                *(byte**)(tm + 16) = dispatchMapTable;  // m_pDispatchMapTable

                // TypeManagerSlot = { TypeManagerHandle (8), int ModuleIndex (4) }.
                const int slotSize = 12;
                int slotCount = indirLen / slotSize;
                if (slotCount < 1) slotCount = 1;
                if (slotCount > 8) slotCount = 8;

                for (int i = 0; i < slotCount; i++)
                {
                    byte* slot = typeManagerIndir + i * slotSize;
                    *(byte**)slot = tm;
                    *(int*)(slot + 8) = i;
                }
            }

            return true;
        }
    }

    internal static unsafe class GcStaticsInit
    {
        private const uint ReadyToRunSignature = 0x00525452; // 'RTR'
        private const int RtrEntrySize = 24;
        private const int SectionId_GCStaticRegion = 201;

        private const nint Uninitialized = 0x1;
        private const nint HasPreInitializedData = 0x2;
        private const nint Mask = 0x3;

        // Fixed app image base — contract with FreestandingPe.props
        // (/BASE:0x100000000 /FIXED) and the kernel PeLoader (honors
        // ImageBase). 4 GiB: above identity-mapped RAM, below the app stack.
        private const ulong ImageBase = 0x100000000;

        private static bool s_initialized;

        public static int MaterializedCount;
        public static int FailedCount;

        public static bool Materialize()
        {
            if (s_initialized) return true;
            s_initialized = true;

            byte* rtr = FindReadyToRunHeader();
            if (rtr == null)
            {
                FailedCount = -1; // header not found — diagnosable by caller
                return false;
            }

            ushort major = *(ushort*)(rtr + 4);
            byte* section = FindSection(rtr, SectionId_GCStaticRegion, out int length);
            if (section == null)
                return true; // no GC statics in this module — nothing to do

            bool relPtr = major >= 9;
            int entryStride = relPtr ? 4 : sizeof(nint);
            int entryCount = length / entryStride;

            for (int i = 0; i < entryCount; i++)
            {
                nint blockPtr;
                if (relPtr)
                {
                    int* pRel = (int*)(section + i * 4);
                    int rel = *pRel;
                    if (rel == 0) continue;
                    blockPtr = (nint)((byte*)pRel + rel);
                }
                else
                {
                    blockPtr = ((nint*)section)[i];
                    if (blockPtr == 0) continue;
                }

                GcMethodTable* eetype;
                bool hasPreInit;
                byte* preInitBlob = null;

                if (relPtr)
                {
                    int eeRel = *(int*)blockPtr;
                    if ((eeRel & (int)Uninitialized) == 0) continue;
                    eetype = (GcMethodTable*)((byte*)blockPtr + (eeRel & ~(int)Mask));
                    hasPreInit = (eeRel & (int)HasPreInitializedData) != 0;
                    if (hasPreInit)
                    {
                        int dataRel = *(int*)((byte*)blockPtr + 4);
                        preInitBlob = (byte*)blockPtr + 4 + dataRel;
                    }
                }
                else
                {
                    nint blockAddr = *(nint*)blockPtr;
                    if ((blockAddr & Uninitialized) == 0) continue;
                    eetype = (GcMethodTable*)(blockAddr & ~Mask);
                    hasPreInit = (blockAddr & HasPreInitializedData) != 0;
                    if (hasPreInit) preInitBlob = *(byte**)(blockPtr + sizeof(nint));
                }

                if (eetype == null)
                {
                    FailedCount++;
                    continue;
                }

                void* obj = GcHeap.AllocateRaw(eetype->BaseSize);
                if (obj == null)
                {
                    FailedCount++;
                    continue;
                }

                *(GcMethodTable**)obj = eetype;

                if (hasPreInit && preInitBlob != null)
                {
                    uint rawDataSize = eetype->BaseSize - 8;
                    byte* objData = (byte*)obj + 8;
                    for (uint b = 0; b < rawDataSize; b++)
                        objData[b] = preInitBlob[b];
                }

                // Replace tag with the object ref; the block slot is a GC
                // root (ILC's __GetGCStaticBase_* reads it forever after).
                *(nint*)blockPtr = (nint)obj;
                GcRoots.RegisterRawSlot((nint*)blockPtr);

                MaterializedCount++;
            }

            return FailedCount == 0;
        }

        // Forward scan of the own image for the RTR header — bounded by
        // SizeOfImage from the PE headers at the fixed base, so it never
        // touches unmapped pages. (The kernel scans outward from an anchor
        // MT instead; a forward scan is enough here because the image base
        // is a link-time constant.)
        internal static byte* FindReadyToRunHeader()
        {
            byte* imageBase = (byte*)ImageBase;
            if (*(ushort*)imageBase != 0x5A4D) return null; // 'MZ'

            int lfanew = *(int*)(imageBase + 0x3C);
            byte* nt = imageBase + lfanew;
            if (*(uint*)nt != 0x00004550) return null; // 'PE\0\0'

            // PE32+: SizeOfImage at OptionalHeader+56.
            byte* opt = nt + 4 + 20;
            uint sizeOfImage = *(uint*)(opt + 56);

            byte* end = imageBase + sizeOfImage - RtrEntrySize;
            for (byte* p = imageBase; p < end; p += 4)
            {
                if (*(uint*)p != ReadyToRunSignature) continue;

                ushort majorCandidate = *(ushort*)(p + 4);
                if (majorCandidate < 8 || majorCandidate > 9) continue;

                ushort numSections = *(ushort*)(p + 12);
                byte entrySize = p[14];
                if (numSections == 0 || numSections > 100) continue;
                if (entrySize != RtrEntrySize) continue;

                return p;
            }

            return null;
        }

        internal static byte* FindSection(byte* rtr, int sectionId, out int length)
        {
            length = 0;
            ushort numSections = *(ushort*)(rtr + 12);
            byte* rows = rtr + 16;

            for (int i = 0; i < numSections; i++)
            {
                byte* row = rows + i * RtrEntrySize;
                int id = *(int*)row;
                if (id != sectionId) continue;

                int flags = *(int*)(row + 4);
                byte* start = *(byte**)(row + 8);
                byte* end = *(byte**)(row + 16);

                // HasEndPointer flag (0x1): length = End-Start, else one slot.
                length = (flags & 0x1) != 0 ? (int)(end - start) : sizeof(nint);
                return start;
            }

            return null;
        }
    }
}
