using System;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;

namespace UsbTest
{
    // authenticatorMakeCredential — registering with the key, the CTAP2 way.
    //
    // The U2F registration this program could already do went through
    // CTAPHID_MSG and a fixed APDU, which is a message format from before CBOR
    // existed. This is the modern one: a CBOR map in, a CBOR map out, and the
    // first thing here that has to *write* CBOR rather than read it.
    internal static class CtapCredential
    {
        // The relying party this program registers under. Not a real domain on
        // purpose: a credential made here should be recognisable as a test one
        // and useless anywhere else.
        private const string RelyingPartyId = "sharpos.local";
        private const string RelyingPartyName = "SharpOS";
        private const string UserName = "tester";
        private const string UserDisplayName = "SharpOS tester";

        // COSE identifier for ECDSA over P-256, which every authenticator
        // supports and this key listed first.
        private const int AlgorithmEs256 = -7;

        /// <summary>The request, encoded and ready to send after the command byte.</summary>
        public static byte[] BuildRequest()
        {
            // Canonical mode, because the authenticator is entitled to reject
            // anything else - and because a writer that enforces it tells us
            // about a mistake here rather than letting the key do it in a
            // status code with no detail.
            var writer = new CborWriter(CborConformanceMode.Ctap2Canonical);

            writer.WriteStartMap(4);

            // 1: clientDataHash. In WebAuthn this is SHA-256 of the client data
            // JSON the browser built. There is no browser here, so the JSON is
            // ours and fixed - but it is hashed for real, because a hash of
            // nothing would have made the one new piece of cryptography in this
            // path untested.
            writer.WriteInt32(1);
            writer.WriteByteString(SHA256.HashData(Encoding.UTF8.GetBytes(ClientDataJson())));

            // 2: rp
            writer.WriteInt32(2);
            writer.WriteStartMap(2);
            writer.WriteTextString("id");
            writer.WriteTextString(RelyingPartyId);
            writer.WriteTextString("name");
            writer.WriteTextString(RelyingPartyName);
            writer.WriteEndMap();

            // 3: user. The id is opaque bytes to the authenticator; a fixed one
            // keeps repeated runs from filling the key with distinct users.
            writer.WriteInt32(3);
            writer.WriteStartMap(3);
            writer.WriteTextString("id");
            writer.WriteByteString(UserId());
            writer.WriteTextString("name");
            writer.WriteTextString(UserName);
            writer.WriteTextString("displayName");
            writer.WriteTextString(UserDisplayName);
            writer.WriteEndMap();

            // 4: pubKeyCredParams
            writer.WriteInt32(4);
            writer.WriteStartArray(1);
            writer.WriteStartMap(2);
            writer.WriteTextString("alg");
            writer.WriteInt32(AlgorithmEs256);
            writer.WriteTextString("type");
            writer.WriteTextString("public-key");
            writer.WriteEndMap();
            writer.WriteEndArray();

            writer.WriteEndMap();

            // Options are left out entirely rather than written as false. The
            // key reported makeCredUvNotRqd, which is the option that lets a
            // credential be made with a touch and no PIN; asking for uv would
            // turn this into a PIN exchange, and there is no PIN protocol here
            // yet (it needs ECDH over P-256, which the tree does not have).
            return writer.Encode();
        }

        /// <summary>What the key answered, in words.</summary>
        public static string Describe(byte[] cbor, int length)
        {
            var text = new StringBuilder();
            var reader = new CborReader(new ReadOnlyMemory<byte>(cbor, 0, length), CborConformanceMode.Lax);

            try
            {
                if (reader.PeekState() != CborReaderState.StartMap)
                    return "not a map - makeCredential should answer with one\n";

                reader.ReadStartMap();
                while (reader.PeekState() != CborReaderState.EndMap)
                {
                    if (reader.PeekState() != CborReaderState.UnsignedInteger)
                    {
                        reader.SkipValue();
                        reader.SkipValue();
                        continue;
                    }

                    ulong key = reader.ReadUInt64();
                    switch (key)
                    {
                        case 1:
                            text.Append("fmt (1): ");
                            text.Append(reader.ReadTextString());
                            text.Append('\n');
                            break;

                        case 2:
                            byte[] authData = reader.ReadByteString();
                            text.Append("authData (2): ");
                            text.Append(authData.Length.ToString());
                            text.Append(" bytes\n");
                            DescribeAuthData(text, authData);
                            break;

                        case 3:
                            text.Append("attStmt (3):\n");
                            text.Append(CtapInfo.RenderValue(reader, 1));
                            break;

                        default:
                            text.Append("key (");
                            text.Append(key.ToString());
                            text.Append("):\n");
                            text.Append(CtapInfo.RenderValue(reader, 1));
                            break;
                    }
                }
                reader.ReadEndMap();
            }
            catch (Exception e)
            {
                text.Append("[decode failed] ");
                text.Append(e.Message);
                text.Append('\n');
            }

            return text.ToString();
        }

        // The authenticator data, which is not CBOR at all: a fixed header
        // followed by the credential. Worth unpacking by hand, because the
        // counter and the flags are what say whether the key was really touched.
        private static void DescribeAuthData(StringBuilder text, byte[] data)
        {
            if (data.Length < 37)
            {
                text.Append("    (too short to hold the header)\n");
                return;
            }

            text.Append("    rpIdHash:  ");
            for (int i = 0; i < 8; i++) text.Append(Format.Hex2(data[i]));
            text.Append("...\n");

            byte flags = data[32];
            text.Append("    flags:     0x");
            text.Append(Format.Hex2(flags));
            if ((flags & 0x01) != 0) text.Append("  user-present");
            if ((flags & 0x04) != 0) text.Append("  user-verified");
            if ((flags & 0x40) != 0) text.Append("  attested-credential");
            if ((flags & 0x80) != 0) text.Append("  extensions");
            text.Append('\n');

            uint counter = ((uint)data[33] << 24) | ((uint)data[34] << 16)
                         | ((uint)data[35] << 8) | data[36];
            text.Append("    signCount: ");
            text.Append(counter.ToString());
            text.Append('\n');

            if ((flags & 0x40) == 0 || data.Length < 55) return;

            text.Append("    aaguid:    ");
            for (int i = 0; i < 16; i++)
            {
                if (i == 4 || i == 6 || i == 8 || i == 10) text.Append('-');
                text.Append(Format.Hex2(data[37 + i]));
            }
            text.Append('\n');

            int idLength = (data[53] << 8) | data[54];
            text.Append("    credentialId: ");
            text.Append(idLength.ToString());
            text.Append(" bytes\n");
        }

        /// <summary>The client data a browser would have built.</summary>
        private static string ClientDataJson()
            => "{\"type\":\"webauthn.create\",\"challenge\":\"c2hhcnBvcy10ZXN0\",\"origin\":\"https://sharpos.local\"}";

        private static byte[] UserId()
        {
            byte[] id = new byte[16];
            for (int i = 0; i < id.Length; i++) id[i] = (byte)(0x50 + i);
            return id;
        }
    }
}
