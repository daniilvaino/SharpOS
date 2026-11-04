// The kernel (OS) is built over CoreLib and uses its internals — the GC, the
// runtime helpers, the tier hooks — as the stock runtime's own code does.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("OS")]
