// Ported from dotnet/runtime release/8.0 (MIT):
//   string.Intern / IsInterned — src/coreclr/nativeaot/System.Private.CoreLib/src/System/String.Intern.cs
//   string.Create<TState>     — src/libraries/System.Private.CoreLib/src/System/String.cs
// As in NativeAOT, the intern table is a runtime table of its own: literals are
// not in it until someone interns them.
// Cuts:
//   - StringInternTable over Internal.TypeSystem.LockFreeReaderHashtable →
//     ConcurrentDictionary<string, string> (std has it; the lock-free table
//     has no other user here) (marked "SharpOS cut:").
//   - Create: ArgumentNullException.ThrowIfNull spelled out; FastAllocateString
//     is std's StringRuntime one.

using System.Buffers;
using System.Collections.Concurrent;
using System.Threading;

namespace System
{
    public sealed partial class String
    {
        public static string Intern(string str)
        {
            if (str == null)
                throw new ArgumentNullException(nameof(str));

            return InternTable.GetOrAdd(str, str); // SharpOS cut: was InternTable.GetOrCreateValue(str)
        }

        public static string IsInterned(string str)
        {
            if (str == null)
                throw new ArgumentNullException(nameof(str));

            string canonicalString;
            if (!InternTable.TryGetValue(str, out canonicalString))
                return null;
            return canonicalString;
        }

        // SharpOS cut: StringInternTable (a LockFreeReaderHashtable<string, string>) → ConcurrentDictionary.
        private static ConcurrentDictionary<string, string> InternTable
        {
            get
            {
                if (s_lazyInternTable == null)
                {
                    ConcurrentDictionary<string, string> internTable = new ConcurrentDictionary<string, string>();
                    internTable.TryAdd(string.Empty, string.Empty); // SharpOS cut: was internTable.AddOrGetExisting(string.Empty)
                    Interlocked.CompareExchange<ConcurrentDictionary<string, string>?>(ref s_lazyInternTable, internTable, null);
                }
                return s_lazyInternTable;
            }
        }

        private static volatile ConcurrentDictionary<string, string>? s_lazyInternTable;

        public static string Create<TState>(int length, TState state, SpanAction<char, TState> action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action)); // SharpOS: was ArgumentNullException.ThrowIfNull(action)

            if (length <= 0)
            {
                if (length == 0)
                    return Empty;
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            string result = SharpOS.Std.NoRuntime.StringRuntime.FastAllocateString(length); // SharpOS: was FastAllocateString(length)
            action(new Span<char>(ref result.GetRawStringData(), length), state);
            return result;
        }
    }
}
