// Adapted for Terminal.Gui from AvaloniaEdit d7a6b63

#nullable disable
// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System.Globalization;
using Terminal.Gui.Editor.Document;
using Terminal.Gui.Editor.Document.Utils;

namespace Terminal.Gui.Editor.Highlighting;

using Color = Terminal.Gui.Drawing.Color; // SharpOS: v1's Terminal.Gui.Color (an enum) would be found first

/// <summary>
///     Represents a highlighted document line.
/// </summary>
public class HighlightedLine
{
    /// <summary>
    ///     Creates a new HighlightedLine instance.
    /// </summary>
    public HighlightedLine (IDocument document, IDocumentLine documentLine)
    {
        //if (!document.Lines.Contains(documentLine))
        //	throw new ArgumentException("Line is null or not part of document");
        Document = document ?? throw new ArgumentNullException (nameof (document));
        DocumentLine = documentLine;
        Sections = new NullSafeCollection<HighlightedSection> ();
    }

    /// <summary>
    ///     Gets the document associated with this HighlightedLine.
    /// </summary>
    public IDocument Document { get; }

    /// <summary>
    ///     Gets the document line associated with this HighlightedLine.
    /// </summary>
    public IDocumentLine DocumentLine { get; }

    /// <summary>
    ///     Gets the highlighted sections.
    ///     The sections are not overlapping, but they may be nested.
    ///     In that case, outer sections come in the list before inner sections.
    ///     The sections are sorted by start offset.
    /// </summary>
    public IList<HighlightedSection> Sections { get; }

    /// <summary>
    ///     Validates that the sections are sorted correctly, and that they are not overlapping.
    /// </summary>
    /// <seealso cref="Sections" />
    public void ValidateInvariants ()
    {
        HighlightedLine line = this;
        var lineStartOffset = line.DocumentLine.Offset;
        var lineEndOffset = line.DocumentLine.EndOffset;
        for (var i = 0; i < line.Sections.Count; i++)
        {
            HighlightedSection s1 = line.Sections[i];
            if (s1.Offset < lineStartOffset || s1.Length < 0 || s1.Offset + s1.Length > lineEndOffset)
            {
                throw new InvalidOperationException ("Section is outside line bounds");
            }

            for (var j = i + 1; j < line.Sections.Count; j++)
            {
                HighlightedSection s2 = line.Sections[j];
                if (s2.Offset >= s1.Offset + s1.Length)
                {
                    // s2 is after s1
                }
                else if (s2.Offset >= s1.Offset && s2.Offset + s2.Length <= s1.Offset + s1.Length)
                {
                    // s2 is nested within s1
                }
                else
                {
                    throw new InvalidOperationException ("Sections are overlapping or incorrectly sorted.");
                }
            }
        }
    }

    /// <inheritdoc />
    public override string ToString ()
    {
        return string.Create (CultureInfo.InvariantCulture, $"[{nameof (HighlightedLine)} Sections={Sections.Count}]");
    }

    #region Merge

    /// <summary>
    ///     Merges the additional line into this line.
    /// </summary>
    public void MergeWith (HighlightedLine additionalLine)
    {
        if (additionalLine == null)
        {
            return;
        }
#if DEBUG
        ValidateInvariants ();
        additionalLine.ValidateInvariants ();
#endif

        var pos = 0;
        Stack<int> activeSectionEndOffsets = new ();
        var lineEndOffset = DocumentLine.EndOffset;
        activeSectionEndOffsets.Push (lineEndOffset);
        foreach (HighlightedSection newSection in additionalLine.Sections)
        {
            var newSectionStart = newSection.Offset;
            // Track the existing sections using the stack, up to the point where
            // we need to insert the first part of the newSection
            while (pos < Sections.Count)
            {
                HighlightedSection s = Sections[pos];
                if (newSection.Offset < s.Offset)
                {
                    break;
                }

                while (s.Offset > activeSectionEndOffsets.Peek ())
                {
                    activeSectionEndOffsets.Pop ();
                }

                activeSectionEndOffsets.Push (s.Offset + s.Length);
                pos++;
            }

            // Now insert the new section
            // Create a copy of the stack so that we can track the sections we traverse
            // during the insertion process:
            Stack<int> insertionStack = new (activeSectionEndOffsets.Reverse ());
            // The stack enumerator reverses the order of the elements, so we call Reverse() to restore
            // the original order.
            int i;
            for (i = pos; i < Sections.Count; i++)
            {
                HighlightedSection s = Sections[i];
                if (newSection.Offset + newSection.Length <= s.Offset)
                {
                    break;
                }

                // Insert a segment in front of s:
                Insert (ref i, ref newSectionStart, s.Offset, newSection.Color, insertionStack);

                while (s.Offset > insertionStack.Peek ())
                {
                    insertionStack.Pop ();
                }

                insertionStack.Push (s.Offset + s.Length);
            }

            Insert (ref i, ref newSectionStart, newSection.Offset + newSection.Length, newSection.Color,
                insertionStack);
        }

#if DEBUG
        ValidateInvariants ();
#endif
    }

    private void Insert (ref int pos, ref int newSectionStart, int insertionEndPos, HighlightingColor color,
        Stack<int> insertionStack)
    {
        if (newSectionStart >= insertionEndPos)
        {
            // nothing to insert here
            return;
        }

        while (insertionStack.Peek () <= newSectionStart)
        {
            insertionStack.Pop ();
        }

        while (insertionStack.Peek () < insertionEndPos)
        {
            var end = insertionStack.Pop ();
            // insert the portion from newSectionStart to end
            if (end > newSectionStart)
            {
                Sections.Insert (pos++, new HighlightedSection
                {
                    Offset = newSectionStart,
                    Length = end - newSectionStart,
                    Color = color
                });
                newSectionStart = end;
            }
        }

        if (insertionEndPos > newSectionStart)
        {
            Sections.Insert (pos++, new HighlightedSection
            {
                Offset = newSectionStart,
                Length = insertionEndPos - newSectionStart,
                Color = color
            });
            newSectionStart = insertionEndPos;
        }
    }

    #endregion
}
