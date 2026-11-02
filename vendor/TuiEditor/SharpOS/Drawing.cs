// SharpOS: the two Terminal.Gui v2 types the highlighting names, for an app
// on Terminal.Gui v1 (which has Color as a 16-colour enum in Terminal.Gui, not
// this). Ours, not v2's code: only what the highlighting uses of them.
//
//   Color      — 24-bit RGB with alpha; Parse takes #RGB, #RRGGBB, #AARRGGBB
//                and the CSS colour names the bundled .xshd files use, plus
//                the sixteen basic ones. An app maps it to its own palette.
//   VisualRole — the Code* roles an .xshd colour may name (category="...").

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Terminal.Gui.Drawing
{
    public readonly struct Color : IEquatable<Color>
    {
        public Color(byte r, byte g, byte b, byte a = 255)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        public byte R { get; }
        public byte G { get; }
        public byte B { get; }
        public byte A { get; }

        public static Color Parse(string text)
        {
            if (TryParse(text, out Color color)) return color;
            throw new FormatException("Not a colour: '" + text + "'.");
        }

        public static bool TryParse(string? text, out Color color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string s = text.Trim();
            if (s[0] == '#')
            {
                string hex = s.Substring(1);
                if (!uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint v)) return false;
                switch (hex.Length)
                {
                    case 3:
                        color = new Color((byte)(((v >> 8) & 0xF) * 17), (byte)(((v >> 4) & 0xF) * 17), (byte)((v & 0xF) * 17));
                        return true;
                    case 6:
                        color = new Color((byte)(v >> 16), (byte)(v >> 8), (byte)v);
                        return true;
                    case 8:
                        color = new Color((byte)(v >> 16), (byte)(v >> 8), (byte)v, (byte)(v >> 24));
                        return true;
                    default:
                        return false;
                }
            }
            foreach (KeyValuePair<string, uint> named in Names)
            {
                if (string.Equals(named.Key, s, StringComparison.OrdinalIgnoreCase))
                {
                    uint v = named.Value;
                    color = new Color((byte)(v >> 16), (byte)(v >> 8), (byte)v);
                    return true;
                }
            }
            return false;
        }

        // CSS names: those of the bundled .xshd files and the sixteen basic.
        private static KeyValuePair<string, uint>[] Names => new[]
        {
            new KeyValuePair<string, uint>("Black", 0x000000), new KeyValuePair<string, uint>("White", 0xFFFFFF),
            new KeyValuePair<string, uint>("Red", 0xFF0000), new KeyValuePair<string, uint>("Green", 0x008000),
            new KeyValuePair<string, uint>("Blue", 0x0000FF), new KeyValuePair<string, uint>("Yellow", 0xFFFF00),
            new KeyValuePair<string, uint>("Cyan", 0x00FFFF), new KeyValuePair<string, uint>("Aqua", 0x00FFFF),
            new KeyValuePair<string, uint>("Magenta", 0xFF00FF), new KeyValuePair<string, uint>("Fuchsia", 0xFF00FF),
            new KeyValuePair<string, uint>("Gray", 0x808080), new KeyValuePair<string, uint>("Grey", 0x808080),
            new KeyValuePair<string, uint>("Silver", 0xC0C0C0), new KeyValuePair<string, uint>("Maroon", 0x800000),
            new KeyValuePair<string, uint>("Olive", 0x808000), new KeyValuePair<string, uint>("Lime", 0x00FF00),
            new KeyValuePair<string, uint>("Navy", 0x000080), new KeyValuePair<string, uint>("Teal", 0x008080),
            new KeyValuePair<string, uint>("Purple", 0x800080), new KeyValuePair<string, uint>("DarkBlue", 0x00008B),
            new KeyValuePair<string, uint>("DarkMagenta", 0x8B008B), new KeyValuePair<string, uint>("DarkCyan", 0x008B8B),
            new KeyValuePair<string, uint>("DarkGreen", 0x006400), new KeyValuePair<string, uint>("DarkGray", 0xA9A9A9),
            new KeyValuePair<string, uint>("DarkGrey", 0xA9A9A9), new KeyValuePair<string, uint>("DarkRed", 0x8B0000),
            new KeyValuePair<string, uint>("DarkSlateGray", 0x2F4F4F), new KeyValuePair<string, uint>("SlateGray", 0x708090),
            new KeyValuePair<string, uint>("LightGray", 0xD3D3D3), new KeyValuePair<string, uint>("LightGrey", 0xD3D3D3),
            new KeyValuePair<string, uint>("MidnightBlue", 0x191970), new KeyValuePair<string, uint>("Sienna", 0xA0522D),
            new KeyValuePair<string, uint>("SaddleBrown", 0x8B4513), new KeyValuePair<string, uint>("Brown", 0xA52A2A),
            new KeyValuePair<string, uint>("Pink", 0xFFC0CB), new KeyValuePair<string, uint>("DeepPink", 0xFF1493),
            new KeyValuePair<string, uint>("Orange", 0xFFA500), new KeyValuePair<string, uint>("Gold", 0xFFD700),
        };

        public bool Equals(Color other) => R == other.R && G == other.G && B == other.B && A == other.A;

        public override bool Equals(object? obj) => obj is Color other && Equals(other);

        public override int GetHashCode() => (A << 24) | (R << 16) | (G << 8) | B;

        public override string ToString() => "#" + R.ToString("X2") + G.ToString("X2") + B.ToString("X2");

        public static bool operator ==(Color a, Color b) => a.Equals(b);

        public static bool operator !=(Color a, Color b) => !a.Equals(b);
    }

    public enum VisualRole
    {
        CodeKeyword,
        CodeType,
        CodeString,
        CodeNumber,
        CodeComment,
        CodePreprocessor,
        CodeOperator,
        CodePunctuation,
        CodeIdentifier,
        CodeFunctionName,
        CodeAttribute,
        CodeConstant,
    }
}
