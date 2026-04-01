namespace OS.Kernel.Paging
{
    internal static class Pager
    {
        private static bool s_initialized;
        private static PagingRequirements s_requirements;

        public static bool IsInitialized => s_initialized;

        public static bool Init(PagingRequirements requirements)
        {
            if (s_initialized)
                return true;

            if (requirements.PageSize == 0)
                requirements.PageSize = X64PageTable.PageSize;

            if (requirements.PageSize != X64PageTable.PageSize)
                return false;

            if (requirements.InitialPageTablePages == 0)
                requirements.InitialPageTablePages = 1;

            if (!X64PageTable.Init(requirements))
                return false;

            s_requirements = requirements;
            s_initialized = true;
            return true;
        }

        public static bool Map(ulong virtualAddress, ulong physicalAddress, PageFlags flags)
        {
            if (!s_initialized)
                return false;

            if (!IsAligned(virtualAddress) || !IsAligned(physicalAddress))
                return false;

            return X64PageTable.Map(virtualAddress, physicalAddress, flags);
        }

        public static bool MapRange(ulong virtualAddressStart, ulong physicalAddressStart, uint pageCount, PageFlags flags)
        {
            if (!s_initialized || pageCount == 0)
                return false;

            if (!IsAligned(virtualAddressStart) || !IsAligned(physicalAddressStart))
                return false;

            ulong currentVirtual = virtualAddressStart;
            ulong currentPhysical = physicalAddressStart;
            uint mappedCount = 0;

            for (uint i = 0; i < pageCount; i++)
            {
                if (!Map(currentVirtual, currentPhysical, flags))
                {
                    RollbackMappedRange(virtualAddressStart, mappedCount);
                    return false;
                }

                mappedCount++;
                if (i + 1 < pageCount && (!TryIncrementPageAddress(ref currentVirtual) || !TryIncrementPageAddress(ref currentPhysical)))
                {
                    RollbackMappedRange(virtualAddressStart, mappedCount);
                    return false;
                }
            }

            return true;
        }

        public static bool Unmap(ulong virtualAddress)
        {
            if (!s_initialized)
                return false;

            if (!IsAligned(virtualAddress))
                return false;

            return X64PageTable.Unmap(virtualAddress);
        }

        public static bool UnmapRange(ulong virtualAddressStart, uint pageCount)
        {
            if (!s_initialized || pageCount == 0)
                return false;

            if (!IsAligned(virtualAddressStart))
                return false;

            ulong currentVirtual = virtualAddressStart;
            for (uint i = 0; i < pageCount; i++)
            {
                if (!Unmap(currentVirtual))
                    return false;

                if (i + 1 < pageCount && !TryIncrementPageAddress(ref currentVirtual))
                    return false;
            }

            return true;
        }

        public static bool TryQuery(ulong virtualAddress, out ulong physicalAddress, out PageFlags flags)
        {
            physicalAddress = 0;
            flags = PageFlags.None;

            if (!s_initialized)
                return false;

            if (!X64PageTable.TryQuery(virtualAddress, out ulong pageBasePhysicalAddress, out flags))
                return false;

            ulong pageOffset = virtualAddress & (X64PageTable.PageSize - 1);
            physicalAddress = pageBasePhysicalAddress + pageOffset;
            return true;
        }

        public static bool TryGetWalkInfo(ulong virtualAddress, out PageWalkInfo walkInfo)
        {
            walkInfo = default;
            if (!s_initialized)
                return false;

            return X64PageTable.TryGetWalkInfo(virtualAddress, out walkInfo);
        }

        public static void GetSummary(out PagingSummary summary)
        {
            X64PageTable.GetSummary(out summary);
            summary.IsInitialized = s_initialized;
            summary.PageSize = s_requirements.PageSize;
            summary.DirectMapBase = s_requirements.DirectMapBase;
        }

        private static bool IsAligned(ulong value)
        {
            return (value & (X64PageTable.PageSize - 1)) == 0;
        }

        private static bool TryIncrementPageAddress(ref ulong address)
        {
            ulong pageSize = X64PageTable.PageSize;
            if (address > 0xFFFFFFFFFFFFFFFFUL - pageSize)
                return false;

            address += pageSize;
            return true;
        }

        private static void RollbackMappedRange(ulong virtualAddressStart, uint mappedCount)
        {
            ulong currentVirtual = virtualAddressStart;
            for (uint i = 0; i < mappedCount; i++)
            {
                Unmap(currentVirtual);
                if (i + 1 < mappedCount)
                    currentVirtual += X64PageTable.PageSize;
            }
        }
    }
}
