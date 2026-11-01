// AesImplementation, the platform part — in place of BCL's
// AesImplementation.Windows.cs (BCrypt) / .OpenSsl.cs / .Apple.cs: the
// managed Rijndael core of .NET Framework (RijndaelManagedTransform.cs).
// AES is Rijndael with a 128-bit block, which AesImplementation enforces
// before it gets here (LegalBlockSizes).

namespace System.Security.Cryptography
{
    internal sealed partial class AesImplementation
    {
        // SharpOS cut: returns the transform itself, not a
        // UniversalCryptoTransform over a BasicSymmetricCipher — the managed
        // core does its own chaining and padding.
        private static ICryptoTransform CreateTransformCore(
            CipherMode cipherMode,
            PaddingMode paddingMode,
            byte[] key,
            byte[]? iv,
            int blockSize,
            int paddingSize,
            int feedbackSize,
            bool encrypting)
        {
            _ = paddingSize;
            return new RijndaelManagedTransform(
                key, cipherMode, iv!, blockSize * 8, feedbackSize * 8, paddingMode,
                encrypting ? RijndaelManagedTransformMode.Encrypt : RijndaelManagedTransformMode.Decrypt);
        }

        private static ILiteSymmetricCipher CreateLiteCipher(
            CipherMode cipherMode,
            ReadOnlySpan<byte> key,
            ReadOnlySpan<byte> iv,
            int blockSize,
            int paddingSize,
            int feedbackSize,
            bool encrypting)
        {
            return new ManagedLiteCipher(cipherMode, key.ToArray(), iv.IsEmpty ? null : iv.ToArray(),
                                         blockSize, paddingSize, feedbackSize, encrypting);
        }

        // The one-shot APIs' cipher (EncryptCbc and friends): no padding —
        // UniversalCryptoOneShot pads and depads around it.
        private sealed class ManagedLiteCipher : ILiteSymmetricCipher
        {
            private readonly CipherMode _mode;
            private readonly byte[] _key;
            private readonly int _feedbackSize;
            private readonly bool _encrypting;
            private RijndaelManagedTransform _transform;

            public int BlockSizeInBytes { get; }
            public int PaddingSizeInBytes { get; }

            public ManagedLiteCipher(CipherMode mode, byte[] key, byte[]? iv, int blockSize, int paddingSize, int feedbackSize, bool encrypting)
            {
                _mode = mode;
                _key = key;
                _feedbackSize = feedbackSize;
                _encrypting = encrypting;
                BlockSizeInBytes = blockSize;
                PaddingSizeInBytes = paddingSize;
                _transform = Create(iv);
            }

            private RijndaelManagedTransform Create(byte[]? iv) =>
                new RijndaelManagedTransform(
                    _key, _mode, iv!, BlockSizeInBytes * 8, _feedbackSize * 8, PaddingMode.None,
                    _encrypting ? RijndaelManagedTransformMode.Encrypt : RijndaelManagedTransformMode.Decrypt);

            public int Transform(ReadOnlySpan<byte> input, Span<byte> output)
            {
                if (input.IsEmpty) return 0;
                byte[] inputArray = input.ToArray();
                byte[] outputArray = new byte[inputArray.Length];
                int written = _transform.TransformBlock(inputArray, 0, inputArray.Length, outputArray, 0);
                outputArray.AsSpan(0, written).CopyTo(output);
                return written;
            }

            public int TransformFinal(ReadOnlySpan<byte> input, Span<byte> output)
            {
                byte[] last = _transform.TransformFinalBlock(input.ToArray(), 0, input.Length);
                last.CopyTo(output);
                return last.Length;
            }

            public void Reset(ReadOnlySpan<byte> iv)
            {
                _transform.Dispose();
                _transform = Create(iv.IsEmpty ? null : iv.ToArray());
            }

            public void Dispose() => _transform.Dispose();
        }
    }
}
