// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

namespace Terminal.Gui.Editor.Indentation;

/// <summary>
///     <para>
///         Pluggable indentation strategies that control how the editor auto-indents new lines.
///     </para>
///     <para>
///         Contains the <see cref="IIndentationStrategy" /> interface and the built-in
///         <see cref="DefaultIndentationStrategy" /> (copies the previous line's leading whitespace on Enter).
///         Consumers can implement custom strategies for language-aware indentation (e.g., increasing indent
///         after an opening brace).
///     </para>
/// </summary>
[CompilerGenerated]
internal static class NamespaceDoc;
