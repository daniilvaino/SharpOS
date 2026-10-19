// Ported from dotnet/runtime src/coreclr/nativeaot/System.Private.CoreLib/src/
// Internal/Runtime/ThreadStatics.cs (the uninlined path).
//
// Cuts:
//   - Inlined thread statics (t_inlinedThreadStaticBase, GetInlinedThreadStaticBaseSlow):
//     they live in the image's native TLS (.tls, _tls_index, gs:[0x58]), and an
//     app image has none of that. Both tiers build with ILC --noinlinetls, so
//     every thread static goes through GetThreadStaticBaseForType.
//   - Dynamic types (TypeLoaderCallbacks.GetThreadStaticGCDescForDynamicType):
//     types are not built at run time here.
//
// Where the storage hangs (RuntimeImports.RhGetThreadStaticStorage in stock): a
// word of the running thread's own, whose address a stub installed by the tier
// returns — the kernel's thread area (OS/src/Kernel/Threading/ThreadArea.cs),
// reached through gs. The kernel and an app keep separate words: each tier's
// storage is in its own heap, and each collector roots its own word.

using System;
using System.Runtime.CompilerServices;
using Internal.Runtime.CompilerHelpers;
using SharpOS.Std.NoRuntime;

namespace Internal.Runtime.CompilerHelpers
{
    // ILC names it in the helper signatures it looks up (the class constructor
    // check returning a thread-static base), so the namespace is BCL's.
    internal unsafe struct TypeManagerSlot
    {
        public IntPtr TypeManager;
        public int ModuleIndex;
    }
}

namespace Internal.Runtime
{
    internal static unsafe class ThreadStatics
    {
        private const int ThreadStaticRegion = 202;   // ReadyToRunSectionType

        // Returns the address of the running thread's words; the tier's word
        // is s_word past it. Null until the tier installs it.
        private static delegate*<nint*> s_words;
        private static int s_word;

        /// <summary>Installed by the tier once every thread has its words.</summary>
        public static void Install(delegate*<nint*> words, int word)
        {
            s_word = word;
            s_words = words;
        }

        public static bool IsAvailable => s_words != null;

        private static ref object[][] Storage()
        {
            if (s_words == null)
                throw new InvalidOperationException("[ThreadStatic] before this tier's threads have their words");
            return ref Unsafe.AsRef<object[][]>(s_words() + s_word);
        }

        /// <summary>
        /// This method is called from a ReadyToRun helper to get base address of thread
        /// static storage for the given type.
        /// </summary>
        internal static object GetThreadStaticBaseForType(TypeManagerSlot* pModuleData, int typeTlsIndex)
        {
            int moduleIndex = pModuleData->ModuleIndex;

            object[][] perThreadStorage = Storage();
            if (perThreadStorage != null && perThreadStorage.Length > moduleIndex)
            {
                object[] perModuleStorage = perThreadStorage[moduleIndex];
                if (perModuleStorage != null && perModuleStorage.Length > typeTlsIndex)
                {
                    object threadStaticBase = perModuleStorage[typeTlsIndex];
                    if (threadStaticBase != null)
                    {
                        return threadStaticBase;
                    }
                }
            }

            return GetUninlinedThreadStaticBaseForTypeSlow(pModuleData, typeTlsIndex);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static object GetUninlinedThreadStaticBaseForTypeSlow(TypeManagerSlot* pModuleData, int typeTlsIndex)
        {
            int moduleIndex = pModuleData->ModuleIndex;

            // Get the array that holds thread statics for the current thread, if none present
            // allocate a new one big enough to hold the current module data
            ref object[][] threadStorage = ref Storage();
            if (threadStorage == null)
            {
                threadStorage = new object[moduleIndex + 1][];
            }
            else if (moduleIndex >= threadStorage.Length)
            {
                object[][] grown = new object[moduleIndex + 1][];
                Array.Copy(threadStorage, grown, threadStorage.Length);
                threadStorage = grown;
            }

            // Get the array that holds thread static memory blocks for each type in the given module
            ref object[] moduleStorage = ref threadStorage[moduleIndex];
            if (moduleStorage == null)
            {
                moduleStorage = new object[typeTlsIndex + 1];
            }
            else if (typeTlsIndex >= moduleStorage.Length)
            {
                // typeTlsIndex could have a big range, we do not want to reallocate every time we see +1 index
                // so we double up from previous size to guarantee a worst case linear complexity
                int newSize = Math.Max(typeTlsIndex + 1, moduleStorage.Length * 2);
                object[] grown = new object[newSize];
                Array.Copy(moduleStorage, grown, moduleStorage.Length);
                moduleStorage = grown;
            }

            // Allocate an object that will represent a memory block for all thread static fields of the type
            object threadStaticBase = AllocateThreadStaticStorageForType(pModuleData->TypeManager, typeTlsIndex);

            moduleStorage[typeTlsIndex] = threadStaticBase;
            return threadStaticBase;
        }

        /// <summary>
        /// This method allocates an object that represents a memory block for all thread static fields of the type
        /// that corresponds to the specified TLS index.
        /// </summary>
        private static object AllocateThreadStaticStorageForType(IntPtr typeManager, int typeTlsIndex)
        {
            // TypeManager: m_pHeader (the ReadyToRun header) at +8.
            byte* rtr = *(byte**)((byte*)typeManager + 8);
            byte* region = FindSection(rtr, ThreadStaticRegion, out int length);

            // Absolute pointers (relocated with the image) at ReadyToRun major 9
            // too, unlike GCStaticRegion's self-relative entries.
            if (region == null || typeTlsIndex >= length / IntPtr.Size)
                throw new InvalidOperationException("thread static of a type without a ThreadStaticRegion entry");
            GcMethodTable* gcDesc = ((GcMethodTable**)region)[typeTlsIndex];

            void* block = GcHeap.AllocateObject(gcDesc->BaseSize, gcDesc);
            if (block == null)
                throw new OutOfMemoryException();
            return Unsafe.AsRef<object>(&block);
        }

        // A section of the ReadyToRun header: rows of {int id, int flags,
        // start, end} after a 16-byte head. The same reader as the app's
        // GcStaticsInit.FindSection, which the kernel does not compile.
        private static byte* FindSection(byte* rtr, int sectionId, out int length)
        {
            length = 0;
            ushort count = *(ushort*)(rtr + 12);
            byte entrySize = rtr[14];
            for (int i = 0; i < count; i++)
            {
                byte* row = rtr + 16 + i * entrySize;
                if (*(int*)row != sectionId) continue;
                byte* start = *(byte**)(row + 8);
                byte* end = *(byte**)(row + 16);
                length = (*(int*)(row + 4) & 0x1) != 0 ? (int)(end - start) : IntPtr.Size;
                return start;
            }
            return null;
        }
    }
}
