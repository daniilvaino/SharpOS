using OS.Hal;
using OS.Kernel.Threading;

namespace OS.Kernel.Process
{
    /// <summary>
    /// Program files read for starts, kept (step196): the second start of a
    /// program does not read it again. A start copies the image out of the
    /// file, so one buffer serves any number of starts, in turn or at once.
    /// </summary>
    /// <remarks>
    /// Keyed by path, upper-cased (FAT names do not tell case). A write to
    /// the volume drops the whole cache (a size check would miss a file
    /// rewritten in place) — except a sector write into a file opened for
    /// such writes, the boot log's: that file is never kept. At most <see cref="LimitBytes"/>;
    /// the least recently started program goes first, never one a start is
    /// reading. Only once the kernel's own file system is mounted: before
    /// that the firmware reads files, into memory the arena does not own.
    /// </remarks>
    internal static unsafe class ProgramCache
    {
        public const ulong LimitBytes = 64UL << 20;
        private const ulong PageSize = 4096;

        private sealed class Entry
        {
            public string Key;
            public void* Buffer;
            public uint Size;
            public ulong LastUse;
            public int Users;
        }

        private static readonly System.Collections.Generic.List<Entry> s_entries = new System.Collections.Generic.List<Entry>();
        private static ulong s_bytes;
        private static ulong s_clock;
        private static ulong s_generation;
        private static ulong s_hits, s_misses;

        /// <summary>Pages the cache holds: the kernel's books leave them out, as they do its heap.</summary>
        public static ulong Pages => s_bytes / PageSize;

        public static ulong Hits => s_hits;
        public static ulong Misses => s_misses;

        /// <summary>
        /// The file at <paramref name="path"/>, from the cache or read into
        /// it. The caller gives it back with <see cref="Return"/>. False when
        /// the cache is not in use: the caller reads and frees as before.
        /// </summary>
        public static bool TryTake(string path, out void* buffer, out uint size, out AppServiceStatus status)
        {
            buffer = null;
            size = 0;
            status = AppServiceStatus.Ok;
            if (Fs.Current == null || path == null) return false;

            string key = path.ToUpperInvariant();
            Preemption.Suppress();
            DropIfWritten();
            Entry hit = Find(key);
            if (hit != null)
            {
                hit.Users++;
                hit.LastUse = ++s_clock;
                s_hits++;
                buffer = hit.Buffer;
                size = hit.Size;
            }
            Preemption.Allow();
            if (hit != null) return true;

            ulong generation = Fat32.WriteGeneration;
            int probe = Fs.Current.ReadFile(path, null, 0, out uint fileSize);
            if (probe < 0 || fileSize == 0) { status = AppServiceStatus.NotFound; return true; }
            void* file = OS.Kernel.Memory.NativeArena.Allocate(fileSize);
            if (file == null) { status = AppServiceStatus.DeviceError; return true; }
            if (Fs.Current.ReadFile(path, (byte*)file, (int)fileSize, out fileSize) < 0)
            {
                OS.Kernel.Memory.NativeArena.FreeLarge(file, fileSize);
                status = AppServiceStatus.NotFound;
                return true;
            }

            bool sectorWritten = Fat32.IsSectorWritten(key);   // takes the volume's lock: not under suppression
            var entry = new Entry { Key = key, Buffer = file, Size = fileSize, Users = 1 };
            Preemption.Suppress();
            s_misses++;
            entry.LastUse = ++s_clock;
            // A file read while the volume was written to, a second copy of
            // one read meanwhile, or one too small to give back is not kept
            // for others: no key, freed when its start is done.
            if (generation != Fat32.WriteGeneration || Find(key) != null || !Cacheable(fileSize) || sectorWritten)
                entry.Key = null;
            s_entries.Add(entry);
            s_bytes += PagedBytes(fileSize);
            Evict();
            Preemption.Allow();
            buffer = file;
            size = fileSize;
            return true;
        }

        /// <summary>A start is done with the file <see cref="TryTake"/> gave.</summary>
        public static void Return(void* buffer)
        {
            if (buffer == null) return;
            Preemption.Suppress();
            for (int i = 0; i < s_entries.Count; i++)
            {
                Entry e = s_entries[i];
                if (e.Buffer != buffer || e.Users == 0) continue;
                if (--e.Users == 0 && e.Key == null) Remove(i);
                break;
            }
            Evict();
            Preemption.Allow();
        }

        // Only what the arena gives back: smaller files share its chunks.
        private static bool Cacheable(uint size) => size >= 64 * 1024 && PagedBytes(size) <= LimitBytes;

        private static ulong PagedBytes(uint size) => ((ulong)size + PageSize - 1) & ~(PageSize - 1);

        private static Entry Find(string key)
        {
            for (int i = 0; i < s_entries.Count; i++)
                if (s_entries[i].Key == key) return s_entries[i];
            return null;
        }

        // Under suppressed preemption. Frees with preemption suppressed too:
        // FreeLarge only hands pages back.
        private static void Evict()
        {
            while (s_bytes > LimitBytes)
            {
                int oldest = -1;
                for (int i = 0; i < s_entries.Count; i++)
                    if (s_entries[i].Users == 0 && (oldest < 0 || s_entries[i].LastUse < s_entries[oldest].LastUse))
                        oldest = i;
                if (oldest < 0) return;
                Remove(oldest);
            }
        }

        private static void DropIfWritten()
        {
            if (s_generation == Fat32.WriteGeneration) return;
            s_generation = Fat32.WriteGeneration;
            for (int i = s_entries.Count - 1; i >= 0; i--)
            {
                if (s_entries[i].Users == 0) Remove(i);
                else s_entries[i].Key = null;   // in use: never found again, freed on Return
            }
        }

        private static void Remove(int i)
        {
            Entry e = s_entries[i];
            s_entries.RemoveAt(i);
            s_bytes -= PagedBytes(e.Size);
            OS.Kernel.Memory.NativeArena.FreeLarge(e.Buffer, e.Size);
        }
    }
}
