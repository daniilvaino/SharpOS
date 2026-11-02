// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

namespace Terminal.Gui.Editor.Document.Folding;

/// <summary>
///     <para>
///         Code-folding infrastructure for collapsing and expanding regions of a document.
///     </para>
///     <para>
///         Contains <see cref="FoldingManager" /> (manages fold state for a document),
///         <see cref="FoldingSection" /> (represents a single collapsible region),
///         <see cref="IFoldingStrategy" /> (pluggable strategy interface), and built-in strategies
///         (<see cref="BraceFoldingStrategy" />, <see cref="XmlFoldingStrategy" />).
///     </para>
///     <para>
///         Foldings auto-expand when the caret moves inside them. Consumers can implement custom
///         <see cref="IFoldingStrategy" /> instances for language-specific folding rules.
///     </para>
/// </summary>
[CompilerGenerated]
internal static class NamespaceDoc;
