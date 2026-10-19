// The pipes' [Message] marks the shared BigGraph.cs types for SharpOS's pipe
// catalog (step208). On CoreCLR there are no pipes: an empty attribute of the
// same name lets the same file compile here.
namespace SharpOS.Std.Pipes
{
    [System.AttributeUsage(System.AttributeTargets.Class)]
    public sealed class MessageAttribute : System.Attribute { }
}
