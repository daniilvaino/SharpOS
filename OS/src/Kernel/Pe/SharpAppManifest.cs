using System;
using TurboXml;

namespace OS.Kernel.Pe
{
    /// <summary>
    /// The SharpOS record inside an application manifest.
    /// </summary>
    /// <remarks>
    /// The manifest is the XML every PE already carries as an RT_MANIFEST
    /// resource. Ours adds one element in its own namespace, so the Windows
    /// half and this half never have to know about each other:
    ///
    ///   &lt;sharpos xmlns="urn:sharpos:app.v1"&gt;
    ///     &lt;app schema="1" abi="3" serviceAbi="0" /&gt;
    ///   &lt;/sharpos&gt;
    /// </remarks>
    internal struct SharpAppManifest
    {
        public const string Namespace = "urn:sharpos:app.v1";

        /// <summary>Shape of the record, not the ABI it describes.</summary>
        public uint Schema;

        /// <summary>Service-table ABI the app was built against.</summary>
        public uint Abi;

        /// <summary>Calling convention of the service thunks.</summary>
        public uint ServiceAbi;

        /// <summary>assemblyIdentity/@name — the app's own name.</summary>
        public string? Name;

        /// <summary>True once an app element was seen and understood.</summary>
        public bool Found;

        /// <summary>What went wrong, when nothing was found.</summary>
        public string? Error;

        public static SharpAppManifest Parse(string manifestXml)
        {
            var handler = new Handler();

            try
            {
                XmlParser.Parse(manifestXml, ref handler);
            }
            catch (Exception e)
            {
                // A manifest that does not parse is not a manifest. Reported
                // rather than swallowed: the caller refuses the launch, and the
                // reason is the difference between "old image" and "broken
                // build".
                handler.Result.Found = false;
                handler.Result.Error = e.Message;
            }

            return handler.Result;
        }

        /// <summary>
        /// Collects the record while the parser walks the document.
        /// </summary>
        /// <remarks>
        /// A struct, and passed by reference, because the parser is generic over
        /// the handler: nothing is boxed and nothing is allocated per element.
        ///
        /// Every member of the interface is implemented, including the ones this
        /// handler ignores — default implementations were dropped from the
        /// vendored interface (see vendor/TurboXml/PROVENANCE.md).
        /// </remarks>
        private struct Handler : IXmlReadHandler
        {
            public SharpAppManifest Result;

            private bool _inSharpOsNamespace;
            private bool _inAppElement;
            private bool _inIdentityElement;

            public void OnBeginTag(ReadOnlySpan<char> name, int line, int column)
            {
                // Matched on the local name after any prefix. The generated
                // manifest declares our namespace as the default one on
                // <sharpos>, so in practice there is no prefix — but a manifest
                // someone edited by hand may well carry one.
                ReadOnlySpan<char> local = LocalName(name);

                if (local.SequenceEqual("sharpos".AsSpan()))
                    _inSharpOsNamespace = true;
                else if (local.SequenceEqual("app".AsSpan()))
                    _inAppElement = _inSharpOsNamespace;
                else if (local.SequenceEqual("assemblyIdentity".AsSpan()))
                    _inIdentityElement = true;
            }

            public void OnAttribute(ReadOnlySpan<char> name, ReadOnlySpan<char> value,
                int nameLine, int nameColumn, int valueLine, int valueColumn)
            {
                ReadOnlySpan<char> local = LocalName(name);

                if (_inIdentityElement && local.SequenceEqual("name".AsSpan()))
                {
                    Result.Name = value.ToString();
                    return;
                }

                if (!_inAppElement)
                    return;

                if (local.SequenceEqual("schema".AsSpan()))
                {
                    Result.Schema = ParseUInt(value);
                    Result.Found = true;
                }
                else if (local.SequenceEqual("abi".AsSpan()))
                {
                    Result.Abi = ParseUInt(value);
                }
                else if (local.SequenceEqual("serviceAbi".AsSpan()))
                {
                    Result.ServiceAbi = ParseUInt(value);
                }
            }

            public void OnEndTagEmpty()
            {
                _inAppElement = false;
                _inIdentityElement = false;
            }

            public void OnEndTag(ReadOnlySpan<char> name, int line, int column)
            {
                ReadOnlySpan<char> local = LocalName(name);

                if (local.SequenceEqual("sharpos".AsSpan()))
                    _inSharpOsNamespace = false;
                else if (local.SequenceEqual("app".AsSpan()))
                    _inAppElement = false;
                else if (local.SequenceEqual("assemblyIdentity".AsSpan()))
                    _inIdentityElement = false;
            }

            public void OnError(string message, int line, int column)
            {
                Result.Found = false;
                Result.Error = message;
            }

            // Nothing in a manifest reaches this half, but the interface has no
            // default implementations here.
            public void OnXmlDeclaration(ReadOnlySpan<char> version, ReadOnlySpan<char> encoding,
                ReadOnlySpan<char> standalone, int line, int column) { }

            public void OnProcessingInstruction(ReadOnlySpan<char> target, ReadOnlySpan<char> data,
                int line, int column) { }

            public void OnText(ReadOnlySpan<char> text, int line, int column) { }

            public void OnComment(ReadOnlySpan<char> comment, int line, int column) { }

            public void OnCData(ReadOnlySpan<char> cdata, int line, int column) { }

            private static ReadOnlySpan<char> LocalName(ReadOnlySpan<char> name)
            {
                int colon = name.IndexOf(':');
                return colon < 0 ? name : name.Slice(colon + 1);
            }

            private static uint ParseUInt(ReadOnlySpan<char> value)
            {
                uint result = 0;
                for (int i = 0; i < value.Length; i++)
                {
                    char c = value[i];
                    if (c < '0' || c > '9')
                        return result;

                    result = (result * 10) + (uint)(c - '0');
                }

                return result;
            }
        }
    }
}
