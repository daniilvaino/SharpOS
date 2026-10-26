// Ported from dotnet/runtime release/8.0 (MIT):
//   src/libraries/System.Private.CoreLib/src/System/Runtime/CompilerServices/StrongBox.cs
// Verbatim minus doc comments.

using System.Diagnostics.CodeAnalysis;

namespace System.Runtime.CompilerServices
{
    public class StrongBox<T> : IStrongBox
    {
        [MaybeNull] public T Value = default!;

        public StrongBox()
        {
        }

        public StrongBox(T value)
        {
            Value = value;
        }

        object? IStrongBox.Value
        {
            get => Value;
            set => Value = (T)value!;
        }
    }

    public interface IStrongBox
    {
        object? Value { get; set; }
    }
}
