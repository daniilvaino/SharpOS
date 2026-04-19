// System.Collections.Generic.EqualityComparer<T> — BCL-compat API.
//
// BCL's Default property picks between a Generic comparer (when T implements
// IEquatable<T>) and an Object comparer (falls back to obj.Equals). That
// selection uses reflection which we don't have. We always return the
// Object variant: it works for everything but pays boxing for value types.
// Callers that need speed pass a custom IEqualityComparer<T> explicitly
// (same knob BCL offers via Dictionary / HashSet ctor overloads).
//
// NOTE: ObjectEqualityComparer<T>.Equals goes through `x.Equals(y)` — the
// virtual dispatch we added on System.Object. Reference types get identity
// comparison by default (ReferenceEquals). Value types pay boxing to invoke
// Object.Equals; override on the struct type to short-circuit.

namespace System.Collections.Generic
{
    public abstract class EqualityComparer<T> : IEqualityComparer<T>
    {
        // Factory without static caching. Making this a static lazy field-
        // backed property produces a crashing vtable slot (RAX non-canonical
        // at virtual dispatch site). Root cause TBD — for now the factory
        // version is the workaround, costs one alloc per Default read.
        public static EqualityComparer<T> Default => new DefaultComparer<T>();

        public abstract bool Equals(T x, T y);
        public abstract int GetHashCode(T obj);
    }

    // NOTE: deliberately NOT named `ObjectEqualityComparer<T>` — ILC has
    // intrinsic handling for BCL's `System.Collections.Generic.ObjectEqualityComparer<T>`
    // that tries to devirtualize/rewrite calls through it, and with our
    // minimal BCL surface that rewrite produces a garbage vtable slot
    // (RAX = F000_0000_xxxx_xxxx at the virtual call, #GP). Any other name
    // avoids the ILC special-case and dispatch works normally.
    internal sealed class DefaultComparer<T> : EqualityComparer<T>
    {
        public override bool Equals(T x, T y)
        {
            if (x == null)
                return y == null;
            if (y == null)
                return false;
            return x.Equals(y);
        }

        public override int GetHashCode(T obj)
        {
            if (obj == null) return 0;
            return obj.GetHashCode();
        }
    }
}
