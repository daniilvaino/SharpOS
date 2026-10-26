// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from dotnet/runtime main (.NET 10, the fork dotnet-runtime-sharpos):
//   src/libraries/System.Private.CoreLib/src/System/Text/StringBuilder.cs
// The interpolated-string members: Append/AppendLine taking
// AppendInterpolatedStringHandler, and the handler itself. Bodies verbatim.
//
// Cuts (marked "SharpOS cut:" in place):
//   - The enum branch of AppendFormatted<T> (typeof(T).IsEnum ->
//     Enum.TryFormatUnconstrained): enum formatting is not ported; an enum
//     value takes the IFormattable/ToString path, as in
//     DefaultInterpolatedStringHandler here.
//   - AppendFormattedWithTempSpace formats into a heap-backed
//     DefaultInterpolatedStringHandler rather than a stackalloc'd buffer:
//     string.StackallocCharBufferSizeLimit is CoreLib-internal.

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace System.Text
{
    public sealed partial class StringBuilder
    {
        /// <summary>Appends the specified interpolated string to this instance.</summary>
        /// <param name="handler">The interpolated string to append.</param>
        /// <returns>A reference to this instance after the append operation has completed.</returns>
        public StringBuilder Append([InterpolatedStringHandlerArgument("")] ref AppendInterpolatedStringHandler handler) => this;

        /// <summary>Appends the specified interpolated string to this instance.</summary>
        /// <param name="provider">An object that supplies culture-specific formatting information.</param>
        /// <param name="handler">The interpolated string to append.</param>
        /// <returns>A reference to this instance after the append operation has completed.</returns>
        public StringBuilder Append(IFormatProvider? provider, [InterpolatedStringHandlerArgument("", nameof(provider))] ref AppendInterpolatedStringHandler handler) => this;

        /// <summary>Appends the specified interpolated string followed by the default line terminator to the end of the current StringBuilder object.</summary>
        /// <param name="handler">The interpolated string to append.</param>
        /// <returns>A reference to this instance after the append operation has completed.</returns>
        public StringBuilder AppendLine([InterpolatedStringHandlerArgument("")] ref AppendInterpolatedStringHandler handler) => AppendLine();

        /// <summary>Appends the specified interpolated string followed by the default line terminator to the end of the current StringBuilder object.</summary>
        /// <param name="provider">An object that supplies culture-specific formatting information.</param>
        /// <param name="handler">The interpolated string to append.</param>
        /// <returns>A reference to this instance after the append operation has completed.</returns>
        public StringBuilder AppendLine(IFormatProvider? provider, [InterpolatedStringHandlerArgument("", nameof(provider))] ref AppendInterpolatedStringHandler handler) => AppendLine();

        /// <summary>Provides a handler used by the language compiler to append interpolated strings into <see cref="StringBuilder"/> instances.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [InterpolatedStringHandler]
        public struct AppendInterpolatedStringHandler
        {
            // Implementation note:
            // As this type is only intended to be targeted by the compiler, public APIs eschew argument validation logic
            // in a variety of places, e.g. allowing a null input when one isn't expected to produce a NullReferenceException rather
            // than an ArgumentNullException.

            /// <summary>The associated StringBuilder to which to append.</summary>
            internal readonly StringBuilder _stringBuilder;
            /// <summary>Optional provider to pass to IFormattable.ToString or ISpanFormattable.TryFormat calls.</summary>
            private readonly IFormatProvider? _provider;
            /// <summary>Whether <see cref="_provider"/> provides an ICustomFormatter.</summary>
            private readonly bool _hasCustomFormatter;

            /// <summary>Creates a handler used to append an interpolated string into a <see cref="StringBuilder"/>.</summary>
            /// <param name="literalLength">The number of constant characters outside of interpolation expressions in the interpolated string.</param>
            /// <param name="formattedCount">The number of interpolation expressions in the interpolated string.</param>
            /// <param name="stringBuilder">The associated StringBuilder to which to append.</param>
            public AppendInterpolatedStringHandler(int literalLength, int formattedCount, StringBuilder stringBuilder)
            {
                _stringBuilder = stringBuilder;
                _provider = null;
                _hasCustomFormatter = false;
            }

            /// <summary>Creates a handler used to translate an interpolated string into a <see cref="string"/>.</summary>
            /// <param name="literalLength">The number of constant characters outside of interpolation expressions in the interpolated string.</param>
            /// <param name="formattedCount">The number of interpolation expressions in the interpolated string.</param>
            /// <param name="stringBuilder">The associated StringBuilder to which to append.</param>
            /// <param name="provider">An object that supplies culture-specific formatting information.</param>
            public AppendInterpolatedStringHandler(int literalLength, int formattedCount, StringBuilder stringBuilder, IFormatProvider? provider)
            {
                _stringBuilder = stringBuilder;
                _provider = provider;
                _hasCustomFormatter = provider is not null && DefaultInterpolatedStringHandler.HasCustomFormatter(provider);
            }

            /// <summary>Writes the specified string to the handler.</summary>
            /// <param name="value">The string to write.</param>
            public void AppendLiteral(string value) => _stringBuilder.Append(value);

            #region AppendFormatted
            // Design note:
            // This provides the same set of overloads and semantics as DefaultInterpolatedStringHandler.

            #region AppendFormatted T
            /// <summary>Writes the specified value to the handler.</summary>
            /// <param name="value">The value to write.</param>
            /// <typeparam name="T">The type of the value to write.</typeparam>
            public void AppendFormatted<T>(T value)
            {
                if (_hasCustomFormatter)
                {
                    // If there's a custom formatter, always use it.
                    AppendCustomFormatter(value, format: null);
                    return;
                }

                if (value is null)
                {
                    return;
                }

                if (value is IFormattable)
                {
                    // SharpOS cut: the enum branch (Enum.TryFormatUnconstrained).
                    if (value is ISpanFormattable)
                    {
                        Span<char> destination = _stringBuilder.RemainingCurrentChunk;
                        if (((ISpanFormattable)value).TryFormat(destination, out int charsWritten, default, _provider)) // constrained call avoiding boxing for value types
                        {
                            if ((uint)charsWritten > (uint)destination.Length)
                            {
                                // Protect against faulty ISpanFormattable implementations returning invalid charsWritten values.
                                // Other code in _stringBuilder uses Unsafe manipulation, and we want to ensure m_ChunkLength remains safe.
                                ThrowHelper.ThrowFormatInvalidString();
                            }

                            _stringBuilder.m_ChunkLength += charsWritten;
                        }
                        else
                        {
                            // Not enough room in the current chunk.  Take the slow path that formats into temporary space
                            // and then copies the result into the StringBuilder.
                            AppendFormattedWithTempSpace(value, 0, format: null);
                        }
                    }
                    else
                    {
                        _stringBuilder.Append(((IFormattable)value).ToString(format: null, _provider)); // constrained call avoiding boxing for value types
                    }
                }
                else
                {
                    _stringBuilder.Append(value.ToString());
                }
            }

            /// <summary>Writes the specified value to the handler.</summary>
            /// <param name="value">The value to write.</param>
            /// <param name="format">The format string.</param>
            /// <typeparam name="T">The type of the value to write.</typeparam>
            public void AppendFormatted<T>(T value, string? format)
            {
                if (_hasCustomFormatter)
                {
                    // If there's a custom formatter, always use it.
                    AppendCustomFormatter(value, format);
                    return;
                }

                if (value is null)
                {
                    return;
                }

                if (value is IFormattable)
                {
                    // SharpOS cut: the enum branch (Enum.TryFormatUnconstrained).
                    if (value is ISpanFormattable)
                    {
                        Span<char> destination = _stringBuilder.RemainingCurrentChunk;
                        if (((ISpanFormattable)value).TryFormat(destination, out int charsWritten, format, _provider)) // constrained call avoiding boxing for value types
                        {
                            if ((uint)charsWritten > (uint)destination.Length)
                            {
                                ThrowHelper.ThrowFormatInvalidString();
                            }

                            _stringBuilder.m_ChunkLength += charsWritten;
                        }
                        else
                        {
                            AppendFormattedWithTempSpace(value, 0, format);
                        }
                    }
                    else
                    {
                        _stringBuilder.Append(((IFormattable)value).ToString(format, _provider)); // constrained call avoiding boxing for value types
                    }
                }
                else
                {
                    _stringBuilder.Append(value.ToString());
                }
            }

            /// <summary>Writes the specified value to the handler.</summary>
            /// <param name="value">The value to write.</param>
            /// <param name="alignment">Minimum number of characters that should be written for this value.  If the value is negative, it indicates left-aligned and the required minimum is the absolute value.</param>
            /// <typeparam name="T">The type of the value to write.</typeparam>
            public void AppendFormatted<T>(T value, int alignment) =>
                AppendFormatted(value, alignment, format: null);

            /// <summary>Writes the specified value to the handler.</summary>
            /// <param name="value">The value to write.</param>
            /// <param name="format">The format string.</param>
            /// <param name="alignment">Minimum number of characters that should be written for this value.  If the value is negative, it indicates left-aligned and the required minimum is the absolute value.</param>
            /// <typeparam name="T">The type of the value to write.</typeparam>
            public void AppendFormatted<T>(T value, int alignment, string? format)
            {
                if (alignment == 0)
                {
                    // This overload is used as a fallback from several disambiguation overloads, so special-case 0.
                    AppendFormatted(value, format);
                }
                else if (alignment < 0)
                {
                    // Left aligned: format into the handler, then append any additional padding required.
                    int start = _stringBuilder.Length;
                    AppendFormatted(value, format);
                    int paddingRequired = -alignment - (_stringBuilder.Length - start);
                    if (paddingRequired > 0)
                    {
                        _stringBuilder.Append(' ', paddingRequired);
                    }
                }
                else
                {
                    // Right aligned: format into temporary space and then copy that into the handler, appropriately aligned.
                    AppendFormattedWithTempSpace(value, alignment, format);
                }
            }

            /// <summary>Formats into temporary space and then appends the result into the StringBuilder.</summary>
            private void AppendFormattedWithTempSpace<T>(T value, int alignment, string? format)
            {
                // SharpOS cut: a pooled buffer instead of stackalloc char[string.StackallocCharBufferSizeLimit].
                var handler = new DefaultInterpolatedStringHandler(0, 0, _provider);
                handler.AppendFormatted(value, format);
                AppendFormatted(handler.Text, alignment);
                handler.Clear();
            }
            #endregion

            #region AppendFormatted ReadOnlySpan<char>
            /// <summary>Writes the specified character span to the handler.</summary>
            /// <param name="value">The span to write.</param>
            public void AppendFormatted(ReadOnlySpan<char> value) => _stringBuilder.Append(value);

            /// <summary>Writes the specified string of chars to the handler.</summary>
            /// <param name="value">The span to write.</param>
            /// <param name="alignment">Minimum number of characters that should be written for this value.  If the value is negative, it indicates left-aligned and the required minimum is the absolute value.</param>
            /// <param name="format">The format string.</param>
            public void AppendFormatted(ReadOnlySpan<char> value, int alignment = 0, string? format = null)
            {
                if (alignment == 0)
                {
                    _stringBuilder.Append(value);
                }
                else
                {
                    bool leftAlign = false;
                    if (alignment < 0)
                    {
                        leftAlign = true;
                        alignment = -alignment;
                    }

                    int paddingRequired = alignment - value.Length;
                    if (paddingRequired <= 0)
                    {
                        _stringBuilder.Append(value);
                    }
                    else if (leftAlign)
                    {
                        _stringBuilder.Append(value);
                        _stringBuilder.Append(' ', paddingRequired);
                    }
                    else
                    {
                        _stringBuilder.Append(' ', paddingRequired);
                        _stringBuilder.Append(value);
                    }
                }
            }
            #endregion

            #region AppendFormatted string
            /// <summary>Writes the specified value to the handler.</summary>
            /// <param name="value">The value to write.</param>
            public void AppendFormatted(string? value)
            {
                if (!_hasCustomFormatter)
                {
                    _stringBuilder.Append(value);
                }
                else
                {
                    AppendFormatted<string?>(value);
                }
            }

            /// <summary>Writes the specified value to the handler.</summary>
            /// <param name="value">The value to write.</param>
            /// <param name="alignment">Minimum number of characters that should be written for this value.  If the value is negative, it indicates left-aligned and the required minimum is the absolute value.</param>
            /// <param name="format">The format string.</param>
            public void AppendFormatted(string? value, int alignment = 0, string? format = null) =>
                AppendFormatted<string?>(value, alignment, format);
            #endregion

            #region AppendFormatted object
            /// <summary>Writes the specified value to the handler.</summary>
            /// <param name="value">The value to write.</param>
            /// <param name="alignment">Minimum number of characters that should be written for this value.  If the value is negative, it indicates left-aligned and the required minimum is the absolute value.</param>
            /// <param name="format">The format string.</param>
            public void AppendFormatted(object? value, int alignment = 0, string? format = null) =>
                AppendFormatted<object?>(value, alignment, format);
            #endregion
            #endregion

            /// <summary>Formats the value using the custom formatter from the provider.</summary>
            /// <param name="value">The value to write.</param>
            /// <param name="format">The format string.</param>
            /// <typeparam name="T">The type of the value to write.</typeparam>
            [MethodImpl(MethodImplOptions.NoInlining)]
            private void AppendCustomFormatter<T>(T value, string? format)
            {
                Debug.Assert(_hasCustomFormatter);
                Debug.Assert(_provider != null);

                ICustomFormatter? formatter = (ICustomFormatter?)_provider.GetFormat(typeof(ICustomFormatter));
                Debug.Assert(formatter != null, "An incorrectly written provider said it implemented ICustomFormatter, and then didn't");

                if (formatter is not null)
                {
                    _stringBuilder.Append(formatter.Format(format, value, _provider));
                }
            }
        }
    }
}
