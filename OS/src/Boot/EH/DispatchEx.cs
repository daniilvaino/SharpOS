using SharpOS.Std.NoRuntime;

namespace OS.Boot.EH
{
    // Phase 1 step 5.4+ — managed exception dispatcher.
    //
    // FindFirstPassHandler walks frames upward starting from the
    // iterator's current ControlPC; for each frame's EH clauses that
    // cover the current code offset, checks if a Typed catch matches
    // the thrown exception type (via class hierarchy walk).
    //
    // First-pass semantics (no unwinding, no funclet calls): identify
    // WHICH frame and clause WILL handle the exception. Returns the
    // information needed by RhpCallCatchFunclet (to be wired in step
    // 5.5/5.6).
    //
    // Filter clauses are skipped в 5.4 — they need RhpCallFilterFunclet
    // shellcode (sub-step 5.5).
    //
    // Reference (stock):
    //   gc-experiment/dotnet-runtime/src/coreclr/nativeaot/Runtime.Base/src/System/Runtime/ExceptionHandling.cs:738-817
    internal static unsafe class DispatchEx
    {
        public struct FirstPassResult
        {
            public bool Found;
            public byte* HandlerAddress;
            public uint IdxCurClause;
            public uint TryRegionIdx;       // index of selected clause within frame
            public int FramesWalked;
            public byte* MethodStart;
            public uint CodeOffset;
        }

        // Walks frames from iter's current state upward, looking for a
        // Typed clause matching `exceptionType`. On success populates
        // FirstPassResult and the iterator is positioned at the
        // catching frame. On failure (no match) returns Found=false and
        // iterator is exhausted.
        //
        // `startIdx` (default = MaxTryRegionIdx = no skip) — for rethrow,
        // pass prev ExInfo's IdxCurClause so the FIRST frame's clauses
        // 0..startIdx (inclusive) are skipped — prevents the just-ran
        // catch from re-catching its own rethrow.
        public static FirstPassResult FindFirstPassHandler(
            GcMethodTable* exceptionType,
            StackFrameIterator* iter,
            uint startIdx = ExInfo.MaxTryRegionIdx)
        {
            FirstPassResult result = default;
            const int MaxFrames = 100;
            bool isFirstFrame = true;

            while (result.FramesWalked < MaxFrames)
            {
                bool ehOk = CoffEhDecoder.EhEnumInit((byte*)iter->ControlPC,
                    out CoffEhDecoder.EHEnum enumState,
                    out byte* methodStart);

                OS.Hal.Log.Begin(OS.Hal.LogLevel.Info);
                OS.Hal.Console.Write("    fp[");
                OS.Hal.Console.WriteUIntRaw((uint)result.FramesWalked);
                OS.Hal.Console.Write("]: PC=0x");
                OS.Hal.Console.WriteHexRaw(iter->ControlPC, 16);
                OS.Hal.Console.Write(" ehInit=");
                OS.Hal.Console.Write(ehOk ? "Y" : "N");
                if (ehOk)
                {
                    OS.Hal.Console.Write(" methodStart=0x");
                    OS.Hal.Console.WriteHexRaw((ulong)(nuint)methodStart, 16);
                }
                OS.Hal.Log.EndLine();

                if (ehOk)
                {
                    uint codeOffset = (uint)((nint)iter->ControlPC - (nint)methodStart);
                    uint clauseIdx = 0;

                    while (CoffEhDecoder.EhEnumNext(ref enumState,
                        out CoffEhDecoder.RhEHClause clause))
                    {
                        OS.Hal.Log.Begin(OS.Hal.LogLevel.Info);
                        OS.Hal.Console.Write("      clause[");
                        OS.Hal.Console.WriteUIntRaw(clauseIdx);
                        OS.Hal.Console.Write("] kind=");
                        OS.Hal.Console.WriteUIntRaw((uint)clause.Kind);
                        OS.Hal.Console.Write(" try=[0x");
                        OS.Hal.Console.WriteHexRaw(clause.TryStartOffset, 8);
                        OS.Hal.Console.Write("..0x");
                        OS.Hal.Console.WriteHexRaw(clause.TryEndOffset, 8);
                        OS.Hal.Console.Write(") off=0x");
                        OS.Hal.Console.WriteHexRaw(codeOffset, 8);
                        OS.Hal.Console.Write(" type=0x");
                        OS.Hal.Console.WriteHexRaw((ulong)(nuint)clause.TargetTypeRaw, 16);
                        OS.Hal.Log.EndLine();

                        // Rethrow skip: на first frame skip clauses
                        // up to и включая startIdx. Subsequent frames
                        // — full search.
                        if (isFirstFrame && startIdx != ExInfo.MaxTryRegionIdx
                            && clauseIdx <= startIdx)
                        {
                            clauseIdx++;
                            continue;
                        }

                        // Filter clauses — skipped в 5.4. Need
                        // RhpCallFilterFunclet (5.5+).
                        if (clause.Kind == CoffEhDecoder.ClauseKind.Filter)
                        {
                            clauseIdx++;
                            continue;
                        }

                        // Typed: check IP coverage + class hierarchy match.
                        if (clause.Kind == CoffEhDecoder.ClauseKind.Typed
                            && codeOffset >= clause.TryStartOffset
                            && codeOffset < clause.TryEndOffset)
                        {
                            GcMethodTable* clauseType = (GcMethodTable*)clause.TargetTypeRaw;
                            if (IsAssignableFromClass(clauseType, exceptionType))
                            {
                                result.Found = true;
                                result.HandlerAddress = clause.HandlerAddress;
                                result.IdxCurClause = clauseIdx;
                                result.TryRegionIdx = clauseIdx;
                                result.MethodStart = methodStart;
                                result.CodeOffset = codeOffset;
                                return result;
                            }
                        }

                        // Fault clauses don't match catches — they
                        // run during second pass (5.7+).
                        clauseIdx++;
                    }
                }

                // No match on this frame — walk up.
                if (!StackFrameIteratorOps.Next(iter))
                    break;
                result.FramesWalked++;
                isFirstFrame = false;
            }

            return result;   // Found = false
        }

        // Top-level dispatcher. Called by RhThrowEx (managed seam after
        // RhpThrowEx shellcode built ExInfo + PAL). Walks frames upward,
        // finds typed catch handler, transfers control via RhpCallCatchFunclet.
        //
        // Stock NativeAOT does two-pass dispatch: pass 1 finds handler,
        // pass 2 runs finallys + invokes catch funclet. For 5.6 we do
        // single-pass (no finallys yet — second pass added in 5.7).
        //
        // Does NOT return on success — RhpCallCatchFunclet does non-local
        // transfer through `mov rsp; jmp rax` to parent's continuation IP.
        // Returns only on unhandled exception (halts caller).
        public static void Dispatch(byte* exceptionPtr, ExInfo* exInfo)
        {
            // Rethrow path (Phase 1 step 6): Kind has Rethrow bit set.
            // Stock NativeAOT keeps the StackFrameIterator positioned at
            // the catch's establisher frame after first-pass succeeds.
            // For rethrow we COPY prev->FrameIter (still at catch frame)
            // into our own FrameIter и применяем startIdx skip = prev's
            // IdxCurClause так что catch который только что отработал
            // не зацепится снова. Walking up from the catch frame finds
            // the next enclosing catch (outer try in our L9 test).
            //
            // Re-initing from prev->ExContext (= original throw site)
            // would put iter at the throwing frame — but our isFirstFrame
            // skip would apply to the WRONG frame (throw site, not catch
            // site), so inner catch would re-catch its own rethrow и
            // зациклит — stack overflow → #GP с non-canonical RIP.
            //
            // Normal throw: init from own ExContext, no skip.
            uint startIdx = ExInfo.MaxTryRegionIdx;
            bool isRethrow = (exInfo->Kind & ExInfo.KindRethrow) != 0;
            bool reusedIter = false;

            OS.Hal.Log.Begin(OS.Hal.LogLevel.Info);
            OS.Hal.Console.Write("Dispatch: kind=0x");
            OS.Hal.Console.WriteHexRaw(exInfo->Kind, 2);
            OS.Hal.Console.Write(" exInfo=0x");
            OS.Hal.Console.WriteHexRaw((ulong)(nuint)exInfo, 16);
            OS.Hal.Console.Write(" prevExInfo=0x");
            OS.Hal.Console.WriteHexRaw((ulong)(nuint)exInfo->PrevExInfo, 16);
            OS.Hal.Log.EndLine();

            if (isRethrow)
            {
                ExInfo* prev = exInfo->PrevExInfo;
                if (prev != null)
                {
                    // Source exception from prev->Exception. Stock NativeAOT
                    // does this в RhRethrow: `rethrownException = activeExInfo.ThrownException`.
                    // ILC's `rethrow` IL does NOT pass the exception в RCX —
                    // RhpRethrow shellcode receives whatever garbage RCX held
                    // before the call. Using that garbage causes IsAssignable
                    // to deref a non-canonical MT and #GP.
                    exceptionPtr = (byte*)(nuint)prev->Exception;

                    OS.Hal.Log.Begin(OS.Hal.LogLevel.Info);
                    OS.Hal.Console.Write("  rethrow: prev->IdxCurClause=");
                    OS.Hal.Console.WriteUIntRaw(prev->IdxCurClause);
                    OS.Hal.Console.Write(" prev->Exception=0x");
                    OS.Hal.Console.WriteHexRaw((ulong)(nuint)exceptionPtr, 16);
                    OS.Hal.Console.Write(" prev->FrameIter.ControlPC=0x");
                    OS.Hal.Console.WriteHexRaw(prev->FrameIter.ControlPC, 16);
                    OS.Hal.Console.Write(" prev->FrameIter.SP=0x");
                    OS.Hal.Console.WriteHexRaw(prev->FrameIter.RegDisplay.SP, 16);
                    OS.Hal.Log.EndLine();

                    exInfo->FrameIter = prev->FrameIter;
                    startIdx = prev->IdxCurClause;
                    reusedIter = true;
                }
                else
                {
                    OS.Hal.Log.Write(OS.Hal.LogLevel.Warn, "  rethrow but prev=null — fallback to own ExContext");
                }
            }

            if (!reusedIter)
            {
                StackFrameIteratorOps.Init(&exInfo->FrameIter, exInfo->ExContext);
            }

            OS.Hal.Log.Begin(OS.Hal.LogLevel.Info);
            OS.Hal.Console.Write("  iter ready: ControlPC=0x");
            OS.Hal.Console.WriteHexRaw(exInfo->FrameIter.ControlPC, 16);
            OS.Hal.Console.Write(" SP=0x");
            OS.Hal.Console.WriteHexRaw(exInfo->FrameIter.RegDisplay.SP, 16);
            OS.Hal.Console.Write(" startIdx=0x");
            OS.Hal.Console.WriteHexRaw(startIdx, 8);
            OS.Hal.Console.Write(reusedIter ? " (reused prev iter)" : " (init from ExContext)");
            OS.Hal.Log.EndLine();

            // Resolve exception type from object header (first 8 bytes).
            GcMethodTable* exType = null;
            if (exceptionPtr != null)
                exType = *(GcMethodTable**)exceptionPtr;

            // First-pass: find catch handler.
            FirstPassResult fp = FindFirstPassHandler(exType, &exInfo->FrameIter, startIdx);

            OS.Hal.Log.Begin(OS.Hal.LogLevel.Info);
            OS.Hal.Console.Write("  fp.Found=");
            OS.Hal.Console.Write(fp.Found ? "Y" : "N");
            OS.Hal.Console.Write(" handler=0x");
            OS.Hal.Console.WriteHexRaw((ulong)(nuint)fp.HandlerAddress, 16);
            OS.Hal.Console.Write(" idxCurClause=");
            OS.Hal.Console.WriteUIntRaw(fp.IdxCurClause);
            OS.Hal.Console.Write(" framesWalked=");
            OS.Hal.Console.WriteUIntRaw((uint)fp.FramesWalked);
            OS.Hal.Log.EndLine();
            if (!fp.Found)
            {
                // Unhandled. In 5.6 we just halt; full plumbing для
                // OnUnhandledException + FailFast comes later.
                OS.Hal.Console.Write("\r\n*** unhandled exception (no matching catch) ***\r\n");
                while (true) { }
            }

            uint catchingTryRegionIdx = fp.IdxCurClause;

            // Update ExInfo state перед catch.
            exInfo->IdxCurClause = fp.IdxCurClause;
            exInfo->Exception = (ulong)(nuint)exceptionPtr;
            exInfo->PassNumber = 2;

            // Phase 1 step 7 — second pass на catch frame. Enumerate
            // clauses на TEKUSCHEM iter (где FFPH остановился — это catch's
            // establisher frame). Invoke fault/finally clauses с curIdx <
            // catchingTryRegionIdx, codeOffset в TRY range. Iter NOT
            // walked through other frames — funclet-aware multi-frame
            // walking — это step 11 territory.
            //
            // Single-frame test L10 (throw + finally + catch в одном методе)
            // covered by this; multi-frame finally clauses fire only когда
            // SFI gets funclet-skip support.
            uint startSecondPassIdx = isRethrow ? startIdx : ExInfo.MaxTryRegionIdx;
            InvokeFinalliesOnFrame(exInfo, startSecondPassIdx, catchingTryRegionIdx);

            // Hand off к catch funclet via shellcode. RegDisplay is first
            // field of StackFrameIterator — &FrameIter == &RegDisplay.
            delegate* unmanaged<byte*, byte*, RegDisplay*, ExInfo*, void> catchFn =
                (delegate* unmanaged<byte*, byte*, RegDisplay*, ExInfo*, void>)
                CallCatchFuncletStub.GetMethodAddress();

            catchFn(exceptionPtr, fp.HandlerAddress,
                    (RegDisplay*)&exInfo->FrameIter, exInfo);

            // Should not return.
            OS.Hal.Console.Write("\r\n*** RhpCallCatchFunclet returned to Dispatch (BUG) ***\r\n");
            while (true) { }
        }

        // Enumerate clauses на текущем iter frame, invoke finally (kind=Fault)
        // clauses covering codeOffset. Каждый Fault clause — IL finally
        // (в NativeAOT encoding finally компилируется как FAULT).
        //
        // startIdx — для rethrow second pass: skip clauses 0..startIdx как в
        // first pass (catch который только что отработал не должен run свой
        // finally — он по семантике уже выполнился перед catch'ем).
        // idxLimit — partial-pass cap: clauses с curIdx >= idxLimit не run
        // (catch's own finally не должен fire перед самим catch'ем).
        private static void InvokeFinalliesOnFrame(ExInfo* exInfo, uint startIdx, uint idxLimit)
        {
            if (!CoffEhDecoder.EhEnumInit((byte*)exInfo->FrameIter.ControlPC,
                out CoffEhDecoder.EHEnum enumState,
                out byte* methodStart))
                return;

            uint codeOffset = (uint)((nint)exInfo->FrameIter.ControlPC - (nint)methodStart);
            uint clauseIdx = 0;

            while (clauseIdx < idxLimit
                && CoffEhDecoder.EhEnumNext(ref enumState,
                    out CoffEhDecoder.RhEHClause clause))
            {
                // Rethrow skip — same logic as first pass.
                if (startIdx != ExInfo.MaxTryRegionIdx && clauseIdx <= startIdx)
                {
                    clauseIdx++;
                    continue;
                }

                if (clause.Kind == CoffEhDecoder.ClauseKind.Fault
                    && codeOffset >= clause.TryStartOffset
                    && codeOffset < clause.TryEndOffset)
                {
                    OS.Hal.Log.Begin(OS.Hal.LogLevel.Info);
                    OS.Hal.Console.Write("    finally[");
                    OS.Hal.Console.WriteUIntRaw(clauseIdx);
                    OS.Hal.Console.Write("]: handler=0x");
                    OS.Hal.Console.WriteHexRaw((ulong)(nuint)clause.HandlerAddress, 16);
                    OS.Hal.Console.Write(" frameSP=0x");
                    OS.Hal.Console.WriteHexRaw(exInfo->FrameIter.RegDisplay.SP, 16);
                    OS.Hal.Log.EndLine();

                    exInfo->IdxCurClause = clauseIdx;
                    delegate* unmanaged<byte*, RegDisplay*, void> finallyFn =
                        (delegate* unmanaged<byte*, RegDisplay*, void>)
                        CallFinallyFuncletStub.GetMethodAddress();
                    finallyFn(clause.HandlerAddress, (RegDisplay*)&exInfo->FrameIter);
                    exInfo->IdxCurClause = ExInfo.MaxTryRegionIdx;
                }
                clauseIdx++;
            }
        }

        // Class assignability — true if `objType` is `targetType` or any
        // class derived from it. Walks the parent chain via GetBaseType.
        // Same algorithm as RhTypeCast_IsInstanceOfClass in
        // GcRuntimeExports but inlined to avoid the object/MT*
        // marshalling overhead.
        private static bool IsAssignableFromClass(GcMethodTable* targetType, GcMethodTable* objType)
        {
            if (targetType == null || objType == null) return false;
            if (objType == targetType) return true;

            const int MaxDepth = 32;
            for (int i = 0; i < MaxDepth; i++)
            {
                objType = objType->GetBaseType();
                if (objType == null) return false;
                if (objType == targetType) return true;
            }
            return false;
        }
    }
}
