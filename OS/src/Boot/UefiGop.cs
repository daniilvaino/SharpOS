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
            public delegate* unmanaged<GopProtocol*, uint, nuint*, GopModeInfo**, ulong> QueryMode;
            public delegate* unmanaged<GopProtocol*, uint, ulong> SetMode;
            public void* Blt;
            public GopMode* Mode;
        }

        // Bounds live in Probes with the rest of the compile-time knobs; 0
        // means "no limit, take the largest the firmware offers".
        private const uint MaxWidth = OS.Kernel.Diagnostics.Probes.DisplayMaxWidth;
        private const uint MaxHeight = OS.Kernel.Diagnostics.Probes.DisplayMaxHeight;

        public static uint SelectedWidth, SelectedHeight;
        public static uint ModeCount;
        public static bool ModeChanged;

        private static EFI_GUID GopGuid() => new EFI_GUID
        {
            Data1 = 0x9042a9de, Data2 = 0x23dc, Data3 = 0x4a38,
            Data4_0 = 0x96, Data4_1 = 0xfb, Data4_2 = 0x7a, Data4_3 = 0xde,
            Data4_4 = 0xd0, Data4_5 = 0x80, Data4_6 = 0x51, Data4_7 = 0x6a
        };

        // Switch modes ONLY when the current one is bigger than we can drive.
        //
        // Switching unconditionally cost every early log line: the firmware's
        // console is bound to the mode it set up, and SetMode leaves it
        // drawing nowhere — with our own console not up yet, the machine goes
        // silent before the banner and looks hung. Whatever the firmware
        // chose, if we can live with it, we leave it alone.
        //
        // Done before the framebuffer is captured, because SetMode changes
        // the base address and the stride — capturing first would leave us
        // drawing into the previous mode's buffer.
        private static void TrySelectMode(GopProtocol* gop)
        {
            if (gop->QueryMode == null || gop->SetMode == null) return;

            uint modes = gop->Mode->MaxMode;
            ModeCount = modes;
            if (modes == 0) return;

            GopModeInfo* current = gop->Mode->Info;
            SelectedWidth = current->HorizontalResolution;
            SelectedHeight = current->VerticalResolution;

            bool tooWide = MaxWidth != 0 && current->HorizontalResolution > MaxWidth;
            bool tooTall = MaxHeight != 0 && current->VerticalResolution > MaxHeight;
            bool unusable = current->PixelFormat == 3;
            if (!tooWide && !tooTall && !unusable) return;

            uint bestMode = gop->Mode->Mode;
            uint bestPixels = 0;
            bool found = false;

            for (uint i = 0; i < modes; i++)
            {
                nuint size = 0;
                GopModeInfo* info = null;
                if (gop->QueryMode(gop, i, &size, &info) != 0 || info == null) continue;

                // BltOnly has no linear framebuffer, so it is useless to us
                // however attractive its resolution.
                if (info->PixelFormat == 3) continue;
                if (MaxWidth != 0 && info->HorizontalResolution > MaxWidth) continue;
                if (MaxHeight != 0 && info->VerticalResolution > MaxHeight) continue;

                uint pixels = info->HorizontalResolution * info->VerticalResolution;
                if (pixels <= bestPixels) continue;

                bestPixels = pixels;
                bestMode = i;
                found = true;
            }

            if (!found) return;

            if (bestMode != gop->Mode->Mode && gop->SetMode(gop, bestMode) == 0)
                ModeChanged = true;

            SelectedWidth = gop->Mode->Info->HorizontalResolution;
            SelectedHeight = gop->Mode->Info->VerticalResolution;
        }

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

            TrySelectMode(gop);

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
