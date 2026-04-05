using OS.Hal;

namespace OS.Kernel.Paging
{
    internal struct PagingSummary
    {
        public bool IsInitialized;
        public bool IsPagerRootActive;
        public ulong PageSize;
        public ulong DirectMapBase;
        public ulong RootTablePhysical;
        public ulong KernelRootTablePhysical;
        public ulong KernelCr3;
        public ulong PagerCr3;
        public ulong ActiveCr3;
        public uint TablePages;
        public uint SpareTablePages;
        public uint MappedPages;
        public uint MapCalls;
        public uint MapFailures;
        public uint QueryCalls;
        public uint QueryHits;
        public uint UnmapCalls;
        public uint UnmapFailures;
    }

    internal static class PagingDiagnostics
    {
        private const ulong AddressMask = 0x000FFFFFFFFFF000UL;
        private const ulong PresentMask = 1UL << 0;
        private const ulong WritableMask = 1UL << 1;
        private const ulong UserMask = 1UL << 2;
        private const ulong WriteThroughMask = 1UL << 3;
        private const ulong CacheDisableMask = 1UL << 4;
        private const ulong AccessedMask = 1UL << 5;
        private const ulong DirtyMask = 1UL << 6;
        private const ulong PageSizeMask = 1UL << 7;
        private const ulong GlobalMask = 1UL << 8;
        private const ulong NoExecuteMask = 1UL << 63;

        public static void DumpSummary()
        {
            Pager.GetSummary(out PagingSummary summary);

            Log.Begin(LogLevel.Info);
            Console.Write("pager page size: ");
            Console.WriteULong(summary.PageSize);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("pager root table: 0x");
            Console.WriteHex(summary.RootTablePhysical, 8);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("kernel root table: 0x");
            Console.WriteHex(summary.KernelRootTablePhysical, 8);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("cr3 kernel/pager/active: 0x");
            Console.WriteHex(summary.KernelCr3, 16);
            Console.Write("/0x");
            Console.WriteHex(summary.PagerCr3, 16);
            Console.Write("/0x");
            Console.WriteHex(summary.ActiveCr3, 16);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("pager root active: ");
            Console.Write(summary.IsPagerRootActive ? "yes" : "no");
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("pager table pages/spare: ");
            Console.WriteUInt(summary.TablePages);
            Console.Write("/");
            Console.WriteUInt(summary.SpareTablePages);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("pager mapped pages: ");
            Console.WriteUInt(summary.MappedPages);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("pager map/query/unmap calls: ");
            Console.WriteUInt(summary.MapCalls);
            Console.Write("/");
            Console.WriteUInt(summary.QueryCalls);
            Console.Write("/");
            Console.WriteUInt(summary.UnmapCalls);
            Log.EndLine();

            Log.Begin(LogLevel.Info);
            Console.Write("pager failures map/unmap: ");
            Console.WriteUInt(summary.MapFailures);
            Console.Write("/");
            Console.WriteUInt(summary.UnmapFailures);
            Log.EndLine();
        }

        public static void DumpMapping(ulong virtualAddress)
        {
            if (!Pager.TryGetWalkInfo(virtualAddress, out PageWalkInfo walkInfo))
            {
                Log.Write(LogLevel.Warn, "paging walk unavailable");
                return;
            }

            Log.Begin(LogLevel.Info);
            Console.Write("paging walk 0x");
            Console.WriteHex(virtualAddress, 16);
            Console.Write(": pml4=");
            Console.WriteUInt(walkInfo.Pml4Index);
            Console.Write(" pdpt=");
            Console.WriteUInt(walkInfo.PdptIndex);
            Console.Write(" pd=");
            Console.WriteUInt(walkInfo.PdIndex);
            Console.Write(" pt=");
            Console.WriteUInt(walkInfo.PtIndex);
            Log.EndLine();

            DumpWalkEntry("pml4e", walkInfo.Pml4Entry, walkInfo.Pml4Present);
            DumpWalkEntry("pdpte", walkInfo.PdptEntry, walkInfo.PdptPresent);
            DumpWalkEntry("pde", walkInfo.PdEntry, walkInfo.PdPresent);
            DumpWalkEntry("pte", walkInfo.PtEntry, walkInfo.PtPresent);

            Log.Begin(LogLevel.Info);
            Console.Write("paging query 0x");
            Console.WriteHex(virtualAddress, 16);
            Console.Write(" -> ");

            if (!Pager.TryQuery(virtualAddress, out ulong physicalAddress, out PageFlags flags))
            {
                Console.Write("not mapped");
                Log.EndLine();
                return;
            }

            Console.Write("0x");
            Console.WriteHex(physicalAddress, 8);
            Console.Write(" flags=");
            WriteFlags(flags);
            Log.EndLine();
        }

        private static void DumpWalkEntry(string level, ulong entry, bool present)
        {
            Log.Begin(LogLevel.Info);
            Console.Write("paging ");
            Console.Write(level);
            Console.Write(": raw=0x");
            Console.WriteHex(entry, 16);
            Console.Write(" present=");
            Console.Write(present ? "1" : "0");

            if (present)
            {
                Console.Write(" addr=0x");
                Console.WriteHex(entry & AddressMask, 8);
                Console.Write(" flags=");
                WriteEntryFlags(entry);
            }

            Log.EndLine();
        }

        private static void WriteFlags(PageFlags flags)
        {
            bool first = true;
            WriteFlag(flags, PageFlags.Present, "Present", ref first);
            WriteFlag(flags, PageFlags.Writable, "Writable", ref first);
            WriteFlag(flags, PageFlags.User, "User", ref first);
            WriteFlag(flags, PageFlags.WriteThrough, "WriteThrough", ref first);
            WriteFlag(flags, PageFlags.CacheDisable, "CacheDisable", ref first);
            WriteFlag(flags, PageFlags.Global, "Global", ref first);
            WriteFlag(flags, PageFlags.NoExecute, "NoExecute", ref first);

            if (first)
                Console.Write("None");
        }

        private static void WriteEntryFlags(ulong entry)
        {
            bool first = true;
            WriteEntryFlag(entry, PresentMask, "P", ref first);
            WriteEntryFlag(entry, WritableMask, "W", ref first);
            WriteEntryFlag(entry, UserMask, "U", ref first);
            WriteEntryFlag(entry, WriteThroughMask, "WT", ref first);
            WriteEntryFlag(entry, CacheDisableMask, "CD", ref first);
            WriteEntryFlag(entry, AccessedMask, "A", ref first);
            WriteEntryFlag(entry, DirtyMask, "D", ref first);
            WriteEntryFlag(entry, PageSizeMask, "PS", ref first);
            WriteEntryFlag(entry, GlobalMask, "G", ref first);
            WriteEntryFlag(entry, NoExecuteMask, "NX", ref first);

            if (first)
                Console.Write("None");
        }

        private static void WriteEntryFlag(ulong entry, ulong flagMask, string text, ref bool first)
        {
            if ((entry & flagMask) == 0)
                return;

            if (!first)
                Console.Write("|");

            Console.Write(text);
            first = false;
        }

        private static void WriteFlag(PageFlags value, PageFlags flag, string text, ref bool first)
        {
            if ((value & flag) != flag)
                return;

            if (!first)
                Console.Write("|");

            Console.Write(text);
            first = false;
        }
    }
}
