// SHA-256 for BCL's SHA256, HMACSHA256, IncrementalHash and PBKDF2: std's own
// streaming implementation (Security/Sha256.cs), the one the kernel and the
// CoreCLR bridge already use — not the browser build's second copy.

using System.Diagnostics;
using SharpOS.Std.Security;

namespace System.Security.Cryptography
{
    internal sealed unsafe class Sha256HashProvider : HashProvider
    {
        private Sha256State _state;

        public Sha256HashProvider()
        {
            Reset();
        }

        public override int HashSizeInBytes => (int)Sha256.DigestBytes;

        public override void AppendHashData(ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty) return;
            fixed (Sha256State* state = &_state)
            fixed (byte* bytes = data)
            {
                Sha256.Update(state, bytes, (uint)data.Length);
            }
        }

        public override int FinalizeHashAndReset(Span<byte> destination)
        {
            Debug.Assert(destination.Length >= HashSizeInBytes);
            fixed (Sha256State* state = &_state)
            fixed (byte* output = destination)
            {
                Sha256.Final(state, output);
            }
            Reset();
            return HashSizeInBytes;
        }

        public override int GetCurrentHash(Span<byte> destination)
        {
            Debug.Assert(destination.Length >= HashSizeInBytes);
            fixed (Sha256State* state = &_state)
            fixed (byte* output = destination)
            {
                Sha256.Snapshot(state, output);
            }
            return HashSizeInBytes;
        }

        public override void Reset()
        {
            fixed (Sha256State* state = &_state)
            {
                Sha256.Init(state);
            }
        }

        public override void Dispose(bool disposing)
        {
        }
    }
}
