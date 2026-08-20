// Events: the base class and the two delegate shapes everything uses.
//
// Ported from dotnet/runtime v8.0 (MIT) — System/EventArgs.cs and
// System/EventHandler.cs. These are BCL-compatible in full, so they keep the
// canonical System namespace: a library written against the real ones has to
// compile here untouched, which is the whole point of having them.
//
// Cut: EventArgs.Empty is a property rather than a static readonly field. A
// lazily initialised static reference field is the one thing this environment
// cannot do (see docs/nativeaot-nostd-kernel-limits.md §1), and every use of
// Empty is "an argument nobody reads", so a fresh instance costs nothing.

namespace System
{
    public class EventArgs
    {
        public static EventArgs Empty => new EventArgs();

        public EventArgs() { }
    }

    public delegate void EventHandler(object? sender, EventArgs e);

    public delegate void EventHandler<TEventArgs>(object? sender, TEventArgs e);
}
