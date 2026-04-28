using OS.Hal;

namespace OS.Boot.EH
{
    // Phase 1 step 6 — patches RethrowStub.RhpRethrow с byte-array
    // shellcode (~186 bytes). Layout идентичен ThrowExPatcher (step 48)
    // c единственным отличием: ExInfo.Kind set к (Throw | Rethrow) = 0x05
    // вместо чистого Throw = 0x01. Dispatcher reads this flag to use
    // prev ExInfo's ExContext + start FindFirstPassHandler от
    // prev->IdxCurClause (skipping the catch that just ran).
    //
    // Same frame layout (FRAME_SIZE = 0x388):
    //   [rsp + 0x000 .. 0x01F]   shadow space
    //   [rsp + 0x020 .. 0x11F]   PAL_LIMITED_CONTEXT (captured rethrow site)
    //   [rsp + 0x120 .. 0x37F]   ExInfo (kind = Throw|Rethrow)
    //   [rsp + 0x380 .. 0x387]   alignment pad
    //
    // Rethrow site context capture is technically wasted (Dispatch uses
    // prev's ExContext, not ours), but keeping shellcode parallel к
    // ThrowEx avoids subtle layout drift. ~10 bytes of waste is fine.
    internal static unsafe class RethrowPatcher
    {
        private const int OffsetCtx     = 0x020;
        private const int OffsetExInfo  = 0x120;
        private const int FrameSize     = 0x388;

        private static bool s_installed;

        public static bool IsInstalled => s_installed;

        public static bool TryInstall()
        {
            if (s_installed) return true;

            byte* target = (byte*)RethrowStub.GetMethodAddress();
            if (target == null) return false;

            byte** headAddr = ExInfoHead.GetHeadAddress();
            void* ingressFn = GetRethrowIngressAddress();

            int p = 0;

            // capture rethrow-site rsp/rip
            target[p++] = 0x48; target[p++] = 0x8D; target[p++] = 0x44; target[p++] = 0x24; target[p++] = 0x08;   // lea rax, [rsp+8]
            target[p++] = 0x48; target[p++] = 0x8B; target[p++] = 0x14; target[p++] = 0x24;                       // mov rdx, [rsp]

            // spill 8 nonvols
            target[p++] = 0x41; target[p++] = 0x57;
            target[p++] = 0x41; target[p++] = 0x56;
            target[p++] = 0x41; target[p++] = 0x55;
            target[p++] = 0x41; target[p++] = 0x54;
            target[p++] = 0x53;
            target[p++] = 0x56;
            target[p++] = 0x57;
            target[p++] = 0x55;

            // sub rsp, FrameSize
            target[p++] = 0x48; target[p++] = 0x81; target[p++] = 0xEC;
            WriteUInt32(target, ref p, (uint)FrameSize);

            // PAL.Rsp = rax; PAL.IP = rdx
            EmitMovMemReg(target, ref p, 0, false, OffsetCtx + PalLimitedContext.OffsetRsp);
            EmitMovMemReg(target, ref p, 2, false, OffsetCtx + PalLimitedContext.OffsetIP);

            // GPR snapshots
            EmitMovMemReg(target, ref p, 5, false, OffsetCtx + PalLimitedContext.OffsetRbp);
            EmitMovMemReg(target, ref p, 7, false, OffsetCtx + PalLimitedContext.OffsetRdi);
            EmitMovMemReg(target, ref p, 6, false, OffsetCtx + PalLimitedContext.OffsetRsi);
            EmitMovMemReg(target, ref p, 3, false, OffsetCtx + PalLimitedContext.OffsetRbx);
            EmitMovMemReg(target, ref p, 4, true,  OffsetCtx + PalLimitedContext.OffsetR12);
            EmitMovMemReg(target, ref p, 5, true,  OffsetCtx + PalLimitedContext.OffsetR13);
            EmitMovMemReg(target, ref p, 6, true,  OffsetCtx + PalLimitedContext.OffsetR14);
            EmitMovMemReg(target, ref p, 7, true,  OffsetCtx + PalLimitedContext.OffsetR15);

            // rdx = &ExInfo
            target[p++] = 0x48; target[p++] = 0x8D; target[p++] = 0x94; target[p++] = 0x24;
            WriteUInt32(target, ref p, (uint)OffsetExInfo);
            // rax = &PAL
            target[p++] = 0x48; target[p++] = 0x8D; target[p++] = 0x84; target[p++] = 0x24;
            WriteUInt32(target, ref p, (uint)OffsetCtx);
            // ExInfo.m_pExContext = rax
            target[p++] = 0x48; target[p++] = 0x89; target[p++] = 0x42; target[p++] = (byte)ExInfo.OffsetExContext;

            // r8 = &s_head; r9 = *r8 (current head = active ExInfo to inherit from)
            target[p++] = 0x49; target[p++] = 0xB8;
            WriteUInt64(target, ref p, (ulong)(nuint)headAddr);
            target[p++] = 0x4D; target[p++] = 0x8B; target[p++] = 0x08;
            // ExInfo.m_pPrevExInfo = r9 (active ExInfo — Dispatcher uses
            // prev's ExContext + IdxCurClause for rethrow semantics)
            target[p++] = 0x4C; target[p++] = 0x89; target[p++] = 0x0A;

            // ExInfo.m_exception = null
            target[p++] = 0x48; target[p++] = 0x31; target[p++] = 0xC0;
            target[p++] = 0x48; target[p++] = 0x89; target[p++] = 0x42; target[p++] = (byte)ExInfo.OffsetException;

            // ExInfo.m_kind = (Throw | Rethrow) = 0x05  ← single byte difference vs ThrowExPatcher
            target[p++] = 0xC6; target[p++] = 0x42; target[p++] = (byte)ExInfo.OffsetKind;
            target[p++] = (byte)(ExInfo.KindThrow | ExInfo.KindRethrow);

            // ExInfo.m_passNumber = 1
            target[p++] = 0xC6; target[p++] = 0x42; target[p++] = (byte)ExInfo.OffsetPassNumber; target[p++] = 0x01;

            // ExInfo.m_idxCurClause = 0xFFFFFFFF (sentinel; Dispatcher
            // overrides for rethrow с prev->IdxCurClause)
            target[p++] = 0xC7; target[p++] = 0x42; target[p++] = (byte)ExInfo.OffsetIdxCurClause;
            target[p++] = 0xFF; target[p++] = 0xFF; target[p++] = 0xFF; target[p++] = 0xFF;

            // s_head = &ExInfo
            target[p++] = 0x49; target[p++] = 0x89; target[p++] = 0x10;

            // call ingress (RCX still holds exception, RDX = &ExInfo)
            target[p++] = 0x49; target[p++] = 0xBA;
            WriteUInt64(target, ref p, (ulong)(nuint)ingressFn);
            target[p++] = 0x41; target[p++] = 0xFF; target[p++] = 0xD2;

            target[p++] = 0xCC;     // int3 — should not return

            if (target[0] != 0x48 || target[p - 1] != 0xCC)
                return false;

            Log.Begin(LogLevel.Info);
            Console.Write("rethrow shellcode: bytes=");
            Console.WriteUIntRaw((uint)p);
            Console.Write(" head=0x");
            Console.WriteHexRaw((ulong)(nuint)headAddr, 16);
            Console.Write(" ingress=0x");
            Console.WriteHexRaw((ulong)(nuint)ingressFn, 16);
            Log.EndLine();

            s_installed = true;
            return true;
        }

        // Reuse RhpTest_ThrowIngress as ingress seam — same managed entry
        // что routes к Dispatch when EhRealDispatch=true. Dispatcher
        // detects Rethrow kind flag и handles accordingly.
        private static void* GetRethrowIngressAddress()
        {
            delegate* unmanaged<byte*, ExInfo*, void> fn =
                &OS.Boot.ExceptionEngine.RhpTest_ThrowIngress;
            return (void*)fn;
        }

        private static void EmitMovMemReg(byte* dst, ref int p, int regSrc, bool prefixR, int disp)
        {
            byte rex = (byte)(0x48 | (prefixR ? 0x04 : 0x00));
            dst[p++] = rex;
            dst[p++] = 0x89;
            byte modrm = (byte)((0x80) | ((regSrc & 0x07) << 3) | 0x04);
            dst[p++] = modrm;
            dst[p++] = 0x24;
            WriteUInt32(dst, ref p, (uint)disp);
        }

        private static void WriteUInt32(byte* dst, ref int p, uint val)
        {
            dst[p++] = (byte)(val);
            dst[p++] = (byte)(val >> 8);
            dst[p++] = (byte)(val >> 16);
            dst[p++] = (byte)(val >> 24);
        }

        private static void WriteUInt64(byte* dst, ref int p, ulong val)
        {
            dst[p++] = (byte)(val);
            dst[p++] = (byte)(val >> 8);
            dst[p++] = (byte)(val >> 16);
            dst[p++] = (byte)(val >> 24);
            dst[p++] = (byte)(val >> 32);
            dst[p++] = (byte)(val >> 40);
            dst[p++] = (byte)(val >> 48);
            dst[p++] = (byte)(val >> 56);
        }
    }
}
