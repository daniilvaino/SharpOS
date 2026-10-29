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
    /// Keyed by path, upper-cased (FAT names do not tell case). A copy is
    /// used while the path leads to the same first cluster and size and the
    /// volume's journal of changes names no write into that file since
    /// (step208; a size check alone would miss a file rewritten in place). A
    /// sector write from outside any file, or a journal outrun, counts as a
    /// change to every file. Before step208 any write dropped the whole
    /// cache, and the shell's history line after each command meant an
    /// interactive start never hit it. A file opened for sector writes, the
    /// boot log's, is never kept. At most <see cref="LimitBytes"/>;
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
            public uint First;          // the file's first cluster
            public ulong Generation;    // Fat32.ContentGeneration it was last known unchanged at
        }

        private static readonly System.Collections.Generic.List<Entry> s_entries = new System.Collections.Generic.List<Entry>();
        private static ulong s_bytes;
        private static ulong s_clock;
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
        public static bool TryTake(string path, out void* buffer, out uint size, out AppServiceStatus status, out bool hit)
        {
            buffer = null;
            size = 0;
            status = AppServiceStatus.Ok;
            hit = false;
            if (Fs.Current == null || path == null) return false;

            string key = Fat32.PathKey(path);
            bool located = Fat32.Locate(path, out uint first, out uint locatedSize);
            Preemption.Suppress();
            Entry kept = located ? Find(key) : null;
            if (kept != null) kept.Users++;          // held while it is checked
            Preemption.Allow();

            // Kept only while the path leads to the same clusters, the same
            // size, and nothing wrote into that file since it was read.
            if (kept != null)
            {
                ulong seen = kept.Generation;
                if (kept.First == first && kept.Size == locatedSize && !Fat32.ChangedSince(first, ref seen))
                {
                    Preemption.Suppress();
                    if (seen > kept.Generation) kept.Generation = seen;
                    kept.LastUse = ++s_clock;
                    s_hits++;
                    Preemption.Allow();
                    buffer = kept.Buffer;
                    size = kept.Size;
                    hit = true;
                    return true;
                }
                Preemption.Suppress();
                kept.Key = null;                      // stale: freed when its last start is done
                Preemption.Allow();
                Return(kept.Buffer);
            }

            ulong generation = Fat32.ContentGeneration;
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

            // These take the volume's lock: not under suppression.
            bool sectorWritten = Fat32.IsSectorWritten(key);
            bool same = located && Fat32.Locate(path, out uint firstAfter, out uint sizeAfter)
                        && firstAfter == first && sizeAfter == fileSize
                        && !Fat32.ChangedSince(first, ref generation);
            var entry = new Entry { Key = key, Buffer = file, Size = fileSize, Users = 1, First = first, Generation = generation };
            Preemption.Suppress();
            s_misses++;
            entry.LastUse = ++s_clock;
            // A file that changed while it was read, a second copy of one
            // read meanwhile, or one too small to give back is not kept for
            // others: no key, freed when its start is done.
            if (!same || Find(key) != null || !Cacheable(fileSize) || sectorWritten)
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

        private static void Remove(int i)
        {
            Entry e = s_entries[i];
            s_entries.RemoveAt(i);
            s_bytes -= PagedBytes(e.Size);
            OS.Kernel.Memory.NativeArena.FreeLarge(e.Buffer, e.Size);
        }
    }
}
