// System.Text.DecoderFallbackException / EncoderFallbackException — то, чем
// кодировка сообщает, что перевод невозможен: байты, не складывающиеся в
// символ, и символы, которых в целевой кодировке нет.
//
// Оба наследуют ArgumentException, как в BCL. Существуют здесь потому, что их
// ловят: System.Formats.Cbor заворачивает их в CborContentException, чтобы
// "строка внутри документа испорчена" не выглядело как "вызывающий передал
// плохой аргумент".

namespace System.Text
{
    public sealed class DecoderFallbackException : ArgumentException
    {
        public DecoderFallbackException()
            : base("Unable to translate bytes into the target encoding.") { }

        public DecoderFallbackException(string message) : base(message) { }

        public DecoderFallbackException(string message, Exception innerException)
            : base(message, innerException) { }

        public DecoderFallbackException(string message, byte[] bytesUnknown, int index)
            : base(message)
        {
            BytesUnknown = bytesUnknown;
            Index = index;
        }

        public byte[] BytesUnknown { get; }
        public int Index { get; }
    }

    public sealed class EncoderFallbackException : ArgumentException
    {
        public EncoderFallbackException()
            : base("Unable to translate characters into the target encoding.") { }

        public EncoderFallbackException(string message) : base(message) { }

        public EncoderFallbackException(string message, Exception innerException)
            : base(message, innerException) { }

        public char CharUnknown { get; }
        public char CharUnknownHigh { get; }
        public char CharUnknownLow { get; }
        public int Index { get; }

        public bool IsUnknownSurrogate() => CharUnknownHigh != '\0';
    }
}
