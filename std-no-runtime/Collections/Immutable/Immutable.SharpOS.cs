// System.Collections.Immutable in std: ImmutableStack<T> (IImmutableStack.cs,
// ImmutableStack.cs, ImmutableStack_1.cs, ImmutableStack_1.Enumerator.cs beside
// this file), ported as they are from dotnet/runtime (.NET 10, the fork
// dotnet-runtime-sharpos, MIT):
//   src/libraries/System.Collections.Immutable/src/System/Collections/Immutable/
// Step232: the highlighting engine of the editor keeps its span stack in one.
// Cut: [DebuggerTypeProxy] (ImmutableEnumerableDebuggerProxy). The rest of the
// library (lists, dictionaries, arrays, builders) is not ported.
//
// What the files need from the library, here: Requires (Validation/Requires.cs,
// the two methods they call) and the one SR key.

using System.Diagnostics.CodeAnalysis;

namespace System.Collections.Immutable
{
    internal static class Requires
    {
        public static void NotNull<T>([NotNull] T value, string? parameterName)
            where T : class // ensures value-types aren't passed to a null checking method
        {
            if (value == null)
            {
                throw new ArgumentNullException(parameterName);
            }
        }

        public static void FailObjectDisposed<TDisposed>(TDisposed disposed)
        {
            throw new ObjectDisposedException(disposed!.GetType().FullName);
        }
    }

    internal static class SR
    {
        internal const string InvalidEmptyOperation = "This operation does not apply to an empty instance.";
    }
}
