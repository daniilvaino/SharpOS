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
        public static FirstPassResult FindFirstPassHandler(
            GcMethodTable* exceptionType,
            StackFrameIterator* iter)
        {
            FirstPassResult result = default;
            const int MaxFrames = 100;

            while (result.FramesWalked < MaxFrames)
            {
                if (CoffEhDecoder.EhEnumInit((byte*)iter->ControlPC,
                    out CoffEhDecoder.EHEnum enumState,
                    out byte* methodStart))
                {
                    uint codeOffset = (uint)((nint)iter->ControlPC - (nint)methodStart);
                    uint clauseIdx = 0;

                    while (CoffEhDecoder.EhEnumNext(ref enumState,
                        out CoffEhDecoder.RhEHClause clause))
                    {
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
            }

            return result;   // Found = false
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
