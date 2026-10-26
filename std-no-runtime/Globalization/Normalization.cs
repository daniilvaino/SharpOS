// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from dotnet/runtime main (.NET 10, the fork dotnet-runtime-sharpos):
//   src/libraries/System.Private.CoreLib/src/System/Globalization/Normalization.cs
//   src/libraries/System.Private.CoreLib/src/System/Text/NormalizationForm.cs
//   src/libraries/System.Private.CoreLib/src/System/StringNormalizationExtensions.cs
//   String.cs, "Normalization Methods"
// std is always in globalization-invariant mode (Globalization.cs), and there
// the BCL treats every string as normalized: Normalize returns its input,
// IsNormalized says true, after checking the form. That branch is the whole
// of these bodies.
//
// SharpOS cuts: GlobalizationMode.Invariant is the constant true, so the
// ICU/NLS back ends are gone; TryNormalize's overlap check (span Overlaps is
// not ported); [UnsupportedOSPlatform("browser")] on the K forms.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace System.Text
{
    public enum NormalizationForm
    {
        FormC = 1,
        FormD = 2,
        FormKC = 5,
        FormKD = 6
    }
}

namespace System.Globalization
{
    internal static partial class Normalization
    {
        internal static bool IsNormalized(ReadOnlySpan<char> source, NormalizationForm normalizationForm = NormalizationForm.FormC)
        {
            CheckNormalizationForm(normalizationForm);

            // In Invariant mode we assume all characters are normalized because we don't support any linguistic operations on strings.
            return true;
        }

        internal static string Normalize(string strInput, NormalizationForm normalizationForm)
        {
            CheckNormalizationForm(normalizationForm);

            // In Invariant mode we assume all characters are normalized because we don't support any linguistic operations on strings.
            return strInput;
        }

        internal static bool TryNormalize(ReadOnlySpan<char> source, Span<char> destination, out int charsWritten, NormalizationForm normalizationForm = NormalizationForm.FormC)
        {
            CheckNormalizationForm(normalizationForm);

            // In Invariant mode we assume all characters are normalized because we don't support any linguistic operations on strings.
            if (source.TryCopyTo(destination))
            {
                charsWritten = source.Length;
                return true;
            }

            charsWritten = 0;
            return false;
        }

        internal static int GetNormalizedLength(this ReadOnlySpan<char> source, NormalizationForm normalizationForm = NormalizationForm.FormC)
        {
            CheckNormalizationForm(normalizationForm);

            // In Invariant mode we assume all characters are normalized because we don't support any linguistic operations on strings.
            return source.Length;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CheckNormalizationForm(NormalizationForm normalizationForm)
        {
            if (normalizationForm != NormalizationForm.FormC &&
                normalizationForm != NormalizationForm.FormD &&
                normalizationForm != NormalizationForm.FormKC &&
                normalizationForm != NormalizationForm.FormKD)
            {
                throw new ArgumentException("Invalid normalization form.", nameof(normalizationForm));
            }
        }
    }
}

namespace System
{
    public sealed partial class String
    {
        // Normalization Methods
        // These just wrap calls to Normalization class
        public bool IsNormalized()
        {
            return IsNormalized(NormalizationForm.FormC);
        }

        public bool IsNormalized(NormalizationForm normalizationForm)
        {
            return Normalization.IsNormalized(this, normalizationForm);
        }

        public string Normalize()
        {
            return Normalize(NormalizationForm.FormC);
        }

        public string Normalize(NormalizationForm normalizationForm)
        {
            return Normalization.Normalize(this, normalizationForm);
        }
    }

    /// <summary>
    /// Extensions for string normalization.
    /// </summary>
    public static partial class StringNormalizationExtensions
    {
        public static bool IsNormalized(this string strInput)
        {
            return IsNormalized(strInput, NormalizationForm.FormC);
        }

        public static bool IsNormalized(this string strInput, NormalizationForm normalizationForm)
        {
            ArgumentNullException.ThrowIfNull(strInput, nameof(strInput));

            return strInput.IsNormalized(normalizationForm);
        }

        public static bool IsNormalized(this ReadOnlySpan<char> source, NormalizationForm normalizationForm = NormalizationForm.FormC) =>
            Normalization.IsNormalized(source, normalizationForm);

        public static string Normalize(this string strInput)
        {
            // Default to Form C
            return Normalize(strInput, NormalizationForm.FormC);
        }

        public static string Normalize(this string strInput, NormalizationForm normalizationForm)
        {
            ArgumentNullException.ThrowIfNull(strInput, nameof(strInput));

            return strInput.Normalize(normalizationForm);
        }

        public static bool TryNormalize(this ReadOnlySpan<char> source, Span<char> destination, out int charsWritten, NormalizationForm normalizationForm = NormalizationForm.FormC) =>
            Normalization.TryNormalize(source, destination, out charsWritten, normalizationForm);

        public static int GetNormalizedLength(this ReadOnlySpan<char> source, NormalizationForm normalizationForm = NormalizationForm.FormC) =>
            Normalization.GetNormalizedLength(source, normalizationForm);
    }
}
