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

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Terminal.Gui.Editor.Document.Utils;
using Terminal.Gui.Drawing;

namespace Terminal.Gui.Editor.Highlighting;

using Color = Terminal.Gui.Drawing.Color; // SharpOS: v1's Terminal.Gui.Color (an enum) would be found first

/// <summary>
///     A highlighting color is a set of font properties and foreground and background color.
/// </summary>
public class HighlightingColor : IFreezable, ICloneable, IEquatable<HighlightingColor>
{
    internal static readonly HighlightingColor Empty = FreezableHelper.FreezeAndReturn (new HighlightingColor ());
    private HighlightingBrush _background;
    private bool? _bold;
    private HighlightingBrush _foreground;
    private bool? _italic;

    private string _name;
    private bool? _strikethrough;
    private bool? _underline;

    /// <summary>
    ///     Gets/Sets the name of the color.
    /// </summary>
    public string Name
    {
        get => _name;
        set
        {
            if (IsFrozen)
            {
                throw new InvalidOperationException ();
            }

            _name = value;
        }
    }

    /// <summary>
    ///     Gets/sets the bold flag. Null if the highlighting color does not change the bold state.
    /// </summary>
    public bool? Bold
    {
        get => _bold;
        set
        {
            if (IsFrozen)
            {
                throw new InvalidOperationException ();
            }

            _bold = value;
        }
    }

    /// <summary>
    ///     Gets/sets the italic flag. Null if the highlighting color does not change the italic state.
    /// </summary>
    public bool? Italic
    {
        get => _italic;
        set
        {
            if (IsFrozen)
            {
                throw new InvalidOperationException ();
            }

            _italic = value;
        }
    }

    /// <summary>
    ///     Gets/sets the underline flag. Null if the underline status does not change the font style.
    /// </summary>
    public bool? Underline
    {
        get => _underline;
        set
        {
            if (IsFrozen)
            {
                throw new InvalidOperationException ();
            }

            _underline = value;
        }
    }

    /// <summary>
    ///     Gets/sets the strikethrough flag
    /// </summary>
    public bool? Strikethrough
    {
        get => _strikethrough;
        set
        {
            if (IsFrozen)
            {
                throw new InvalidOperationException ();
            }

            _strikethrough = value;
        }
    }

    /// <summary>
    ///     Gets/sets the foreground color applied by the highlighting.
    /// </summary>
    public HighlightingBrush Foreground
    {
        get => _foreground;
        set
        {
            if (IsFrozen)
            {
                throw new InvalidOperationException ();
            }

            _foreground = value;
        }
    }

    /// <summary>
    ///     Gets/sets the background color applied by the highlighting.
    /// </summary>
    public HighlightingBrush Background
    {
        get => _background;
        set
        {
            if (IsFrozen)
            {
                throw new InvalidOperationException ();
            }

            _background = value;
        }
    }

    private VisualRole? _role;

    /// <summary>
    ///     Gets/sets the Terminal.Gui <see cref="VisualRole" /> this color maps to, if any.
    ///     Populated at load time from the xshd <c>category=</c> attribute or the
    ///     <see cref="XshdRoleMap" /> name table. When set and the active <see cref="Scheme" />
    ///     explicitly defines that role, the colorizer uses the scheme's themed attribute;
    ///     otherwise it keeps only this color's xshd-declared foreground over the editor's scheme
    ///     background (the xshd background is not used).
    /// </summary>
    public VisualRole? Role
    {
        get => _role;
        set
        {
            if (IsFrozen)
            {
                throw new InvalidOperationException ();
            }

            _role = value;
        }
    }

    internal bool IsEmptyForMerge => _bold == null && _italic == null && _underline == null
                                     && _strikethrough == null && _foreground == null && _background == null;

    object ICloneable.Clone ()
    {
        return Clone ();
    }

    /// <inheritdoc />
    public virtual bool Equals (HighlightingColor other)
    {
        if (other == null)
        {
            return false;
        }

        return _name == other._name && _bold == other._bold
                                    && _italic == other._italic && _underline == other._underline &&
                                    _strikethrough == other._strikethrough
                                    && _role == other._role
                                    && Equals (_foreground, other._foreground) &&
                                    Equals (_background, other._background);
    }

    /// <summary>
    ///     Prevent further changes to this highlighting color.
    /// </summary>
    public virtual void Freeze ()
    {
        IsFrozen = true;
    }

    /// <summary>
    ///     Gets whether this HighlightingColor instance is frozen.
    /// </summary>
    public bool IsFrozen { get; private set; }

    /// <summary>
    ///     Gets CSS code for the color.
    /// </summary>
    [SuppressMessage ("Microsoft.Globalization", "CA1308:NormalizeStringsToUppercase",
        Justification = "CSS usually uses lowercase, and all possible values are English-only")]
    public virtual string ToCss ()
    {
        StringBuilder b = new ();
        Color? c = Foreground?.Color;
        if (c != null)
        {
            Color cv = c.Value;
            b.AppendFormat (CultureInfo.InvariantCulture, "color: #{0:x2}{1:x2}{2:x2}; ", cv.R, cv.G, cv.B);
        }

        if (Bold != null)
        {
            b.Append ("font-weight: ");
            b.Append (Bold.Value ? "bold" : "normal");
            b.Append ("; ");
        }

        if (Italic != null)
        {
            b.Append ("font-style: ");
            b.Append (Italic.Value ? "italic" : "normal");
            b.Append ("; ");
        }

        if (Underline != null)
        {
            b.Append ("text-decoration: ");
            b.Append (Underline.Value ? "underline" : "none");
            b.Append ("; ");
        }

        if (Strikethrough != null)
        {
            if (Underline == null)
            {
                b.Append ("text-decoration:  ");
            }

            b.Remove (b.Length - 1, 1);
            b.Append (Strikethrough.Value ? " line-through" : " none");
            b.Append ("; ");
        }

        return b.ToString ();
    }

    /// <inheritdoc />
    public override string ToString ()
    {
        return string.Create (CultureInfo.InvariantCulture,
            $"[{nameof (HighlightingColor)} {(string.IsNullOrEmpty (Name) ? ToCss () : Name)}]");
    }

    /// <summary>
    ///     Clones this highlighting color.
    ///     If this color is frozen, the clone will be unfrozen.
    /// </summary>
    public virtual HighlightingColor Clone ()
    {
        HighlightingColor c = (HighlightingColor)MemberwiseClone ();
        c.IsFrozen = false;
        return c;
    }

    /// <inheritdoc />
    public sealed override bool Equals (object obj)
    {
        return Equals (obj as HighlightingColor);
    }

    /// <inheritdoc />
    [SuppressMessage ("ReSharper", "NonReadonlyMemberInGetHashCode")]
    public override int GetHashCode ()
    {
        var hashCode = 0;
        unchecked
        {
            if (_name != null)
            {
                hashCode += 1000000007 * _name.GetHashCode ();
            }

            hashCode += 1000000009 * _bold.GetHashCode ();
            hashCode += 1000000021 * _italic.GetHashCode ();
            hashCode += 1000000093 * _role.GetHashCode ();
            if (_foreground != null)
            {
                hashCode += 1000000033 * _foreground.GetHashCode ();
            }

            if (_background != null)
            {
                hashCode += 1000000087 * _background.GetHashCode ();
            }
        }

        return hashCode;
    }

    /// <summary>
    ///     Overwrites the properties in this HighlightingColor with those from the given color;
    ///     but maintains the current values where the properties of the given color return <c>null</c>.
    /// </summary>
    public void MergeWith (HighlightingColor color)
    {
        FreezableHelper.ThrowIfFrozen (this);
        if (color._bold != null)
        {
            _bold = color._bold;
        }

        if (color._italic != null)
        {
            _italic = color._italic;
        }

        if (color._foreground != null)
        {
            _foreground = color._foreground;
        }

        if (color._background != null)
        {
            _background = color._background;
        }

        if (color._underline != null)
        {
            _underline = color._underline;
        }

        if (color._strikethrough != null)
        {
            _strikethrough = color._strikethrough;
        }

        if (color._role != null)
        {
            _role = color._role;
        }
    }
}
