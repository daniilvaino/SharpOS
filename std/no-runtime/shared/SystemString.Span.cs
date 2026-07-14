// Span-dependent slice of System.String, split out of SystemString.cs.
//
// ReadOnlySpan<char> drags in Span<T>/Unsafe/SpanHelpers. The minimal
// apps (FetchApp, HelloSharpFs) curate their compile list and do not
// carry the Span types, yet they compile the shared SystemString.cs;
// keeping this ctor there made a Span-free project fail with CS0246.
// It is the ONLY Span-typed member of String and nothing the apps
// compile constructs a string from a span, so partitioning it here —
// included only by projects that also compile Runtime/ReadOnlySpan.cs
// (the kernel) — is correct, not a workaround: each project gets a
// coherent String for the type set it actually has.

namespace System
{
    public sealed unsafe partial class String
    {
        public String(ReadOnlySpan<char> value)
        {
            int n = value.Length;
            if (n == 0)
            {
                _stringLength = 0;
                return;
            }
            _stringLength = n;
            fixed (char* dest = &_firstChar)
                for (int i = 0; i < n; i++) dest[i] = value[i];
        }
    }
}
