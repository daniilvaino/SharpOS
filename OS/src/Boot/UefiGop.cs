namespace OS.Boot
{
    // Phase B#2 sub-step 1 — capture the GOP framebuffer while UEFI Boot
    // Services are still alive (SharpOS never ExitBootServices, so the
    // protocol stays valid, but we snapshot base/geometry into BootInfo
    // so the kernel can map it and a managed renderer can draw to it).
    // EFI_GRAPHICS_OUTPUT_PROTOCOL GUID {9042a9de-23dc-4a38-96fb-7aded080516a}.
    internal static unsafe class UefiGop
    {
        // EFI_GRAPHICS_OUTPUT_MODE_INFORMATION (9 × UINT32, sequential).
        private struct GopModeInfo
        {
            public uint Version;
            public uint HorizontalResolution;
            public uint VerticalResolution;
            public uint PixelFormat;          // EFI_GRAPHICS_PIXEL_FORMAT
            public uint Mask0, Mask1, Mask2, Mask3;   // EFI_PIXEL_BITMASK
            public uint PixelsPerScanLine;
        }

        // EFI_GRAPHICS_OUTPUT_PROTOCOL_MODE.
        private struct GopMode
        {
            public uint MaxMode;
            public uint Mode;
            public GopModeInfo* Info;
            public nuint SizeOfInfo;
            public ulong FrameBufferBase;     // EFI_PHYSICAL_ADDRESS
            public nuint FrameBufferSize;
        }

        // EFI_GRAPHICS_OUTPUT_PROTOCOL: 3 fn ptrs (QueryMode/SetMode/Blt)
        // then Mode*. We only read Mode.
        private struct GopProtocol
        {
            public void* QueryMode;
            public void* SetMode;
            public void* Blt;
            public GopMode* Mode;
        }

        private static EFI_GUID GopGuid() => new EFI_GUID
        {
            Data1 = 0x9042a9de, Data2 = 0x23dc, Data3 = 0x4a38,
            Data4_0 = 0x96, Data4_1 = 0xfb, Data4_2 = 0x7a, Data4_3 = 0xde,
            Data4_4 = 0xd0, Data4_5 = 0x80, Data4_6 = 0x51, Data4_7 = 0x6a
        };

        // Fill BootInfo framebuffer fields + GraphicsAvailable. Returns
        // false (and leaves fields zeroed) on no GOP / BltOnly-no-FB.
        public static bool TryCapture(EFI_SYSTEM_TABLE* st, ref BootInfo info)
        {
            if (st == null || st->BootServices == null)
                return false;

            EFI_GUID guid = GopGuid();
            GopProtocol* gop = null;
            ulong status = st->BootServices->LocateProtocol(&guid, null, (void**)&gop);
            if (status != 0 || gop == null || gop->Mode == null || gop->Mode->Info == null)
                return false;

            GopMode* m = gop->Mode;
            GopModeInfo* mi = m->Info;

            // PixelBltOnly (3) → no linear framebuffer to draw into.
            if (mi->PixelFormat == 3 || m->FrameBufferBase == 0 || m->FrameBufferSize == 0)
                return false;

            info.FramebufferBase        = m->FrameBufferBase;
            info.FramebufferSize        = (ulong)m->FrameBufferSize;
            info.FramebufferWidth       = mi->HorizontalResolution;
            info.FramebufferHeight      = mi->VerticalResolution;
            info.FramebufferStride      = mi->PixelsPerScanLine;
            info.FramebufferPixelFormat = mi->PixelFormat;
            info.GraphicsAvailable      = 1;
            return true;
        }
    }
}
