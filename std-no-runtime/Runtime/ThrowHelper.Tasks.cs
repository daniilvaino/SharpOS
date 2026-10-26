// Ported from dotnet/runtime release/8.0 (v8.0.27, MIT):
//   src/libraries/System.Private.CoreLib/src/System/ThrowHelper.cs
// The members the ValueTask / ManualResetValueTaskSourceCore port calls,
// bodies verbatim; ExceptionArgument carries only the names they pass.
// The other half of the partial class is Number/ThrowHelper.cs.

using System.Diagnostics.CodeAnalysis;

namespace System
{
    internal static partial class ThrowHelper
    {
        [DoesNotReturn]
        internal static void ThrowArgumentNullException(ExceptionArgument argument)
        {
            throw new ArgumentNullException(GetArgumentName(argument));
        }

        [DoesNotReturn]
        internal static void ThrowInvalidOperationException()
        {
            throw new InvalidOperationException();
        }

        [DoesNotReturn]
        internal static void ThrowUnexpectedStateForKnownCallback(object? state)
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, SR.Argument_UnexpectedStateForKnownCallback);
        }

        private static string GetArgumentName(ExceptionArgument argument)
        {
            switch (argument)
            {
                case ExceptionArgument.task: return "task";
                case ExceptionArgument.source: return "source";
                case ExceptionArgument.continuation: return "continuation";
                case ExceptionArgument.array: return "array";
                case ExceptionArgument.list: return "list";
                case ExceptionArgument.value: return "value";
                case ExceptionArgument.buffer: return "buffer";
                case ExceptionArgument.dictionary: return "dictionary";
                case ExceptionArgument.s: return "s";
                case ExceptionArgument.index: return "index";
                case ExceptionArgument.codePoint: return "codePoint";
                case ExceptionArgument.culture: return "culture";
                case ExceptionArgument.ch: return "ch";
                case ExceptionArgument.input: return "input";
                case ExceptionArgument.format: return "format";
                case ExceptionArgument.factor: return "factor";
                case ExceptionArgument.divisor: return "divisor";
                case ExceptionArgument.values: return "values";
                default: return "";
            }
        }
    }

    internal enum ExceptionArgument
    {
        task,
        source,
        continuation,
        array,          // Collection<T>, Random (step198)
        list,
        value,
        buffer,
        dictionary,
        s,              // CharUnicodeInfo, Rune
        index,
        codePoint,
        culture,
        ch,
        input,
        format,         // DateTime / DateTimeOffset / TimeSpan / DateOnly / TimeOnly
        factor,         // TimeSpan
        divisor,        // TimeSpan
        values,         // MemoryExtensions SearchValues overloads
    }

    internal static partial class SR
    {
        internal const string Argument_UnexpectedStateForKnownCallback = "The state argument was not the expected type.";
    }
}
