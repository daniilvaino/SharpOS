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
//   - StackTrace returns null until step 11 wires _corDbgStackTrace
//     pretty-printing.
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

        // Standard COR_E_EXCEPTION HResult — what BCL Exception sets in
        // its parameterless constructor.
        private const int COR_E_EXCEPTION = unchecked((int)0x80131500);

        public Exception()
        {
            _HResult = COR_E_EXCEPTION;
        }

        public Exception(string message)
        {
            _HResult = COR_E_EXCEPTION;
            _message = message;
        }

        public Exception(string message, Exception innerException)
        {
            _HResult = COR_E_EXCEPTION;
            _message = message;
            _innerException = innerException;
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

        public virtual string StackTrace => _stackTraceString;

        public virtual string Source => _source;

        public virtual string HelpLink
        {
            get => _helpURL;
            set => _helpURL = value;
        }

        public object Data => _data;

        // Stack-trace IPs accessor — used by step 14 (rich stack trace).
        // Returns the IntPtr[] entries the runtime AppendExceptionStackFrame
        // wrote into _corDbgStackTrace, sized to _idxFirstFreeStackTraceEntry.
        // Until step 11 wires real append, this is always empty.
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

        // Phase 1 step 11 — used by DispatchEx to populate stack trace
        // на first-pass walk. Each call appends iter.ControlPC к
        // _corDbgStackTrace и increments index. _stackTraceString is also
        // updated к non-null marker so StackTrace getter returns non-null
        // (proper formatting deferred).
        internal void AppendStackFrame(System.IntPtr ip)
        {
            const int Capacity = 16;
            if (_corDbgStackTrace == null)
                _corDbgStackTrace = new System.IntPtr[Capacity];
            if (_idxFirstFreeStackTraceEntry < _corDbgStackTrace.Length)
            {
                _corDbgStackTrace[_idxFirstFreeStackTraceEntry] = ip;
                _idxFirstFreeStackTraceEntry++;
                _stackTraceString = "[trace]";   // marker — non-null indicates trace populated
            }
        }

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
