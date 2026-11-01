// System.Runtime.Versioning.RequiresPreviewFeaturesAttribute — ported from
// dotnet/runtime v8.0, System.Private.CoreLib (MIT), as is. Needed by the
// X86Base intrinsics (DivRem is a preview API there).

namespace System.Runtime.Versioning
{
    [AttributeUsage(AttributeTargets.Assembly |
                    AttributeTargets.Module |
                    AttributeTargets.Class |
                    AttributeTargets.Interface |
                    AttributeTargets.Delegate |
                    AttributeTargets.Struct |
                    AttributeTargets.Enum |
                    AttributeTargets.Constructor |
                    AttributeTargets.Method |
                    AttributeTargets.Property |
                    AttributeTargets.Field |
                    AttributeTargets.Event, Inherited = false)]
    public sealed class RequiresPreviewFeaturesAttribute : Attribute
    {
        public RequiresPreviewFeaturesAttribute() { }

        public RequiresPreviewFeaturesAttribute(string? message) => Message = message;

        public string? Message { get; }

        public string? Url { get; set; }
    }
}
