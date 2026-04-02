using OS.Kernel.Elf;

namespace OS.Kernel.Process
{
    internal struct ProcessContext
    {
        public ProcessState State;
        public ProcessImage ProcessImage;
        public ElfLoadedImage LoadedImage;
    }
}
