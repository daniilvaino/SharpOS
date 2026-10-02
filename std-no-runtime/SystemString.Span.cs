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
        // From dotnet/runtime release/8.0 String.cs: MaxLength, the
        // interpolated-string Create overloads, and the static Ctor that ILC
        // redirects `newobj String::.ctor(ReadOnlySpan<char>)` to — without it
        // `new string(span)` failed codegen ("Expected method 'Ctor' not found").
        internal const int MaxLength = 0x3FFFFFDF;

        public static string Create(IFormatProvider provider,
            [System.Runtime.CompilerServices.InterpolatedStringHandlerArgument(nameof(provider))]
            ref System.Runtime.CompilerServices.DefaultInterpolatedStringHandler handler) =>
            handler.ToStringAndClear();

        public static string Create(IFormatProvider provider, Span<char> initialBuffer,
            [System.Runtime.CompilerServices.InterpolatedStringHandlerArgument(nameof(provider), nameof(initialBuffer))]
            ref System.Runtime.CompilerServices.DefaultInterpolatedStringHandler handler) =>
            handler.ToStringAndClear();

        private static string Ctor(ReadOnlySpan<char> value)
        {
            if (value.Length == 0)
                return "";

            string result = SharpOS.Std.NoRuntime.StringRuntime.FastAllocateString(value.Length);
            if (result.Length != value.Length)
                return "";

            fixed (char* dst = &result.GetPinnableReference())
            {
                for (int i = 0; i < value.Length; i++)
                    dst[i] = value[i];
            }

            return result;
        }

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

        // BCL surface (String.cs): a string IS a span of its characters. The
        // ported number formatter passes `string? format` where a
        // ReadOnlySpan<char> is expected, as CoreLib code does everywhere; null
        // becomes the empty span, as upstream.
        public static implicit operator ReadOnlySpan<char>(string? value) =>
            value != null ? new ReadOnlySpan<char>(ref value._firstChar, value.Length) : default;

        /// <summary>Copies the contents of this string into the destination span.</summary>
        /// <exception cref="ArgumentException">The destination span is shorter than the source string.</exception>
        public void CopyTo(Span<char> destination)
        {
            if ((uint)Length <= (uint)destination.Length)
            {
                fixed (char* src = &_firstChar)
                    for (int i = 0; i < _stringLength; i++) destination[i] = src[i];
            }
            else
            {
                throw new ArgumentException("Destination is too short.", nameof(destination));
            }
        }

        /// <summary>Copies the contents of this string into the destination span.</summary>
        /// <returns>true if the data was copied; false if the destination was too short to fit the contents of the string.</returns>
        public bool TryCopyTo(Span<char> destination)
        {
            bool retVal = false;
            if ((uint)Length <= (uint)destination.Length)
            {
                fixed (char* src = &_firstChar)
                    for (int i = 0; i < _stringLength; i++) destination[i] = src[i];
                retVal = true;
            }
            return retVal;
        }
    }
}
