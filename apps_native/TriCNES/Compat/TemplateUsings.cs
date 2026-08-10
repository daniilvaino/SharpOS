// Namespace markers so the upstream sources compile UNCHANGED.
//
// Two of them start with the default Visual Studio file header:
//
//     using System.Linq;
//     using System.Threading.Tasks;
//
// Neither is used — checked, not assumed: no LINQ operator and no Task, async
// or await appears anywhere in the emulator. But a `using` for a namespace
// that does not exist is a compile error, so the namespaces have to be real
// even though nothing lives in them.
//
// The alternative was deleting two lines from a third-party tree we want to
// keep pulling from upstream. Two empty namespaces are the cheaper price.
//
// If either namespace ever gains a real implementation in std, these markers
// become harmless duplicates of an empty namespace and can be deleted.

namespace System.Linq
{
    internal static class NamespaceMarker { }
}

namespace System.Threading.Tasks
{
    internal static class NamespaceMarker { }
}
