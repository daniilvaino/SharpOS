using System;
using System.Runtime;
using System.Runtime.InteropServices;
using OS.Hal;
using OS.Kernel;
using OS.Kernel.Memory;
using OS.Kernel.Paging;

namespace OS.PAL.SharpOSHost
{
    // CRT heap forwarders — fork-side `malloc`/`free`/`realloc` in
    // winapi_shim.cpp dispatch to these (per D9: CoreCLR memory routed
    // through SharpOSHost). Real allocation goes via NativeArena —
    // page-backed bump arena, dedicated to non-managed native blobs
    // (memory-ownership.md §3 / §7.1 / M1).
    //
    // `free` is no-op (NativeArena is bump-only, lifetime = forever).
    // `realloc` allocs new + copies; old block leaks the same way it
    // did when this lived in GcHeap. Matches the existing contract
    // CoreCLR was already relying on. When/if cleanup matters, swap
    // arena for a freelist/slab without touching callers.
    internal static unsafe class CrtHeapStubs
    {
        private static ulong NowTicks()
            => OS.Hal.Timer.Hpet.IsInitialized ? OS.Hal.Timer.Hpet.ReadCounter() : 0;

        private static ulong ElapsedMs(ulong start, ulong end)
        {
            if (start == 0 || end < start || OS.Hal.Timer.Hpet.FrequencyHz == 0)
                return 0;
            return (end - start) * 1000UL / OS.Hal.Timer.Hpet.FrequencyHz;
        }

        [RuntimeExport("SharpOSHost_HeapAlloc")]
        public static void* HeapAlloc(ulong size)
        {
            // No per-call trace here — caller (winapi_shim's malloc wrapper)
            // emits one [crt] line including the returned pointer.
            if (size == 0)
            {
                // C runtime semantics: malloc(0) should return a unique
                // non-null pointer (caller may free it). Allocate 1 byte.
                return NativeArena.Allocate(1);
            }
            void* p = NativeArena.Allocate(size);
            if (p == null)
            {
                // step113 Release bring-up: diagnose bad_alloc source.
                // Print the failing request size + arena totals (ungated).
                // Bogus-huge size => codegen/underflow; reasonable size =>
                // genuine PhysicalMemory exhaustion.
                OS.Hal.Console.Write("[HeapAlloc NULL] size=0x");
                OS.Hal.Console.WriteHex(size);
                OS.Hal.Console.Write(" arenaTotal=0x");
                OS.Hal.Console.WriteHex(NativeArena.TotalBytes);
                OS.Hal.Console.WriteLine("");
            }
            return p;
        }

        [RuntimeExport("SharpOSHost_HeapFree")]
        public static void HeapFree(void* ptr)
        {
            // No-op — managed GC handles reclaim. Block becomes unreachable
            // once C++ caller drops the pointer.
        }

        [RuntimeExport("SharpOSHost_HeapRealloc")]
        public static void* HeapRealloc(void* old, ulong size)
        {
            if (size == 0) return null;
            void* fresh = NativeArena.Allocate(size);
            if (fresh != null && old != null)
            {
                // No header — no way to know original size. Copy `size`
                // bytes; for grow-pattern this over-reads source, but
                // GC blocks are continuous mapped memory so this is
                // bounded. Real impl needs size header per alloc.
                byte* dst = (byte*)fresh;
                byte* src = (byte*)old;
                for (ulong i = 0; i < size; i++) dst[i] = src[i];
            }
            return fresh;
        }

        // SharpOSHost_CreateGuid — fork's CoCreateGuid forwarder. Writes
        // 16 bytes of v4 GUID into `out`. Real generation lives in BCL
        // System.Guid.NewGuid() per CLAUDE.md invariant 1.
        [RuntimeExport("SharpOSHost_CreateGuid")]
        public static void CreateGuid(void* outGuid)
        {
            if (outGuid == null) return;
            Guid g = Guid.NewGuid();
            byte* src = (byte*)&g;
            byte* dst = (byte*)outGuid;
            for (int i = 0; i < 16; i++) dst[i] = src[i];
        }

        // SharpOSHost_FillRandom — fork's BCryptGenRandom forwarder. Fills
        // `n` bytes of non-deterministic data (hash-flood-resistant seed for
        // System.HashCode / randomized string hashing / System.Text.Json;
        // NOT cryptographic). Entropy reuses the same source as CreateGuid
        // (BCL Guid.NewGuid → 16 entropy bytes/draw) — no second RNG, logic
        // stays in C# per CLAUDE.md invariant 1.
        [RuntimeExport("SharpOSHost_FillRandom")]
        public static void FillRandom(void* buf, int n)
        {
            if (buf == null || n <= 0) return;
            byte* dst = (byte*)buf;
            int off = 0;
            while (off < n)
            {
                Guid g = Guid.NewGuid();
                byte* src = (byte*)&g;
                int chunk = n - off; if (chunk > 16) chunk = 16;
                for (int i = 0; i < chunk; i++) dst[off + i] = src[i];
                off += chunk;
            }
        }

        // SharpOSHost_GetFullPathName — fork's GetFullPathNameW forwarder.
        // Marshals wchar input → managed string, calls BCL System.IO.Path.
        // GetFullPath (pure string normalization, no FS probe), copies result
        // back. Returns char count written (excluding null), or required size
        // when outBuf too small. Per CLAUDE.md invariant 1, path logic in C#.
        [RuntimeExport("SharpOSHost_GetFullPathName")]
        public static uint GetFullPathName(char* lpFileName, uint nBufferLength, char* lpBuffer)
        {
            if (lpFileName == null) return 0;

            // Find input length (null-terminated).
            int inLen = 0;
            while (lpFileName[inLen] != 0) inLen++;

            // FromUtf16(char* + len) constructs a managed string без the
            // string-from-char[] ctor that ILC doesn't link in our config.
            string input = String.FromUtf16(lpFileName, inLen);
            string full = System.IO.Path.GetFullPath(input);
            int outLen = full.Length;

            if (lpBuffer == null || nBufferLength == 0)
                return (uint)(outLen + 1);  // required size including null

            if ((uint)outLen + 1 > nBufferLength)
                return (uint)(outLen + 1);  // buffer too small — caller probes

            for (int i = 0; i < outLen; i++) lpBuffer[i] = full[i];
            lpBuffer[outLen] = '\0';
            return (uint)outLen;            // excluding null on success
        }

        // --- File I/O substrate ---
        //
        // Fake handle = pointer к FileState struct on GcHeap. Buffer holds
        // entire file content (slurped at open time via Platform.TryReadFile
        // → UEFI SimpleFileSystem). Read seeks within in-memory buffer.
        // CloseHandle is no-op; GC reclaims state + buffer.

        [StructLayout(LayoutKind.Sequential)]
        private struct FileState
        {
            public void* Buffer;
            public uint Size;
            public uint Position;
            public uint Flags;
        }

        [RuntimeExport("SharpOSHost_FileOpen")]
        public static void* FileOpen(char* lpFileName)
        {
            if (lpFileName == null) return null;
            int len = 0;
            while (lpFileName[len] != 0) len++;

            // Strip Windows extended-path prefix `\\?\` if present —
            // CoreCLR's Path.Windows.cs / GetFullPathName upper layer
            // sometimes prepends it. UEFI SimpleFileSystem expects plain
            // backslash paths relative to FS root.
            char* p = lpFileName;
            int pLen = len;
            if (pLen >= 4 && p[0] == '\\' && p[1] == '\\' && p[2] == '?' && p[3] == '\\')
            {
                p += 4;
                pLen -= 4;
            }
            // Strip virtual drive-letter prefix "C:\..." → "\..." — managed
            // code uses C:\sharpos\* so BCL's Path.IsPathFullyQualified accepts
            // them, but our FAT/UEFI FS root has no drive concept.
            if (pLen >= 3 && (p[0] == 'C' || p[0] == 'c') && p[1] == ':' && p[2] == '\\')
            {
                p += 2;
                pLen -= 2;
            }
            // Tolerate UNC form "\\sharpos\..." too (in case anything legacy
            // still constructs it).
            else if (pLen >= 2 && p[0] == '\\' && p[1] == '\\')
            {
                p += 1;
                pLen -= 1;
            }
            string path = String.FromUtf16(p, pLen);

            Console.Write("[host] FileOpen path=\"");
            for (int i = 0; i < pLen; i++) Console.WriteChar(p[i]);
            Console.Write("\" ");

            ulong readStart = NowTicks();
            if (!Platform.TryReadFile(path, out void* buf, out uint size))
            {
                Console.WriteLine("→ not found");
                return null;
            }
            ulong readEnd = NowTicks();
            // [PEIMG] ties a loaded image to its flat buffer range. If a
            // [VH] object address falls inside one of these → it's an object
            // baked into the loaded PE image (frozen/preinit), not a kernel
            // alloc → fix = register that region with the GC.
            Console.Write("→ ok size=0x"); Console.WriteHex(size);
            Console.Write(" read_ms=");
            Console.WriteULong(ElapsedMs(readStart, readEnd));
            Console.Write(" [PEIMG buf=0x"); Console.WriteHex((ulong)buf);
            Console.Write(" end=0x"); Console.WriteHex((ulong)buf + size);
            Console.WriteLine("]");

            // PE files loaded for CoreCLR (SPC.dll, etc.) contain native /
            // R2R-compiled code that the runtime jumps to directly via the
            // pointer returned by PAL_LOADLoadPEFile. Our GcHeap allocator
            // returns RW (NX=1) pages — JIT/interop jumps into those pages
            // fault with #PF I=1 (NX violation). Walk the buffer's pages and
            // flip NX off (RWX is the simplest workable scheme; per-section
            // protection refinement is a later phase).
            {
                const ulong PAGE = 4096UL;
                ulong patchStart = NowTicks();
                ulong va = ((ulong)buf) & ~(PAGE - 1);
                ulong end = ((ulong)buf + size + PAGE - 1) & ~(PAGE - 1);
                int patched = 0;
                while (va < end)
                {
                    if (X64PageTable.TrySetKernelFlags(va, PageFlags.Present | PageFlags.Writable))
                        patched++;
                    va += PAGE;
                }
                X64PageTable.FlushTlbAll();
                ulong patchEnd = NowTicks();
                Console.Write("[host] FileOpen RWX patch: pages="); Console.WriteInt(patched);
                Console.Write(" ms=");
                Console.WriteULong(ElapsedMs(patchStart, patchEnd));
                Console.WriteLine("");
            }

            FileState* state = (FileState*)NativeArena.Allocate((ulong)sizeof(FileState));
            if (state == null) return null;
            state->Buffer = buf;
            state->Size = size;
            state->Position = 0;
            state->Flags = 0;
            return state;
        }

        [RuntimeExport("SharpOSHost_FileRead")]
        public static int FileRead(void* handle, void* outBuf, uint count, uint* bytesRead)
        {
            if (handle == null || outBuf == null)
            {
                if (bytesRead != null) *bytesRead = 0;
                return 0;
            }
            FileState* s = (FileState*)handle;
            uint avail = s->Size - s->Position;
            uint toCopy = count < avail ? count : avail;
            byte* src = (byte*)s->Buffer + s->Position;
            byte* dst = (byte*)outBuf;
            for (uint i = 0; i < toCopy; i++) dst[i] = src[i];
            s->Position += toCopy;
            if (bytesRead != null) *bytesRead = toCopy;
            return 1;
        }

        [RuntimeExport("SharpOSHost_FileGetSize")]
        public static uint FileGetSize(void* handle)
        {
            if (handle == null) return 0xFFFFFFFFu;
            return ((FileState*)handle)->Size;
        }

        // origin: 0 = FILE_BEGIN, 1 = FILE_CURRENT, 2 = FILE_END.
        // Returns new absolute position, or -1 on error.
        [RuntimeExport("SharpOSHost_FileSetPosition")]
        public static long FileSetPosition(void* handle, long distance, uint origin)
        {
            if (handle == null) return -1;
            FileState* s = (FileState*)handle;
            long newPos;
            switch (origin)
            {
                case 0: newPos = distance; break;
                case 1: newPos = (long)s->Position + distance; break;
                case 2: newPos = (long)s->Size + distance; break;
                default: return -1;
            }
            if (newPos < 0 || newPos > s->Size) return -1;
            s->Position = (uint)newPos;
            return newPos;
        }

        [RuntimeExport("SharpOSHost_FileClose")]
        public static void FileClose(void* handle)
        {
            // No-op — GC reclaims FileState and its buffer when unreachable.
        }
    }
}
