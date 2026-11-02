// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

namespace Terminal.Gui.Editor.Document.Utils;

/// <summary>
///     <para>
///         Shared utility types used by the document layer and other subsystems.
///     </para>
///     <para>
///         Includes the <see cref="Rope{T}" /> data structure (a balanced B-tree for efficient insert/delete in large
///         sequences), <see cref="Deque{T}" /> (double-ended queue), <see cref="CompressingTreeList{T}" /> (run-length
///         compressed list), <see cref="FileReader" /> (encoding-detecting file reader),
///         <see cref="IFreezable" /> (immutability pattern), and various string/text helpers.
///     </para>
///     <para>
///         These types are adapted from
///         <a href="https://github.com/AvaloniaUI/AvaloniaEdit">AvaloniaEdit</a>'s utility layer and have no
///         dependency on Terminal.Gui.
///     </para>
/// </summary>
[CompilerGenerated]
internal static class NamespaceDoc;
