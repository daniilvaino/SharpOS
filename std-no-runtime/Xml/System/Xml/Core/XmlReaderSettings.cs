// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Xml.Schema;

namespace System.Xml
{
    // XmlReaderSettings class specifies basic features of an XmlReader.
    public sealed class XmlReaderSettings
    {
        internal static readonly XmlReaderSettings s_defaultReaderSettings = new() { ReadOnly = true };
        private bool _useAsync;
        private XmlNameTable? _nameTable;
        private XmlResolver? _xmlResolver;
        private int _lineNumberOffset;
        private int _linePositionOffset;
        private ConformanceLevel _conformanceLevel;
        private bool _checkCharacters;
        private long _maxCharactersInDocument;
        private long _maxCharactersFromEntities;
        private bool _ignoreWhitespace;
        private bool _ignorePIs;
        private bool _ignoreComments;
        private DtdProcessing _dtdProcessing;
        private ValidationType _validationType;
        // SharpOS cut: _validationFlags, _schemas, _valEventHandler and the
        // _addValidationFunc delegate — no DTD or XSD validation here.
        private bool _closeInput;

        public XmlReaderSettings()
        {
            Initialize();
        }

        public bool Async
        {
            get => _useAsync;
            set
            {
                CheckReadOnly();
                _useAsync = value;
            }
        }

        public XmlNameTable? NameTable
        {
            get => _nameTable;
            set
            {
                CheckReadOnly();
                _nameTable = value;
            }
        }

        internal bool IsXmlResolverSet
        {
            get;
            set; // keep set internal as we need to call it from the schema validation code
        }

        public XmlResolver? XmlResolver
        {
            set
            {
                CheckReadOnly();
                _xmlResolver = value;
                IsXmlResolverSet = true;
            }
        }

        internal XmlResolver? GetXmlResolver()
        {
            return _xmlResolver;
        }

        //This is used by get XmlResolver in Xsd.
        //Check if the config set to prohibit default resolver
        //notice we must keep GetXmlResolver() to avoid dead lock when init System.Config.ConfigurationManager
        internal XmlResolver? GetXmlResolver_CheckConfig()
        {
            return IsXmlResolverSet ? _xmlResolver : null; // SharpOS cut: LocalAppContextSwitches.AllowDefaultResolver (off by default upstream too)
        }

        public int LineNumberOffset
        {
            get => _lineNumberOffset;
            set
            {
                CheckReadOnly();
                _lineNumberOffset = value;
            }
        }

        public int LinePositionOffset
        {
            get => _linePositionOffset;
            set
            {
                CheckReadOnly();
                _linePositionOffset = value;
            }
        }

        public ConformanceLevel ConformanceLevel
        {
            get => _conformanceLevel;
            set
            {
                CheckReadOnly();

                if ((uint)value > (uint)ConformanceLevel.Document)
                {
                    ThrowArgumentOutOfRangeException(nameof(value));
                }
                _conformanceLevel = value;
            }
        }

        public bool CheckCharacters
        {
            get => _checkCharacters;
            set
            {
                CheckReadOnly();
                _checkCharacters = value;
            }
        }

        public long MaxCharactersInDocument
        {
            get => _maxCharactersInDocument;
            set
            {
                CheckReadOnly();
                if (value < 0)
                {
                    ThrowArgumentOutOfRangeException(nameof(value));
                }
                _maxCharactersInDocument = value;
            }
        }

        public long MaxCharactersFromEntities
        {
            get => _maxCharactersFromEntities;
            set
            {
                CheckReadOnly();
                if (value < 0)
                {
                    ThrowArgumentOutOfRangeException(nameof(value));
                }
                _maxCharactersFromEntities = value;
            }
        }

        public bool IgnoreWhitespace
        {
            get => _ignoreWhitespace;
            set
            {
                CheckReadOnly();
                _ignoreWhitespace = value;
            }
        }

        public bool IgnoreProcessingInstructions
        {
            get => _ignorePIs;
            set
            {
                CheckReadOnly();
                _ignorePIs = value;
            }
        }

        public bool IgnoreComments
        {
            get => _ignoreComments;
            set
            {
                CheckReadOnly();
                _ignoreComments = value;
            }
        }

        [Obsolete("XmlReaderSettings.ProhibitDtd has been deprecated. Use DtdProcessing instead.")]
        public bool ProhibitDtd
        {
            get => _dtdProcessing == DtdProcessing.Prohibit;
            set
            {
                CheckReadOnly();
                _dtdProcessing = value ? DtdProcessing.Prohibit : DtdProcessing.Parse;
            }
        }

        public DtdProcessing DtdProcessing
        {
            get => _dtdProcessing;
            set
            {
                CheckReadOnly();

                if ((uint)value > (uint)DtdProcessing.Parse)
                {
                    ThrowArgumentOutOfRangeException(nameof(value));
                }
                _dtdProcessing = value;
            }
        }

        public bool CloseInput
        {
            get => _closeInput;
            set
            {
                CheckReadOnly();
                _closeInput = value;
            }
        }

        public ValidationType ValidationType
        {
            get => _validationType;
            set
            {
                CheckReadOnly();

                if ((uint)value > (uint)ValidationType.Schema)
                {
                    ThrowArgumentOutOfRangeException(nameof(value));
                }
                // SharpOS cut: the validating readers (XmlValidatingReaderImpl, XsdValidatingReader).
                if (value != ValidationType.None)
                {
                    throw new NotSupportedException("XML validation (DTD, XSD) is not supported by SharpOS's System.Xml.");
                }
                _validationType = value;
            }
        }

        // SharpOS cut: ValidationFlags, Schemas and the ValidationEventHandler event
        // (XmlSchemaValidationFlags, XmlSchemaSet) — no validation here.

        internal bool ReadOnly { get; set; }

        public void Reset()
        {
            CheckReadOnly();
            Initialize();
        }

        public XmlReaderSettings Clone()
        {
            XmlReaderSettings clonedSettings = (XmlReaderSettings)MemberwiseClone();
            clonedSettings.ReadOnly = false;
            return clonedSettings;
        }

        // SharpOS cut: GetEventHandler (see ValidationEventHandler).

        internal XmlReader CreateReader(string inputUri, XmlParserContext? inputContext)
        {
            ArgumentException.ThrowIfNullOrEmpty(inputUri);

            // resolve and open the url
            XmlResolver tmpResolver = GetXmlResolver() ?? GetDefaultPermissiveResolver();

            // create text XML reader
            XmlReader reader = new XmlTextReaderImpl(inputUri, this, inputContext, tmpResolver);

            // wrap with validating reader
            if (ValidationType != ValidationType.None)
            {
                reader = AddValidation(reader);
            }

            // SharpOS cut: the async check wrapper (XmlAsyncCheckReader) — the async API is not ported.

            return reader;
        }

        internal XmlReader CreateReader(Stream input, Uri? baseUri, string? baseUriString, XmlParserContext? inputContext)
        {
            ArgumentNullException.ThrowIfNull(input);

            baseUriString ??= baseUri?.ToString() ?? string.Empty;

            // create text XML reader
            XmlReader reader = new XmlTextReaderImpl(input, null, 0, this, baseUri, baseUriString, inputContext, _closeInput);

            // wrap with validating reader
            if (ValidationType != ValidationType.None)
            {
                reader = AddValidation(reader);
            }

            // SharpOS cut: the async check wrapper (XmlAsyncCheckReader) — the async API is not ported.

            return reader;
        }

        internal XmlReader CreateReader(TextReader input, string? baseUriString, XmlParserContext? inputContext)
        {
            ArgumentNullException.ThrowIfNull(input);

            baseUriString ??= string.Empty;

            // create xml text reader
            XmlReader reader = new XmlTextReaderImpl(input, this, baseUriString, inputContext);

            // wrap with validating reader
            if (ValidationType != ValidationType.None)
            {
                reader = AddValidation(reader);
            }

            // SharpOS cut: the async check wrapper (XmlAsyncCheckReader) — the async API is not ported.

            return reader;
        }

        internal XmlReader CreateReader(XmlReader reader)
        {
            ArgumentNullException.ThrowIfNull(reader);

            return AddValidationAndConformanceWrapper(reader);
        }

        private void CheckReadOnly([CallerMemberName] string? propertyName = null)
        {
            if (ReadOnly)
            {
                throw new XmlException(SR.Xml_ReadOnlyProperty, $"{GetType().Name}.{propertyName}");
            }
        }

        private void Initialize(XmlResolver? resolver = null)
        {
            _nameTable = null;
            _xmlResolver = resolver;
            // limit the entity resolving to 10 million character. the caller can still
            // override it to any other value or set it to zero for unlimited it
            _maxCharactersFromEntities = (long)1e7;
            _lineNumberOffset = 0;
            _linePositionOffset = 0;
            _checkCharacters = true;
            _conformanceLevel = ConformanceLevel.Document;
            _ignoreWhitespace = false;
            _ignorePIs = false;
            _ignoreComments = false;
            _dtdProcessing = DtdProcessing.Prohibit;
            _closeInput = false;
            _maxCharactersInDocument = 0;
            _validationType = ValidationType.None;
            _useAsync = false;
            ReadOnly = false;
            IsXmlResolverSet = false;
        }

        // SharpOS cut: the validating readers. ValidationType can only be None
        // (its setter refuses the rest), so adding validation is nothing, and
        // AddValidationAndConformanceInternal, AddValidationInternal and
        // CreateDtdValidatingReader go.
        internal XmlReader AddValidation(XmlReader reader) => reader;

        private XmlReader AddValidationAndConformanceWrapper(XmlReader reader) => AddConformanceWrapper(reader);
        private XmlReader AddConformanceWrapper(XmlReader baseReader)
        {
            XmlReaderSettings? baseReaderSettings = baseReader.Settings;
            bool checkChars = false;
            bool noWhitespace = false;
            bool noComments = false;
            bool noPIs = false;
            DtdProcessing dtdProc = (DtdProcessing)(-1);
            bool needWrap = false;

            if (baseReaderSettings == null)
            {
#pragma warning disable 618

                if (_conformanceLevel != ConformanceLevel.Auto && _conformanceLevel != XmlReader.GetV1ConformanceLevel(baseReader))
                {
                    throw new InvalidOperationException(SR.Format(SR.Xml_IncompatibleConformanceLevel, _conformanceLevel.ToString()));
                }

                // get the V1 XmlTextReader ref
                XmlTextReader? v1XmlTextReader = baseReader as XmlTextReader;
                // SharpOS cut: the XmlValidatingReader branch (no validating readers).

                // assume the V1 readers already do all conformance checking;
                // wrap only if IgnoreWhitespace, IgnoreComments, IgnoreProcessingInstructions or ProhibitDtd is true;
                if (_ignoreWhitespace)
                {
                    WhitespaceHandling wh = WhitespaceHandling.All;
                    // special-case our V1 readers to see if whey already filter whitespace
                    if (v1XmlTextReader != null)
                    {
                        wh = v1XmlTextReader.WhitespaceHandling;
                    }
                    if (wh == WhitespaceHandling.All)
                    {
                        noWhitespace = true;
                        needWrap = true;
                    }
                }
                if (_ignoreComments)
                {
                    noComments = true;
                    needWrap = true;
                }
                if (_ignorePIs)
                {
                    noPIs = true;
                    needWrap = true;
                }
                // DTD processing
                DtdProcessing baseDtdProcessing = DtdProcessing.Parse;
                if (v1XmlTextReader != null)
                {
                    baseDtdProcessing = v1XmlTextReader.DtdProcessing;
                }

                if ((_dtdProcessing == DtdProcessing.Prohibit && baseDtdProcessing != DtdProcessing.Prohibit) ||
                    (_dtdProcessing == DtdProcessing.Ignore && baseDtdProcessing == DtdProcessing.Parse))
                {
                    dtdProc = _dtdProcessing;
                    needWrap = true;
                }
#pragma warning restore 618
            }
            else
            {
                if (_conformanceLevel != baseReaderSettings.ConformanceLevel && _conformanceLevel != ConformanceLevel.Auto)
                {
                    throw new InvalidOperationException(SR.Format(SR.Xml_IncompatibleConformanceLevel, _conformanceLevel.ToString()));
                }
                if (_checkCharacters && !baseReaderSettings.CheckCharacters)
                {
                    checkChars = true;
                    needWrap = true;
                }
                if (_ignoreWhitespace && !baseReaderSettings.IgnoreWhitespace)
                {
                    noWhitespace = true;
                    needWrap = true;
                }
                if (_ignoreComments && !baseReaderSettings.IgnoreComments)
                {
                    noComments = true;
                    needWrap = true;
                }
                if (_ignorePIs && !baseReaderSettings.IgnoreProcessingInstructions)
                {
                    noPIs = true;
                    needWrap = true;
                }

                if ((_dtdProcessing == DtdProcessing.Prohibit && baseReaderSettings.DtdProcessing != DtdProcessing.Prohibit) ||
                    (_dtdProcessing == DtdProcessing.Ignore && baseReaderSettings.DtdProcessing == DtdProcessing.Parse))
                {
                    dtdProc = _dtdProcessing;
                    needWrap = true;
                }
            }

            if (needWrap)
            {
                if (baseReader is IXmlNamespaceResolver readerAsNsResolver)
                {
                    return new XmlCharCheckingReaderWithNS(baseReader, readerAsNsResolver, checkChars, noWhitespace, noComments, noPIs, dtdProc);
                }

                return new XmlCharCheckingReader(baseReader, checkChars, noWhitespace, noComments, noPIs, dtdProc);
            }

            return baseReader;
        }

        internal static XmlResolver GetDefaultPermissiveResolver()
        {
            return XmlResolver.FileSystemResolver; // SharpOS cut: XmlUrlResolver (no networking) and the switch that picks it
        }

        [DoesNotReturn]
        [StackTraceHidden]
        private static void ThrowArgumentOutOfRangeException(string paramName)
        {
            throw new ArgumentOutOfRangeException(paramName);
        }
    }
}
