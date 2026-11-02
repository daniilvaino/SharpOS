// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;

namespace Terminal.Gui.Editor.Rendering;

/// <summary>
///     <para>
///         Cell-grid rendering pipeline that transforms document lines into visual output.
///     </para>
///     <para>
///         The pipeline flows: <see cref="VisualLineBuilder" /> → <see cref="CellVisualLine" /> (composed of
///         <see cref="CellVisualLineElement" />s such as <see cref="TextRunElement" />, <see cref="TabElement" />,
///         <see cref="NewlineGlyphElement" />, and <see cref="FoldingMarkerElement" />).
///     </para>
///     <para>
///         Pluggable extension points:
///         <list type="bullet">
///             <item>
///                 <see cref="IVisualLineTransformer" /> — mutates element attributes (syntax highlighting, fold
///                 markers).
///             </item>
///             <item><see cref="IBackgroundRenderer" /> — paints cell rectangles (selection, current line, search hits).</item>
///             <item><see cref="IOverlayRenderer" /> — draws overlays above the text (multi-caret indicators).</item>
///         </list>
///     </para>
///     <para>
///         Visual lines are cached with LRU eviction and selectively invalidated from
///         <see cref="Document.TextDocument.Changed" /> offset/length ranges.
///     </para>
/// </summary>
[CompilerGenerated]
internal static class NamespaceDoc;
