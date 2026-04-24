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
        // Factory without static caching. The proper BCL-compat form is a
        // lazy static field (`if (s_default == null) s_default = new ...;
        // return s_default;`). Under our minimal ILC setup ANY
        // reference-typed static field with a lazy-init path tries to
        // invoke System.Runtime.CompilerServices.ClassConstructorRunner's
        // CheckStaticClassConstruction* helpers — and even though we
        // provide a matching type/methods, ILC doesn't link them in
        // (--resilient mode, probably wrong module/signature match).
        // RAX at the crash site is 0x08FFFFFFFFFFFFFF which looks like the
        // "unresolved helper" sentinel. Revisit when we understand the ILC
        // resolution path or move to a newer SDK; one alloc per read is
        // acceptable for now.
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
        // Prefer IEquatable<T>.Equals(T) so value types compare via their
        // own primitive/override body without boxing both operands. The
        // interface dispatch path is backed by our shared-generic resolver
        // (OS/src/Kernel/Memory/InterfaceDispatchResolver.cs) and works end-
        // to-end for shared-generic bodies as of step 31. For types that do
        // not implement IEquatable<T>, fall through to Object.Equals (the
        // virtual on the non-generic Object vtable — cheap path that always
        // works).
        public override bool Equals(T x, T y)
        {
            if (x is IEquatable<T> eq) return eq.Equals(y);
            if (x == null) return y == null;
            if (y == null) return false;
            return x.Equals(y);
        }

        public override int GetHashCode(T obj)
        {
            if (obj == null) return 0;
            return obj.GetHashCode();
        }
    }
}
