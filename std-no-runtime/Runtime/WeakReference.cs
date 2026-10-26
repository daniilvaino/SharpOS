// Ported shape from dotnet/runtime (MIT), src/libraries/System.Private.CoreLib/
// src/System/WeakReference.T.cs — NOT its behaviour, as ConditionalWeakTable
// here: neither collector has weak handles, so the target is held strongly
// and TryGetTarget answers it until SetTarget replaces it. A cache keyed on
// WeakReference keeps its entries; what relies on the target being collected
// (finalization registries) never sees it go. Recorded in
// docs/nativeaot-nostd-kernel-limits.md with ConditionalWeakTable.
//
// Cuts: trackResurrection is accepted and has no meaning (nothing is
// collected); no ISerializable.

using System.Diagnostics.CodeAnalysis;

namespace System
{
    public sealed class WeakReference<T> where T : class?
    {
        private T _target;

        public WeakReference(T target) : this(target, false)
        {
        }

        public WeakReference(T target, bool trackResurrection)
        {
            _ = trackResurrection;
            _target = target;
        }

        public bool TryGetTarget([MaybeNullWhen(false), NotNullWhen(true)] out T target)
        {
            target = _target;
            return target != null;
        }

        public void SetTarget(T target) => _target = target;
    }
}
