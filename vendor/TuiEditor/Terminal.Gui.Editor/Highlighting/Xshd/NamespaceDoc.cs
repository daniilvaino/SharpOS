// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

namespace Terminal.Gui.Editor.Highlighting.Xshd;

/// <summary>
///     <para>
///         xshd (XML Syntax Highlighting Definition) file format support: loaders, savers, and AST types.
///     </para>
///     <para>
///         Contains <see cref="HighlightingLoader" /> (reads xshd XML into an <see cref="XshdSyntaxDefinition" />
///         and compiles it into an <see cref="Terminal.Gui.Editor.Highlighting.IHighlightingDefinition" />),
///         <see cref="SaveXshdVisitor" /> (serializes definitions back to XML), and the AST node types
///         (<see cref="XshdRuleSet" />, <see cref="XshdSpan" />, <see cref="XshdRule" />,
///         <see cref="XshdKeywords" />, <see cref="XshdColor" />, etc.) that represent the parsed structure.
///     </para>
///     <para>
///         Supports both xshd v1 and v2 formats via <see cref="V1Loader" /> and <see cref="V2Loader" />.
///     </para>
/// </summary>
[CompilerGenerated]
internal static class NamespaceDoc;
