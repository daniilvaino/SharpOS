// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

namespace Terminal.Gui.Editor.Highlighting;

/// <summary>
///     <para>
///         Syntax highlighting engine driven by xshd (XML Syntax Highlighting Definition) files.
///     </para>
///     <para>
///         The core types are <see cref="IHighlightingDefinition" /> (describes a language's highlighting rules),
///         <see cref="DocumentHighlighter" /> (applies rules to a <see cref="Document.TextDocument" /> producing
///         <see cref="HighlightedLine" />s), and <see cref="HighlightingManager" /> (registry of built-in and
///         user-loaded definitions, with lookup by name or file extension).
///     </para>
///     <para>
///         Built-in definitions include C#, C++, Java, JavaScript, Python, PowerShell, TSQL, VB, JSON, HTML, XML,
///         CSS, and Markdown. Highlight colors compose with the active Terminal.Gui color scheme.
///     </para>
/// </summary>
[CompilerGenerated]
internal static class NamespaceDoc;
