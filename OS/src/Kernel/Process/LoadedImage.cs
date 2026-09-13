namespace OS.Kernel.Process
{
    /// <summary>
    /// An application image mapped into the address space, as PeLoader leaves
    /// it for ProcessImageBuilder.
    /// </summary>
    /// <remarks>
    /// Format-neutral on purpose: the builder and JumpStub need only the entry
    /// point and the mapped range. It was named ElfLoadedImage while the ELF
    /// loader produced it too; PE has been the only format since step137.
    /// </remarks>
    internal struct LoadedImage
    {
        public ulong EntryPoint;
        public uint SectionCount;
        public ulong LoadedPages;
        public ulong LowestVirtualAddress;
        public ulong HighestVirtualAddressExclusive;

        /// <summary>
        /// The SharpOS record out of the image's own manifest resource, when it
        /// carries one.
        /// </summary>
        /// <remarks>
        /// Read at load time because that is when the image is mapped and its
        /// resource directory is addressable. The alternative — reading the file
        /// again before loading, purely to look at a few hundred bytes near its
        /// end — costs a second read of megabytes per launch.
        /// </remarks>
        public bool ManifestFound;
        public uint ManifestSchema;
        public uint ManifestAbi;
        public uint ManifestServiceAbi;
    }
}
