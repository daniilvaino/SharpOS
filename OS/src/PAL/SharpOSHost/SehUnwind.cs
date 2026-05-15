using System.Runtime;
using System.Runtime.InteropServices;
using OS.Boot.EH;
using OS.Hal;

namespace OS.PAL.SharpOSHost
{
    // Phase 2 + 3 of SEH unwind: RtlLookupFunctionEntry + RtlVirtualUnwind.
    //
    // RtlLookupFunctionEntry — given an instruction pointer, find the
    // matching .pdata entry. Already implemented inside CoffMethodLookup
    // (binary search в RUNTIME_FUNCTION array sorted by BeginAddress);
    // this wraps it под Windows ABI.
    //
    // RtlVirtualUnwind — given a function entry + current CONTEXT, apply
    // the function's UNWIND_INFO opcodes to walk one frame upward. After
    // the call: ContextRecord->Rip = caller's return address, Rsp = caller
    // SP, callee-saved regs restored from where they were spilled in
    // prolog. Returns pointer к personality routine if the function has
    // EHANDLER/UHANDLER flag set.
    //
    // Layout per AMD64 ABI (winnt.h UNWIND_INFO + UNWIND_CODE):
    //
    //   UNWIND_INFO:
    //     +0x00 [byte] Version (low 3 bits) | Flags (high 5 bits)
    //     +0x01 [byte] SizeOfProlog
    //     +0x02 [byte] CountOfCodes
    //     +0x03 [byte] FrameRegister (low 4 bits) | FrameOffset (high 4 bits)
    //     +0x04 [array of UNWIND_CODE: 2 bytes each, count = CountOfCodes,
    //            but each code may span multiple slots]
    //     [aligned to DWORD]
    //     if EHANDLER|UHANDLER: ExceptionHandler RVA (4 bytes)
    //                           ExceptionData[...] (language-specific)
    //     if CHAININFO: chained RUNTIME_FUNCTION (12 bytes)
    //
    //   UNWIND_CODE:
    //     +0x00 [byte] OffsetInProlog  — codes only apply if prolog has
    //                                    progressed past this offset.
    //     +0x01 [byte] UnwindOp (low 4 bits) | OpInfo (high 4 bits)
    //
    // For "virtual unwind" (i.e. from inside the body, not mid-prolog),
    // OffsetInProlog is ignored — apply all codes in forward order.
    internal static unsafe class SehUnwind
    {
        // Windows-API-shaped lookup. Returns pointer to RUNTIME_FUNCTION
        // (in our .pdata array) or null if IP isn't в our image.
        //
        // pImageBase out-param: caller uses for unwind-info RVA math.
        // pHistoryTable: unused (optional optimization slot).
        [RuntimeExport("RtlLookupFunctionEntry")]
        [UnmanagedCallersOnly(EntryPoint = "RtlLookupFunctionEntry")]
        public static RuntimeFunction* RtlLookupFunctionEntry(
            ulong controlPc, ulong* pImageBase, void* pHistoryTable)
            => LookupFunctionEntry(controlPc, pImageBase);

        public static RuntimeFunction* LookupFunctionEntry(
            ulong controlPc, ulong* pImageBase)
        {
            if (!CoffRuntimeFunctionTable.IsInitialized)
                return null;

            byte* imageBase = CoffRuntimeFunctionTable.ImageBase;
            if (pImageBase != null) *pImageBase = (ulong)imageBase;

            // Use existing binary search infra. Convert IP → byte* anchor.
            if (!CoffMethodLookup.TryFindMethod((byte*)controlPc, out var info))
                return null;

            // CoffMethodLookup returns CurrentRuntimeFunction (which may be
            // a funclet inside the method). For Windows ABI, that IS what
            // RtlLookupFunctionEntry returns — each .pdata entry is a
            // distinct "function" в RUNTIME_FUNCTION sense. Funclet/parent
            // distinction matters только to personality routines.
            return (RuntimeFunction*)info.CurrentRuntimeFunction;
        }

        // RtlVirtualUnwind — applies one function's unwind codes to a
        // CONTEXT. After return:
        //   Context->Rip = caller's return address
        //   Context->Rsp = caller's SP (= our SP + frame size + 8 for ret)
        //   Context->R*  = restored from where prolog saved them
        //
        // Args:
        //   handlerType  — UNW_FLAG_EHANDLER if walking exception search,
        //                  UNW_FLAG_UHANDLER if walking unwind dispatch,
        //                  0 если просто frame-walk (no handler call needed)
        //   imageBase    — base of PE image (for RVA → VA math)
        //   controlPc    — current IP (for funclet/prolog awareness)
        //   functionEntry — .pdata entry returned by RtlLookupFunctionEntry
        //   context      — input/output: register state
        //   handlerData  — out: language-specific data ptr after personality RVA
        //   establisherFrame — out: caller SP (after frame teardown)
        //
        // Returns: personality routine pointer if function has E/U handler
        //          and handlerType matches; null otherwise.
        [RuntimeExport("RtlVirtualUnwind")]
        [UnmanagedCallersOnly(EntryPoint = "RtlVirtualUnwind")]
        public static void* RtlVirtualUnwind(
            uint handlerType,
            ulong imageBase,
            ulong controlPc,
            RuntimeFunction* functionEntry,
            Context* context,
            void** handlerData,
            ulong* establisherFrame,
            void* contextPointers)
            => VirtualUnwind(handlerType, imageBase, controlPc, functionEntry,
                             context, handlerData, establisherFrame);

        public static void* VirtualUnwind(
            uint handlerType,
            ulong imageBase,
            ulong controlPc,
            RuntimeFunction* functionEntry,
            Context* context,
            void** handlerData,
            ulong* establisherFrame)
        {
            if (functionEntry == null || context == null) return null;

            byte* image = (byte*)imageBase;
            byte* unwindInfo = image + functionEntry->UnwindInfoAddress;

            return ApplyUnwindInfo(handlerType, image, controlPc, functionEntry,
                                   unwindInfo, context, handlerData, establisherFrame);
        }

        // Recursive worker — chases UNW_FLAG_CHAININFO to parent fragments.
        private static void* ApplyUnwindInfo(
            uint handlerType,
            byte* image,
            ulong controlPc,
            RuntimeFunction* functionEntry,
            byte* unwindInfo,
            Context* context,
            void** handlerData,
            ulong* establisherFrame)
        {
            byte verFlags = unwindInfo[0];
            byte version  = (byte)(verFlags & 0x07);
            byte flags    = (byte)((verFlags >> 3) & 0x1F);
            byte prologSize  = unwindInfo[1];
            byte countOfCodes = unwindInfo[2];
            byte frameRegInfo = unwindInfo[3];
            int  frameReg    = frameRegInfo & 0x0F;
            int  frameOffset = (frameRegInfo >> 4) & 0x0F;   // × 16 = bytes

            // Determine how far through prolog we are. If RIP - BeginAddress
            // < prologSize, we're mid-prolog and must SKIP codes whose
            // OffsetInProlog > current offset (those slots haven't been
            // executed yet). Otherwise we're in body/epilog — apply all.
            ulong rva = controlPc - (ulong)image;
            int progress = (int)((long)rva - (long)functionEntry->BeginAddress);
            bool midProlog = (progress >= 0) && (progress < prologSize);

            ushort* codes = (ushort*)(unwindInfo + 4);
            int i = 0;
            while (i < countOfCodes)
            {
                ushort raw = codes[i];
                int codeOffsetInProlog = raw & 0xFF;
                int op   = (raw >> 8) & 0x0F;
                int info = (raw >> 12) & 0x0F;

                int slotsConsumed;
                if (midProlog && codeOffsetInProlog > progress)
                {
                    // This prolog step hadn't been executed yet — skip it.
                    slotsConsumed = SlotCount(op, info);
                }
                else
                {
                    slotsConsumed = ApplyCode(op, info, codes + i, context, frameReg, frameOffset);
                }
                if (slotsConsumed < 0)
                {
                    Console.Write("[seh-unwind] unknown UNWIND_CODE op=");
                    Console.WriteInt(op);
                    Console.Write(" info=");
                    Console.WriteInt(info);
                    Console.WriteLine("");
                    return null;
                }
                i += slotsConsumed;
            }

            // Handle chained unwind info.
            if ((flags & UnwindFlags.UNW_FLAG_CHAININFO) != 0)
            {
                // After the codes (rounded up to DWORD), a 12-byte
                // RUNTIME_FUNCTION points at the parent fragment.
                int unwindSize = 4 + 2 * countOfCodes;
                unwindSize = (unwindSize + 3) & ~3;
                RuntimeFunction* parent = (RuntimeFunction*)(unwindInfo + unwindSize);
                byte* parentUnwindInfo = image + parent->UnwindInfoAddress;
                // Walk parent fragment too. Note: chained fragments don't
                // have their own handler; if parent has one, we want that.
                return ApplyUnwindInfo(handlerType, image, controlPc, parent,
                                       parentUnwindInfo, context, handlerData, establisherFrame);
            }

            // SP now points at saved return address. Pop it into RIP, then
            // advance SP by 8.
            ulong* sp = (ulong*)context->Rsp;
            context->Rip = *sp;
            context->Rsp = (ulong)(sp + 1);

            if (establisherFrame != null)
                *establisherFrame = context->Rsp;

            // If function has matching handler, expose it.
            bool wantHandler = (handlerType & UnwindFlags.UNW_FLAG_EHANDLER) != 0
                            || (handlerType & UnwindFlags.UNW_FLAG_UHANDLER) != 0;
            if (wantHandler && (flags & (UnwindFlags.UNW_FLAG_EHANDLER | UnwindFlags.UNW_FLAG_UHANDLER)) != 0)
            {
                int unwindSize = 4 + 2 * countOfCodes;
                unwindSize = (unwindSize + 3) & ~3;
                uint handlerRva = *(uint*)(unwindInfo + unwindSize);
                if (handlerData != null) *handlerData = unwindInfo + unwindSize + 4;
                return image + handlerRva;
            }

            return null;
        }

        // Apply one unwind code. Returns number of slots consumed (1 / 2 / 3),
        // or -1 if opcode unknown.
        //
        // Per-opcode behavior reverses the prolog action:
        //   PUSH_NONVOL r:    register r ← [Rsp]; Rsp += 8
        //   ALLOC_LARGE size: Rsp += size
        //   ALLOC_SMALL n:    Rsp += (n+1)*8
        //   SET_FPREG:        Rsp = framereg - frameoffset*16
        //   SAVE_NONVOL r,o:  register r ← [Rsp + o*8]
        //   SAVE_XMM128:      (skipped — vol across calls for our use; just consume slots)
        //   PUSH_MACHFRAME:   pops machine frame (interrupt-style)
        //   EPILOG:           Win8+ marker — no register effect, consumed for slot accounting
        private static int ApplyCode(int op, int info, ushort* code, Context* ctx,
                                     int frameReg, int frameOffset)
        {
            switch (op)
            {
                case UnwindOp.UWOP_PUSH_NONVOL:
                {
                    ulong val = *(ulong*)ctx->Rsp;
                    WriteReg(ctx, info, val);
                    ctx->Rsp += 8;
                    return 1;
                }
                case UnwindOp.UWOP_ALLOC_LARGE:
                {
                    if (info == 0)
                    {
                        ushort sizeSlots = code[1];
                        ctx->Rsp += (ulong)sizeSlots * 8u;
                        return 2;
                    }
                    else if (info == 1)
                    {
                        uint sizeLo = code[1];
                        uint sizeHi = code[2];
                        uint size = sizeLo | (sizeHi << 16);
                        ctx->Rsp += size;
                        return 3;
                    }
                    return -1;
                }
                case UnwindOp.UWOP_ALLOC_SMALL:
                    ctx->Rsp += (ulong)((info + 1) * 8);
                    return 1;
                case UnwindOp.UWOP_SET_FPREG:
                {
                    ulong fpVal = ReadReg(ctx, frameReg);
                    ctx->Rsp = fpVal - (ulong)(frameOffset * 16);
                    return 1;
                }
                case UnwindOp.UWOP_SAVE_NONVOL:
                {
                    uint slotOffset = code[1];   // *8 = bytes
                    ulong val = *(ulong*)(ctx->Rsp + slotOffset * 8u);
                    WriteReg(ctx, info, val);
                    return 2;
                }
                case UnwindOp.UWOP_SAVE_NONVOL_FAR:
                {
                    uint lo = code[1];
                    uint hi = code[2];
                    uint offset = lo | (hi << 16);
                    ulong val = *(ulong*)(ctx->Rsp + offset);
                    WriteReg(ctx, info, val);
                    return 3;
                }
                case UnwindOp.UWOP_EPILOG:
                    // Win8+ v2 marker: signals epilog location. No register
                    // effect. Code spans 1 or 2 slots depending on first one
                    // (low bit of info = "first epilog code").
                    return 2;   // conservative: most epilog markers are 2 slots
                case UnwindOp.UWOP_SAVE_XMM128:
                    // We don't restore XMM regs (volatile around call). Consume.
                    return 2;
                case UnwindOp.UWOP_SAVE_XMM128_FAR:
                    return 3;
                case UnwindOp.UWOP_PUSH_MACHFRAME:
                    // Pops an interrupt/exception machine frame: 5 or 6 qwords
                    // (with or without error code). info=0 → no error code (5
                    // qwords: RIP, CS, EFlags, RSP, SS); info=1 → +error code.
                    // We don't currently need this for CoreCLR's standard
                    // call frames. Skip with diagnostic.
                    return 1;
                default:
                    return -1;
            }
        }

        // Slot count for skipping mid-prolog codes that haven't been reached.
        private static int SlotCount(int op, int info)
        {
            switch (op)
            {
                case UnwindOp.UWOP_PUSH_NONVOL:     return 1;
                case UnwindOp.UWOP_ALLOC_LARGE:     return info == 0 ? 2 : 3;
                case UnwindOp.UWOP_ALLOC_SMALL:     return 1;
                case UnwindOp.UWOP_SET_FPREG:       return 1;
                case UnwindOp.UWOP_SAVE_NONVOL:     return 2;
                case UnwindOp.UWOP_SAVE_NONVOL_FAR: return 3;
                case UnwindOp.UWOP_EPILOG:          return 2;
                case UnwindOp.UWOP_SAVE_XMM128:     return 2;
                case UnwindOp.UWOP_SAVE_XMM128_FAR: return 3;
                case UnwindOp.UWOP_PUSH_MACHFRAME:  return 1;
                default: return 1;
            }
        }

        private static void WriteReg(Context* ctx, int regId, ulong val)
        {
            switch (regId)
            {
                case 0:  ctx->Rax = val; break;
                case 1:  ctx->Rcx = val; break;
                case 2:  ctx->Rdx = val; break;
                case 3:  ctx->Rbx = val; break;
                case 4:  ctx->Rsp = val; break;
                case 5:  ctx->Rbp = val; break;
                case 6:  ctx->Rsi = val; break;
                case 7:  ctx->Rdi = val; break;
                case 8:  ctx->R8  = val; break;
                case 9:  ctx->R9  = val; break;
                case 10: ctx->R10 = val; break;
                case 11: ctx->R11 = val; break;
                case 12: ctx->R12 = val; break;
                case 13: ctx->R13 = val; break;
                case 14: ctx->R14 = val; break;
                case 15: ctx->R15 = val; break;
            }
        }

        private static ulong ReadReg(Context* ctx, int regId)
        {
            switch (regId)
            {
                case 0:  return ctx->Rax;
                case 1:  return ctx->Rcx;
                case 2:  return ctx->Rdx;
                case 3:  return ctx->Rbx;
                case 4:  return ctx->Rsp;
                case 5:  return ctx->Rbp;
                case 6:  return ctx->Rsi;
                case 7:  return ctx->Rdi;
                case 8:  return ctx->R8;
                case 9:  return ctx->R9;
                case 10: return ctx->R10;
                case 11: return ctx->R11;
                case 12: return ctx->R12;
                case 13: return ctx->R13;
                case 14: return ctx->R14;
                case 15: return ctx->R15;
                default: return 0;
            }
        }
    }
}
