namespace OS.Kernel.Crypto
{
    // SHA-256 (FIPS 180-4) in managed C#. Lives kernel-side per the
    // SharpOS invariant: PAL is a thin forwarder, implementations stay
    // in C#. Exposed to forked CoreCLR through SharpOSHost_Sha256_*
    // exports in PAL/SharpOSHost/Sha256Bridge.cs.
    //
    // State layout: 8 H-words + 64-byte buffer + bufLen + total byte
    // count = 8*4 + 64 + 4 + 8 = 108 bytes. Kept inline in a struct so
    // callers can choose stack or heap allocation; the bridge layer
    // hands out heap-allocated boxes as Win32-style handles.
    internal unsafe struct Sha256State
    {
        public fixed uint H[8];
        public fixed byte Buffer[64];
        public uint BufferLen;
        public ulong TotalBytes;
    }

    internal static unsafe class Sha256
    {
        public const uint DigestBytes = 32;

        // K table inlined into Compress via stackalloc-with-initializer to
        // avoid the class-constructor trap (static readonly uint[] would
        // trigger ClassConstructorRunner.CheckStaticClassConstruction*
        // which is unimplemented in the NoStdLib kernel environment; see
        // CLAUDE.md "ClassConstructorRunner trap"). One re-fill per
        // compression block; negligible cost.

        private static uint Rotr(uint x, int n) => (x >> n) | (x << (32 - n));

        public static void Init(Sha256State* s)
        {
            s->H[0] = 0x6a09e667; s->H[1] = 0xbb67ae85; s->H[2] = 0x3c6ef372; s->H[3] = 0xa54ff53a;
            s->H[4] = 0x510e527f; s->H[5] = 0x9b05688c; s->H[6] = 0x1f83d9ab; s->H[7] = 0x5be0cd19;
            s->BufferLen = 0;
            s->TotalBytes = 0;
        }

        private static void Compress(Sha256State* s, byte* block)
        {
            uint* k = stackalloc uint[64] {
                0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,
                0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,
                0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,
                0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,
                0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,
                0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,
                0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,
                0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2
            };

            uint* w = stackalloc uint[64];
            for (int i = 0; i < 16; i++)
            {
                w[i] = ((uint)block[i * 4] << 24) | ((uint)block[i * 4 + 1] << 16)
                     | ((uint)block[i * 4 + 2] << 8) | (uint)block[i * 4 + 3];
            }
            for (int i = 16; i < 64; i++)
            {
                uint s0 = Rotr(w[i - 15], 7) ^ Rotr(w[i - 15], 18) ^ (w[i - 15] >> 3);
                uint s1 = Rotr(w[i - 2], 17) ^ Rotr(w[i - 2], 19) ^ (w[i - 2] >> 10);
                w[i] = w[i - 16] + s0 + w[i - 7] + s1;
            }

            uint a = s->H[0], b = s->H[1], c = s->H[2], d = s->H[3];
            uint e = s->H[4], f = s->H[5], g = s->H[6], h = s->H[7];
            for (int i = 0; i < 64; i++)
            {
                uint S1 = Rotr(e, 6) ^ Rotr(e, 11) ^ Rotr(e, 25);
                uint ch = (e & f) ^ ((~e) & g);
                uint t1 = h + S1 + ch + k[i] + w[i];
                uint S0 = Rotr(a, 2) ^ Rotr(a, 13) ^ Rotr(a, 22);
                uint mj = (a & b) ^ (a & c) ^ (b & c);
                uint t2 = S0 + mj;
                h = g; g = f; f = e; e = d + t1; d = c; c = b; b = a; a = t1 + t2;
            }
            s->H[0] += a; s->H[1] += b; s->H[2] += c; s->H[3] += d;
            s->H[4] += e; s->H[5] += f; s->H[6] += g; s->H[7] += h;
        }

        public static void Update(Sha256State* s, byte* data, uint len)
        {
            s->TotalBytes += len;
            while (len > 0)
            {
                uint copy = 64 - s->BufferLen;
                if (copy > len) copy = len;
                for (uint i = 0; i < copy; i++) s->Buffer[s->BufferLen + i] = data[i];
                s->BufferLen += copy;
                data += copy;
                len -= copy;
                if (s->BufferLen == 64)
                {
                    Compress(s, s->Buffer);
                    s->BufferLen = 0;
                }
            }
        }

        public static void Final(Sha256State* s, byte* out32)
        {
            ulong bitLen = s->TotalBytes * 8;
            s->Buffer[s->BufferLen++] = 0x80;
            if (s->BufferLen > 56)
            {
                while (s->BufferLen < 64) s->Buffer[s->BufferLen++] = 0;
                Compress(s, s->Buffer);
                s->BufferLen = 0;
            }
            while (s->BufferLen < 56) s->Buffer[s->BufferLen++] = 0;
            for (int i = 7; i >= 0; i--) s->Buffer[s->BufferLen++] = (byte)(bitLen >> (i * 8));
            Compress(s, s->Buffer);
            for (int i = 0; i < 8; i++)
            {
                out32[i * 4    ] = (byte)(s->H[i] >> 24);
                out32[i * 4 + 1] = (byte)(s->H[i] >> 16);
                out32[i * 4 + 2] = (byte)(s->H[i] >>  8);
                out32[i * 4 + 3] = (byte)(s->H[i]      );
            }
        }

        // Stateless one-shot. Stack-allocates a transient state; no heap
        // traffic for the BCL's `SHA256.HashData(data)` path.
        public static void OneShot(byte* data, uint len, byte* out32)
        {
            Sha256State s;
            Init(&s);
            Update(&s, data, len);
            Final(&s, out32);
        }

        // Snapshot the current state and finalize the copy. Used by
        // BCL's HashAlgorithm.GetCurrentHash without mutating the
        // running context.
        public static void Snapshot(Sha256State* s, byte* out32)
        {
            Sha256State copy = *s;
            Final(&copy, out32);
        }
    }
}
