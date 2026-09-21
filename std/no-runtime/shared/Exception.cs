// System.Exception — full BCL-compatible layout for SharpOS.
//
// Step 1 of the Phase 1 try/catch roadmap. Replaces the minimal stub that
// lived in Threading.cs (only `_message` field) with the complete 88-byte
// shape NativeAOT runtime expects.
//
// Field set ported from
//   gc-experiment/dotnet-runtime/src/coreclr/nativeaot/System.Private.CoreLib/src/System/Exception.NativeAot.cs
// in the same declaration order. ILC determines actual layout (LayoutKind
// is Auto for classes), but most members are reference-typed and their
// offsets line up with stock NativeAOT for the fields the runtime walks.
// Bit-exact layout will matter when we wire AppendExceptionStackFrame in
// step 11 — at which point we either add [StructLayout(Sequential)] or
// audit ILC layout output. For step 1 only Message round-trip is needed.
//
// Behaviour cuts vs full BCL:
//   - No serialization (no SerializationInfo / GetObjectData).
//   - StackTrace formats the recorded addresses (step177); method names
//     wait on the metadata reader, so a frame reads as an address and its
//     offset in the image that owns it.
//   - HelpLink / Source getters always return null (fields exist for
//     layout / future use but ToString never reads them).
//   - Data getter exposed as object-typed (placeholder for IDictionary
//     once SortedList<,> can host an actual ListDictionary).

namespace System
{
    public class Exception
    {
        // Field declaration order MUST match BCL Exception.NativeAot.cs.
        // ILC will pack reference fields together regardless, but keeping
        // declaration order makes future [StructLayout(Sequential)] swap
        // a one-line change.
        internal string _message;
        private object _data;                  // placeholder for IDictionary
        private Exception _innerException;
        private string _helpURL;
        private string _source;
        private int _HResult;
        private string _stackTraceString;
        private string _remoteStackTraceString;
        private IntPtr[] _corDbgStackTrace;
        private int _idxFirstFreeStackTraceEntry;

        // Frames the buffer had no room for. Not a BCL field, and added
        // anyway: without it a trace that stopped at the cap and a trace that
        // reached the bottom of the stack are the same text, and there is no
        // way to tell a complete answer from a cut-off one.
        private int _droppedStackFrames;

        // Standard COR_E_EXCEPTION HResult — what BCL Exception sets in
        // its parameterless constructor.
        private const int COR_E_EXCEPTION = unchecked((int)0x80131500);

        // The trace buffer is reserved HERE, in the constructor, and not
        // lazily at the first recorded frame.
        //
        // Not for speed - for ownership. Applications share the kernel's EH
        // engine (step140 handoff), so AppendStackFrame runs as kernel code
        // even for an application's exception. Allocating there put the
        // buffer in the KERNEL heap while the exception object it belongs to
        // lived in the APPLICATION heap: unreachable from the application's
        // collector, which never sees it, and from the kernel's, which has no
        // root to it. The kernel's sweep is free to reclaim a buffer the
        // application still points at.
        //
        // `newobj` allocates the object and runs the constructor in the same
        // image, so reserving here puts the buffer on the same heap as the
        // object by construction, whoever creates it.
        public Exception()
        {
            _HResult = COR_E_EXCEPTION;
            ReserveStackTrace();
        }

        public Exception(string message)
        {
            _HResult = COR_E_EXCEPTION;
            _message = message;
            ReserveStackTrace();
        }

        public Exception(string message, Exception innerException)
        {
            _HResult = COR_E_EXCEPTION;
            _message = message;
            _innerException = innerException;
            ReserveStackTrace();
        }

        public virtual string Message
        {
            get
            {
                // BCL fallback: if no message, returns
                // "Exception of type 'X' was thrown." We don't have
                // GetType().ToString() wired through reflection; just
                // return null in that case until step 11 expands.
                return _message;
            }
        }

        public Exception InnerException => _innerException;

        public int HResult
        {
            get => _HResult;
            protected set => _HResult = value;
        }

        // Built from the recorded addresses the first time anyone asks, and
        // kept afterwards, as the BCL does.
        //
        // What stood here until step177 was the literal "[trace]": a marker
        // that said "frames were recorded" and nothing about which. Every
        // report that printed it - the census, an app's catch block, a probe
        // checking that a rethrow keeps its trace - printed seven characters
        // and called it a stack trace. The addresses were there the whole
        // time, in _corDbgStackTrace, written by the first pass.
        public virtual string StackTrace
        {
            get
            {
                if (_stackTraceString != null) return _stackTraceString;
                if (_idxFirstFreeStackTraceEntry == 0) return null;
                _stackTraceString = FormatStackTrace();
                return _stackTraceString;
            }
        }

        // Address -> base of the image that owns it, or 0 when nothing knows.
        //
        // A hook rather than a call, because this type is compiled into both
        // the kernel and every application, and only the kernel can answer:
        // the question is "which of the loaded images covers this address",
        // and the registry that knows lives there. Null until installed, and
        // then the trace is addresses alone - which is still a trace, and
        // still more than a seven-character marker.
        internal static unsafe delegate*<ulong, ulong> s_resolveImageBase;

        private unsafe string FormatStackTrace()
        {
            System.IntPtr[] frames = _corDbgStackTrace;
            if (frames == null) return null;

            int n = _idxFirstFreeStackTraceEntry;
            if (n > frames.Length) n = frames.Length;
            if (n == 0) return null;

            // "   at 0x0000000000000000 [0x0000000000000000+0x0000000000000000]"
            // plus the line break is 74; the slack is for nothing in
            // particular, and costs one allocation that lives as long as the
            // exception does.
            const int PerFrame = 80;
            char[] buffer = new char[n * PerFrame + PerFrame];
            int at = 0;

            for (int i = 0; i < n; i++)
            {
                ulong ip = (ulong)(long)frames[i];

                // Room for a name is deliberately in front of the address
                // rather than instead of it: when the metadata reader lands
                // the name goes here and the address stays, because a name
                // without an offset cannot be checked against the image.
                Put(buffer, ref at, "   at 0x");
                PutHex(buffer, ref at, ip, 16);

                ulong imageBase = s_resolveImageBase == null ? 0UL : s_resolveImageBase(ip);
                if (imageBase != 0 && ip >= imageBase)
                {
                    Put(buffer, ref at, " [0x");
                    PutHex(buffer, ref at, imageBase, 16);
                    Put(buffer, ref at, "+0x");
                    PutHex(buffer, ref at, ip - imageBase, 8);
                    Put(buffer, ref at, "]");
                }

                buffer[at++] = '\r';
                buffer[at++] = '\n';
            }

            // A truncated trace that does not say so is worse than a short
            // one: it reads as the whole story, and the reader has no way to
            // know the callers were cut off.
            if (_droppedStackFrames != 0)
            {
                Put(buffer, ref at, "   ... ");
                PutDecimal(buffer, ref at, _droppedStackFrames);
                Put(buffer, ref at, " more frames");
                buffer[at++] = '\r';
                buffer[at++] = '\n';
            }

            return new string(buffer, 0, at);
        }

        private static void PutDecimal(char[] buffer, ref int at, int value)
        {
            if (value < 0) { buffer[at++] = '-'; value = -value; }

            int scale = 1;
            for (int probe = value / 10; probe != 0; probe /= 10) scale *= 10;

            while (scale != 0)
            {
                buffer[at++] = (char)('0' + (value / scale) % 10);
                scale /= 10;
            }
        }

        private static void Put(char[] buffer, ref int at, string text)
        {
            for (int i = 0; i < text.Length; i++)
                buffer[at++] = text[i];
        }

        // Fixed width, so the columns line up and a symbolizer can be handed
        // the field as-is. `minDigits` is a minimum, not a truncation: a value
        // wider than that prints wider rather than wrong.
        private static void PutHex(char[] buffer, ref int at, ulong value, int minDigits)
        {
            int digits = 1;
            ulong probe = value;
            while ((probe >>= 4) != 0) digits++;
            if (digits < minDigits) digits = minDigits;

            for (int shift = (digits - 1) * 4; shift >= 0; shift -= 4)
            {
                int nibble = (int)((value >> shift) & 0xF);
                buffer[at++] = (char)(nibble < 10 ? '0' + nibble : 'A' + (nibble - 10));
            }
        }

        public virtual string Source => _source;

        public virtual string HelpLink
        {
            get => _helpURL;
            set => _helpURL = value;
        }

        public object Data => _data;

        // Stack-trace IPs accessor. Returns the entries
        // AppendExceptionStackFrame wrote into _corDbgStackTrace, sized to
        // _idxFirstFreeStackTraceEntry — a COPY, allocated by whoever asks.
        internal IntPtr[] GetStackIPs()
        {
            int n = _idxFirstFreeStackTraceEntry;
            IntPtr[] result = new IntPtr[n];
            if (_corDbgStackTrace != null)
            {
                for (int i = 0; i < n; i++)
                    result[i] = _corDbgStackTrace[i];
            }
            return result;
        }

        // True once AppendExceptionStackFrame has run for this instance.
        // Used by future Message/Source/StackTrace getters that gate on
        // "has been thrown".
        internal bool HasBeenThrown => _idxFirstFreeStackTraceEntry != 0;

        // Phase 1 step 11 — used by DispatchEx to populate the stack trace
        // on the first-pass walk. Each call appends iter.ControlPC to
        // _corDbgStackTrace and increments the index.
        // Sixteen was the depth of the probe that first needed a trace, not
        // a decision. The first pass walks outward from the throw, so the cap
        // keeps the frames nearest it and drops the callers - which is the
        // better half to keep, and still loses the context that says how the
        // failing path was entered at all. Terminal.Gui's paint path is
        // deeper than sixteen on its own.
        //
        // Sixty-four costs 512 bytes on an object that is thrown at most once.
        private const int StackTraceCapacity = 64;

        internal void AppendStackFrame(System.IntPtr ip)
        {
            // Still guarded: an exception can reach here without a buffer if
            // its object was built by something other than a constructor.
            if (_corDbgStackTrace == null)
                _corDbgStackTrace = new System.IntPtr[StackTraceCapacity];

            if (_idxFirstFreeStackTraceEntry >= _corDbgStackTrace.Length)
            {
                // Count what is lost rather than dropping it in silence. The
                // formatter says so at the end of the text.
                //
                // Undercounts past 100 frames: DispatchEx stops the first-pass
                // walk there, so a deeper stack is cut by that limit too and
                // this never learns about the rest. That ceiling is a separate
                // question - it also means a handler below frame 100 is never
                // found - and is not this method's to answer.
                _droppedStackFrames++;
                return;
            }

            _corDbgStackTrace[_idxFirstFreeStackTraceEntry] = ip;
            _idxFirstFreeStackTraceEntry++;

            // Nothing is formatted here. This runs during the first pass,
            // frame by frame, while the exception is still looking for a
            // handler; building a string per frame would allocate on a path
            // that must not, and would throw away the previous one each time.
            // The getter formats once, when asked.
            _stackTraceString = null;
        }

        // For an exception made ahead of time and thrown when memory has run
        // out (GcHeap.OutOfMemory): the buffer AppendStackFrame would
        // allocate at the throw is allocated now, while there is room.
        internal void ReserveStackTrace()
        {
            if (_corDbgStackTrace == null)
                _corDbgStackTrace = new System.IntPtr[StackTraceCapacity];
        }

        // The same preallocated instance is thrown again and again; each
        // throw starts its trace from empty instead of appending to the last.
        internal void ResetStackTrace()
        {
            _idxFirstFreeStackTraceEntry = 0;
            _droppedStackFrames = 0;
            _stackTraceString = null;
        }

        /// <summary>The recorded addresses themselves, not a copy.</summary>
        /// <remarks>
        /// GetStackIPs copies into a fresh array, which is allocated by
        /// whoever asks - so it cannot answer "which heap owns the buffer".
        /// That question has a test, and this is what the test reads.
        /// </remarks>
        internal IntPtr[] StackTraceBuffer => _corDbgStackTrace;

        /// <summary>Frames the buffer had no room for.</summary>
        internal int DroppedStackFrames => _droppedStackFrames;

        public override string ToString()
        {
            // Minimal: just Message, prefixed with type name. Without
            // reflection we can't get the dynamic type name; use the
            // runtime-known field name.
            string msg = Message;
            if (msg == null)
                return "Exception";
            return msg;
        }
    }
}
