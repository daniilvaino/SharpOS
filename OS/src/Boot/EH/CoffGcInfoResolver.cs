using OS.PAL.SharpOSHost;

namespace OS.Boot.EH
{
    // Step 110 Part 6/7 — given a decoded CoffGcSlot and a frame's saved
    // CPU context, return the pointer value the slot represents.
    //
    // For register slots: reads ctx->Reg(regNum) — value held in that GP
    //   register at the moment the frame was last live (i.e. at the
    //   safepoint / current PC).
    // For stack slots: computes the address relative to ctx->Rsp (or the
    //   FP base for SpBase==FpBase) and dereferences as ulong.
    //
    // Returns the raw pointer value; caller (mark phase) decides whether
    // it's in GcHeap range and trace-worthy. Interior pointers
    // (CoffGcSlotFlags 0x2 bit) point INSIDE objects rather than at MT
    // header — kernel mark walker handles both via its own range check.
    internal static unsafe class CoffGcInfoResolver
    {
        // Read GP register #idx (AMD64 encoding: 0..15 = rax..r15) from
        // a saved CONTEXT block. The Context struct uses explicit field
        // offsets; we mirror that as a small switch for clarity.
        public static ulong ReadGpReg(Context* ctx, int idx)
        {
            switch (idx)
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

        // Resolve a slot to its current pointer value.
        //
        // hdr is needed for SpBase==CallerSp (offset by
        // SizeOfStackOutgoingAndScratchArea above current SP) and for
        // SpBase==FpBase (decoded FP register lives in hdr.StackBaseRegister).
        public static ulong ResolveSlotValue(
            in CoffGcSlot slot,
            Context* ctx,
            in CoffGcInfoHeader hdr)
        {
            if (slot.Kind == 0)
            {
                return ReadGpReg(ctx, slot.RegOrOffset);
            }
            else
            {
                ulong addr = ResolveSlotAddress(in slot, ctx, in hdr);
                if (addr == 0) return 0;
                return *(ulong*)addr;
            }
        }

        // For stack-resident slots, the slot's address (= where the
        // pointer lives, not the pointer itself). Caller dereferences.
        public static ulong ResolveSlotAddress(
            in CoffGcSlot slot,
            Context* ctx,
            in CoffGcInfoHeader hdr)
        {
            if (slot.Kind != 1) return 0;

            ulong baseValue;
            switch (slot.SpBase)
            {
                case CoffGcStackSlotBase.Caller:
                    // Caller's SP sits above current SP by the outgoing/scratch
                    // area. Header carries that exact value (zeroed if slim).
                    baseValue = ctx->Rsp + hdr.SizeOfStackOutgoingAndScratchArea;
                    break;
                case CoffGcStackSlotBase.CurrentSp:
                    baseValue = ctx->Rsp;
                    break;
                case CoffGcStackSlotBase.FpBase:
                    // Header's StackBaseRegister is the *encoded* AMD64 reg
                    // number (0=RBP, 1=RSP per DenormalizeStackBaseRegister
                    // already applied at header decode time).
                    if (hdr.StackBaseRegister == 0xFFFFFFFFu) return 0;
                    baseValue = ReadGpReg(ctx, (int)hdr.StackBaseRegister);
                    break;
                default:
                    return 0;
            }
            return baseValue + (ulong)(long)slot.RegOrOffset;
        }
    }
}
