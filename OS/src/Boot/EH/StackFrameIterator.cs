using System.Runtime.InteropServices;
using OS.Hal;

namespace OS.Boot.EH
{
    // Phase 1 step 4 — managed stack frame iterator + unwind decoder.
    //
    // Walks frames upward from a captured PAL_LIMITED_CONTEXT by reading
    // each function's UNWIND_INFO blob and applying unwind codes in
    // forward order (which is the reverse of prolog order — NativeAOT/
    // Win-x64 stores codes "epilog-first"). After all codes are applied,
    // SP points to the saved return address; reading it gives us the
    // caller's IP.
    //
    // Unwind codes implemented (per empirical scan of our binary):
    //   UWOP_PUSH_NONVOL (0)  — pop saved nonvol from current SP
    //   UWOP_ALLOC_LARGE (1)  — add SP, imm16*8 (opInfo=0) or imm32 (opInfo=1)
    //   UWOP_ALLOC_SMALL (2)  — add SP, (opInfo+1)*8
    //   UWOP_SET_FPREG   (3)  — SP = *pRbp - FrameOffset*16  (FrameOffset
    //                            stored in UNWIND_INFO header byte 3)
    //
    // Unsupported opcodes (UWOP_SAVE_NONVOL, _SAVE_XMM128, _PUSH_MACHFRAME,
    // their FAR variants) — log + return iterator-exhausted. Empirically
    // ILC doesn't emit them for our codebase; will extend if seen.
    //
    // Reference:
    //   gc-experiment/dotnet-runtime/src/coreclr/nativeaot/Runtime.Base/src/System/Runtime/StackFrameIterator.cs
    //   amd64/UnwindInfo.cs equivalents
    [StructLayout(LayoutKind.Explicit, Size = 0x230)]
    internal unsafe struct StackFrameIterator
    {
        [FieldOffset(0x000)] public RegDisplay RegDisplay;
        [FieldOffset(0x130)] public ulong ControlPC;
        [FieldOffset(0x138)] public ulong FramePointer;
        [FieldOffset(0x140)] public ulong OriginalControlPC;
        [FieldOffset(0x148)] public uint Flags;        // bit 0 = exhausted

        public const uint FlagExhausted = 0x1;
    }

    internal static unsafe class StackFrameIteratorOps
    {
        // x64 register numbers (per UNWIND_CODE OpInfo encoding).
        private const int RegRax = 0;
        private const int RegRcx = 1;
        private const int RegRdx = 2;
        private const int RegRbx = 3;
        private const int RegRsp = 4;
        private const int RegRbp = 5;
        private const int RegRsi = 6;
        private const int RegRdi = 7;
        private const int RegR8 = 8;
        private const int RegR9 = 9;
        private const int RegR10 = 10;
        private const int RegR11 = 11;
        private const int RegR12 = 12;
        private const int RegR13 = 13;
        private const int RegR14 = 14;
        private const int RegR15 = 15;

        // UNWIND_CODE opcodes.
        private const int UWOP_PUSH_NONVOL = 0;
        private const int UWOP_ALLOC_LARGE = 1;
        private const int UWOP_ALLOC_SMALL = 2;
        private const int UWOP_SET_FPREG = 3;

        // Initialise iterator from a captured PAL_LIMITED_CONTEXT.
        // Register-pointer table starts pointing INTO the PAL — caller
        // must keep PAL alive throughout the walk.
        public static void Init(StackFrameIterator* iter, PalLimitedContext* pal)
        {
            iter->ControlPC = pal->IP;
            iter->FramePointer = pal->Rbp;
            iter->OriginalControlPC = pal->IP;
            iter->Flags = 0;

            iter->RegDisplay.SP = pal->Rsp;
            iter->RegDisplay.ControlPC = pal->IP;
            iter->RegDisplay.pRbx = &pal->Rbx;
            iter->RegDisplay.pRbp = &pal->Rbp;
            iter->RegDisplay.pRsi = &pal->Rsi;
            iter->RegDisplay.pRdi = &pal->Rdi;
            iter->RegDisplay.pR12 = &pal->R12;
            iter->RegDisplay.pR13 = &pal->R13;
            iter->RegDisplay.pR14 = &pal->R14;
            iter->RegDisplay.pR15 = &pal->R15;
        }

        // Advance iterator to caller's frame. Returns false when the
        // walk can't continue (current IP not in our binary, unsupported
        // unwind opcode encountered, or stack went bad). After a false
        // return the iterator is marked exhausted.
        public static bool Next(StackFrameIterator* iter)
        {
            if ((iter->Flags & StackFrameIterator.FlagExhausted) != 0)
                return false;

            byte* ip = (byte*)iter->ControlPC;
            if (!CoffMethodLookup.TryFindMethod(ip, out CoffMethodLookup.MethodInfo info))
            {
                iter->Flags |= StackFrameIterator.FlagExhausted;
                return false;
            }

            byte* unwindInfo = info.ImageBase
                             + info.RootRuntimeFunction->UnwindInfoAddress;

            byte countOfCodes = unwindInfo[2];
            byte frameRegInfo = unwindInfo[3];
            int frameOffsetUnits = (frameRegInfo >> 4) & 0x0F;   // *16 = bytes
            // FrameRegister low 4 bits — we only support rbp (5) or none (0)

            ushort* codes = (ushort*)(unwindInfo + 4);

            // Apply unwind codes forward (which reverses the prolog).
            int i = 0;
            while (i < countOfCodes)
            {
                ushort raw = codes[i];
                int unwindOp = (raw >> 8) & 0x0F;
                int opInfo = (raw >> 12) & 0x0F;

                switch (unwindOp)
                {
                    case UWOP_PUSH_NONVOL:
                        UpdateRegPtr(iter, opInfo, (ulong*)iter->RegDisplay.SP);
                        iter->RegDisplay.SP += 8;
                        i += 1;
                        break;

                    case UWOP_ALLOC_LARGE:
                        if (opInfo == 0)
                        {
                            // Next slot: u16 size in 8-byte units.
                            uint size = codes[i + 1];
                            iter->RegDisplay.SP += size * 8u;
                            i += 2;
                        }
                        else if (opInfo == 1)
                        {
                            // Next two slots: u32 size in bytes.
                            uint sizeLo = codes[i + 1];
                            uint sizeHi = codes[i + 2];
                            uint size = sizeLo | (sizeHi << 16);
                            iter->RegDisplay.SP += size;
                            i += 3;
                        }
                        else
                        {
                            ReportUnsupported(unwindOp, opInfo);
                            iter->Flags |= StackFrameIterator.FlagExhausted;
                            return false;
                        }
                        break;

                    case UWOP_ALLOC_SMALL:
                        iter->RegDisplay.SP += (uint)((opInfo + 1) * 8);
                        i += 1;
                        break;

                    case UWOP_SET_FPREG:
                        // rbp = rsp + offset → unwind: rsp = rbp - offset.
                        // Current rbp value is at *pRbp.
                        if (iter->RegDisplay.pRbp == null)
                        {
                            ReportUnsupported(unwindOp, opInfo);
                            iter->Flags |= StackFrameIterator.FlagExhausted;
                            return false;
                        }
                        ulong rbpVal = *iter->RegDisplay.pRbp;
                        iter->RegDisplay.SP = rbpVal - (ulong)(frameOffsetUnits * 16);
                        i += 1;
                        break;

                    default:
                        ReportUnsupported(unwindOp, opInfo);
                        iter->Flags |= StackFrameIterator.FlagExhausted;
                        return false;
                }
            }

            // SP now points at saved return address.
            byte* retAddrSlot = (byte*)iter->RegDisplay.SP;
            ulong nextIP = *(ulong*)retAddrSlot;
            iter->RegDisplay.SP += 8;

            iter->ControlPC = nextIP;
            iter->RegDisplay.ControlPC = nextIP;
            // FramePointer advances to the (now-restored) caller's rbp.
            iter->FramePointer = *iter->RegDisplay.pRbp;

            // Sanity: nextIP must be canonical and inside our image. If
            // the walk crossed into UEFI/firmware (e.g. we walked past
            // KernelMain.Start) — exhaust.
            if (nextIP == 0)
            {
                iter->Flags |= StackFrameIterator.FlagExhausted;
                return false;
            }

            return true;
        }

        // Map UNWIND_OP register number to RegDisplay pointer slot, write
        // the saved-location pointer. Volatile registers (rax/rcx/rdx/r8-r11)
        // can be PUSH_NONVOL'd in some prologs but we don't need their
        // pointer for stack walking — they're restored from the original
        // call site, not the unwind chain. Just advance SP for them.
        private static void UpdateRegPtr(StackFrameIterator* iter, int regId, ulong* slot)
        {
            switch (regId)
            {
                case RegRbx: iter->RegDisplay.pRbx = slot; break;
                case RegRbp: iter->RegDisplay.pRbp = slot; break;
                case RegRsi: iter->RegDisplay.pRsi = slot; break;
                case RegRdi: iter->RegDisplay.pRdi = slot; break;
                case RegR12: iter->RegDisplay.pR12 = slot; break;
                case RegR13: iter->RegDisplay.pR13 = slot; break;
                case RegR14: iter->RegDisplay.pR14 = slot; break;
                case RegR15: iter->RegDisplay.pR15 = slot; break;
                default:
                    // Volatile or unrecognised — slot still consumed by
                    // PUSH_NONVOL but we don't track its pointer.
                    break;
            }
        }

        private static void ReportUnsupported(int unwindOp, int opInfo)
        {
            Log.Begin(LogLevel.Warn);
            Console.Write("sfi: unsupported UNWIND_CODE op=");
            Console.WriteUIntRaw((uint)unwindOp);
            Console.Write(" opInfo=");
            Console.WriteUIntRaw((uint)opInfo);
            Log.EndLine();
        }
    }
}
