using System;
using System.Formats.Cbor;
using System.Text;

namespace UsbTest
{
    // authenticatorGetInfo, in words.
    //
    // The reply is a CBOR map whose keys are numbers, so a hex dump of it is
    // exactly as informative as the reader's memory of the specification. This
    // turns it into lines a person can read without one.
    //
    // Every name is printed together with the key it decodes, as
    // "versions (1)". That is not decoration: these names come from CTAP 2.2,
    // this key answers with fields past what that version defines, and a name
    // this program guessed wrong would otherwise be indistinguishable from one
    // the key actually meant. With the number beside it, a wrong name is
    // visible and the raw truth is never lost.
    internal static class CtapInfo
    {
        /// <summary>The whole reply, one field per line.</summary>
        /// <remarks>
        /// Read twice when it has to be. CTAP2 defines a canonical CBOR of its
        /// own, and reading in that mode is how a key with a sloppy encoder
        /// gets found out - but a reader that refuses the document is useless
        /// to someone who just wants to see what the key said. So: canonical
        /// first, and if that is rejected, lax with the rejection printed
        /// above the data. Both facts survive, and neither hides the other.
        /// </remarks>
        public static string Describe(byte[] cbor, int length)
        {
            string strict = Render(cbor, length, CborConformanceMode.Ctap2Canonical, out bool refused);
            if (!refused) return strict;

            return "[not canonical CTAP2 CBOR] " + strict.Trim() + "\n"
                 + "reading it anyway:\n\n"
                 + Render(cbor, length, CborConformanceMode.Lax, out _);
        }

        private static string Render(byte[] cbor, int length, CborConformanceMode mode, out bool refused)
        {
            var text = new StringBuilder();
            refused = false;

            var reader = new CborReader(new ReadOnlyMemory<byte>(cbor, 0, length), mode);

            try
            {
                if (reader.PeekState() != CborReaderState.StartMap)
                {
                    text.Append("not a map - getInfo should answer with one\n");
                    Value(text, reader, 0);
                    return text.ToString();
                }

                int? count = reader.ReadStartMap();
                text.Append("getInfo: ");
                text.Append(count.HasValue ? count.Value.ToString() : "?");
                text.Append(" fields\n\n");

                while (reader.PeekState() != CborReaderState.EndMap)
                {
                    // A top-level key is a number here; anything else is a
                    // reply this program does not understand, and saying so
                    // beats rendering it as though it did.
                    if (reader.PeekState() != CborReaderState.UnsignedInteger)
                    {
                        text.Append("  (unexpected key) ");
                        Value(text, reader, 1);
                        Value(text, reader, 1);
                        continue;
                    }

                    ulong key = reader.ReadUInt64();
                    text.Append(Name(key));
                    text.Append(" (");
                    text.Append(key.ToString());
                    text.Append("):");

                    Field(text, reader, key);
                }

                reader.ReadEndMap();
            }
            catch (CborContentException e)
            {
                // The encoding itself was refused. Which of the two passes
                // this is decides whether that is news or the end of the road.
                refused = true;
                return e.Message;
            }
            catch (Exception e)
            {
                text.Append("\n[decode failed] ");
                text.Append(e.Message);
                text.Append('\n');
            }

            return text.ToString();
        }

        // The fields worth a shape of their own; everything else goes through
        // the general renderer.
        private static void Field(StringBuilder text, CborReader reader, ulong key)
        {
            switch (key)
            {
                case 3:     // aaguid: sixteen bytes that name the model
                    text.Append(' ');
                    text.Append(Uuid(reader.ReadByteString()));
                    text.Append('\n');
                    return;

                case 10:    // algorithms: a list of {alg, type}
                    text.Append('\n');
                    Algorithms(text, reader);
                    return;

                case 14:    // firmwareVersion: read as hex by its makers
                    ulong version = reader.ReadUInt64();
                    text.Append(' ');
                    text.Append(version.ToString());
                    text.Append("  (0x");
                    text.Append(Format.Hex8((uint)version));
                    text.Append(")\n");
                    return;

                default:
                    text.Append('\n');
                    Value(text, reader, 1);
                    return;
            }
        }

        private static void Algorithms(StringBuilder text, CborReader reader)
        {
            if (reader.PeekState() != CborReaderState.StartArray)
            {
                Value(text, reader, 1);
                return;
            }

            reader.ReadStartArray();
            while (reader.PeekState() != CborReaderState.EndArray)
            {
                if (reader.PeekState() != CborReaderState.StartMap)
                {
                    Value(text, reader, 1);
                    continue;
                }

                reader.ReadStartMap();
                string type = "";
                long alg = 0;
                bool haveAlg = false;

                while (reader.PeekState() != CborReaderState.EndMap)
                {
                    string name = reader.ReadTextString();
                    if (name == "alg") { alg = reader.ReadInt64(); haveAlg = true; }
                    else if (name == "type") { type = reader.ReadTextString(); }
                    else { reader.SkipValue(); }
                }
                reader.ReadEndMap();

                text.Append("    ");
                text.Append(haveAlg ? Cose(alg) : "?");
                if (type.Length != 0)
                {
                    text.Append("  (");
                    text.Append(type);
                    text.Append(')');
                }
                text.Append('\n');
            }
            reader.ReadEndArray();
        }

        /// <summary>One value, rendered as the getInfo view renders them.</summary>
        /// <remarks>
        /// Open to the rest of the program so that a reply this file knows
        /// nothing about - a makeCredential attestation statement, say - still
        /// looks like every other map here instead of like a second opinion.
        /// </remarks>
        public static string RenderValue(CborReader reader, int depth)
        {
            var text = new StringBuilder();
            Value(text, reader, depth);
            return text.ToString();
        }

        // Anything at all, indented. Arrays of short text go on one line,
        // because a list of seven extension names down the page is a list
        // nobody reads to the end.
        private static void Value(StringBuilder text, CborReader reader, int depth)
        {
            switch (reader.PeekState())
            {
                case CborReaderState.UnsignedInteger:
                    Indent(text, depth); text.Append(reader.ReadUInt64().ToString()); text.Append('\n');
                    return;

                case CborReaderState.NegativeInteger:
                    Indent(text, depth); text.Append(reader.ReadInt64().ToString()); text.Append('\n');
                    return;

                case CborReaderState.TextString:
                    Indent(text, depth); text.Append(reader.ReadTextString()); text.Append('\n');
                    return;

                case CborReaderState.ByteString:
                    byte[] bytes = reader.ReadByteString();
                    Indent(text, depth);
                    text.Append(bytes.Length.ToString());
                    text.Append(" bytes: ");
                    text.Append(Hex(bytes, 16));
                    text.Append('\n');
                    return;

                case CborReaderState.Boolean:
                    Indent(text, depth); text.Append(reader.ReadBoolean() ? "yes" : "no"); text.Append('\n');
                    return;

                case CborReaderState.Null:
                    reader.ReadNull(); Indent(text, depth); text.Append("(null)\n");
                    return;

                case CborReaderState.StartArray:
                    Array(text, reader, depth);
                    return;

                case CborReaderState.StartMap:
                    Map(text, reader, depth);
                    return;

                default:
                    // Floats, tags, simple values: present in CBOR, absent from
                    // CTAP2's canonical subset. Skipped by name rather than
                    // guessed at.
                    Indent(text, depth);
                    text.Append('(');
                    text.Append(StateName(reader.PeekState()));
                    text.Append(", skipped)\n");
                    reader.SkipValue();
                    return;
            }
        }

        private static void Array(StringBuilder text, CborReader reader, int depth)
        {
            reader.ReadStartArray();

            // Gather first, so the decision to put them on one line can be made
            // after seeing them rather than guessed from the count.
            var items = new System.Collections.Generic.List<string>();
            bool allShortText = true;

            while (reader.PeekState() != CborReaderState.EndArray)
            {
                if (reader.PeekState() == CborReaderState.TextString)
                {
                    string s = reader.ReadTextString();
                    items.Add(s);
                    if (s.Length > 24) allShortText = false;
                }
                else if (reader.PeekState() == CborReaderState.UnsignedInteger)
                {
                    items.Add(reader.ReadUInt64().ToString());
                }
                else
                {
                    allShortText = false;
                    items.Add(null);          // rendered below, in place
                    Value(text, reader, depth + 1);
                }
            }
            reader.ReadEndArray();

            if (allShortText && items.Count != 0)
            {
                Indent(text, depth);
                for (int i = 0; i < items.Count; i++)
                {
                    if (i != 0) text.Append(", ");
                    text.Append(items[i]);
                }
                text.Append('\n');
                return;
            }

            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] == null) continue;      // already written
                Indent(text, depth); text.Append(items[i]); text.Append('\n');
            }
        }

        private static void Map(StringBuilder text, CborReader reader, int depth)
        {
            reader.ReadStartMap();
            while (reader.PeekState() != CborReaderState.EndMap)
            {
                Indent(text, depth);

                if (reader.PeekState() == CborReaderState.TextString) text.Append(reader.ReadTextString());
                else if (reader.PeekState() == CborReaderState.UnsignedInteger) text.Append(reader.ReadUInt64().ToString());
                else { reader.SkipValue(); text.Append("(key skipped)"); }

                text.Append(": ");

                // Simple values go on the same line as their key; anything
                // with a shape of its own starts below it.
                CborReaderState state = reader.PeekState();
                if (state == CborReaderState.Boolean) { text.Append(reader.ReadBoolean() ? "yes" : "no"); text.Append('\n'); }
                else if (state == CborReaderState.UnsignedInteger) { text.Append(reader.ReadUInt64().ToString()); text.Append('\n'); }
                else if (state == CborReaderState.NegativeInteger) { text.Append(reader.ReadInt64().ToString()); text.Append('\n'); }
                else if (state == CborReaderState.TextString) { text.Append(reader.ReadTextString()); text.Append('\n'); }
                else { text.Append('\n'); Value(text, reader, depth + 1); }
            }
            reader.ReadEndMap();
        }

        private static void Indent(StringBuilder text, int depth)
        {
            for (int i = 0; i < depth; i++) text.Append("    ");
        }

        /// <summary>A reader state, by name.</summary>
        /// <remarks>
        /// Spelled out rather than taken from ToString: enums in an app here
        /// carry no metadata, so ToString answers with the number. A line
        /// reading "(7, skipped)" is not worth printing.
        /// </remarks>
        private static string StateName(CborReaderState state)
        {
            switch (state)
            {
                case CborReaderState.Undefined: return "undefined";
                case CborReaderState.UnsignedInteger: return "unsigned";
                case CborReaderState.NegativeInteger: return "negative";
                case CborReaderState.ByteString: return "bytes";
                case CborReaderState.TextString: return "text";
                case CborReaderState.StartIndefiniteLengthByteString: return "indefinite bytes";
                case CborReaderState.StartIndefiniteLengthTextString: return "indefinite text";
                case CborReaderState.StartArray: return "array";
                case CborReaderState.StartMap: return "map";
                case CborReaderState.Tag: return "tag";
                case CborReaderState.SimpleValue: return "simple value";
                case CborReaderState.HalfPrecisionFloat: return "half float";
                case CborReaderState.SinglePrecisionFloat: return "float";
                case CborReaderState.DoublePrecisionFloat: return "double";
                case CborReaderState.Null: return "null";
                case CborReaderState.Boolean: return "boolean";
                case CborReaderState.Finished: return "finished";
                default: return "state " + ((int)state).ToString();
            }
        }

        /// <summary>The getInfo keys CTAP 2.2 names.</summary>
        /// <remarks>
        /// Printed beside the number, never instead of it. This key answers
        /// with fields beyond 2.2, and an unnamed number is a better answer
        /// than a name this table invented.
        /// </remarks>
        private static string Name(ulong key)
        {
            switch (key)
            {
                case 1: return "versions";
                case 2: return "extensions";
                case 3: return "aaguid";
                case 4: return "options";
                case 5: return "maxMsgSize";
                case 6: return "pinUvAuthProtocols";
                case 7: return "maxCredentialCountInList";
                case 8: return "maxCredentialIdLength";
                case 9: return "transports";
                case 10: return "algorithms";
                case 11: return "maxSerializedLargeBlobArray";
                case 12: return "forcePINChange";
                case 13: return "minPINLength";
                case 14: return "firmwareVersion";
                case 15: return "maxCredBlobLength";
                case 16: return "maxRPIDsForSetMinPINLength";
                case 17: return "preferredPlatformUvAttempts";
                case 18: return "uvModality";
                case 19: return "certifications";
                case 20: return "remainingDiscoverableCredentials";
                case 21: return "vendorPrototypeConfigCommands";
                case 22: return "attestationFormats";
                case 23: return "uvCountSinceLastPinEntry";
                case 24: return "longTouchForReset";
                case 25: return "encIdentifier";
                case 26: return "transportsForReset";
                case 27: return "pinComplexityPolicy";
                case 28: return "pinComplexityPolicyURL";
                case 29: return "maxPINLength";
                default: return "key";
            }
        }

        /// <summary>COSE algorithm identifiers, as names.</summary>
        private static string Cose(long alg)
        {
            switch (alg)
            {
                case -7: return "ES256";
                case -8: return "EdDSA";
                case -35: return "ES384";
                case -36: return "ES512";
                case -37: return "PS256";
                case -47: return "ES256K";
                case -257: return "RS256";
                default: return "alg " + alg.ToString();
            }
        }

        private static string Uuid(byte[] bytes)
        {
            if (bytes == null || bytes.Length != 16) return Hex(bytes, 16);

            var text = new StringBuilder();
            for (int i = 0; i < 16; i++)
            {
                if (i == 4 || i == 6 || i == 8 || i == 10) text.Append('-');
                text.Append(Format.Hex2(bytes[i]));
            }
            return text.ToString();
        }

        private static string Hex(byte[] bytes, int max)
        {
            if (bytes == null) return "";

            var text = new StringBuilder();
            int n = bytes.Length < max ? bytes.Length : max;
            for (int i = 0; i < n; i++)
            {
                if (i != 0) text.Append(' ');
                text.Append(Format.Hex2(bytes[i]));
            }
            if (n < bytes.Length) text.Append(" ...");
            return text.ToString();
        }
    }
}
