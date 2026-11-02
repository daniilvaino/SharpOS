// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

namespace Terminal.Gui.Editor.Document.Search;

/// <summary>
///     <para>
///         Pluggable search strategies for find and replace operations.
///     </para>
///     <para>
///         Contains <see cref="ISearchStrategy" /> (the strategy interface), built-in implementations
///         (<see cref="RegexSearchStrategy" /> and normal/whole-word string search), and
///         <see cref="SearchStrategyFactory" /> for constructing strategies by mode and options.
///     </para>
///     <para>
///         Search strategies operate on <see cref="Terminal.Gui.Editor.Document.ITextSource" /> and return
///         match results as offset/length pairs. The editor's find/replace UI and
///         <see cref="Terminal.Gui.Editor.Rendering.SearchHitRenderer" /> consume these results.
///     </para>
/// </summary>
[CompilerGenerated]
internal static class NamespaceDoc;
