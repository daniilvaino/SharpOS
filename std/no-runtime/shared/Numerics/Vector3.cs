// System.Numerics.Vector3 — minimal cut of dotnet/runtime v8.0.27
//   src/libraries/System.Private.CoreLib/src/System/Numerics/Vector3.cs (MIT)
//
// Fields + ctors + Zero + componentwise operators only. Cuts: all SIMD
// intrinsic paths, Length/Normalize/Dot/Cross (need float Math.Sqrt
// plumbing), Vector2/4 interplay, formatting. Today's only consumer is
// `using System.Numerics` resolution in ported app code (ManagedDoom
// Player.cs); the audio listener math that actually computes with it
// arrives with the sound bring-up.

namespace System.Numerics
{
    public struct Vector3 : IEquatable<Vector3>
    {
        public float X;
        public float Y;
        public float Z;

        public Vector3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public Vector3(float value) : this(value, value, value) { }

        public static Vector3 Zero => default;

        public static Vector3 operator +(Vector3 left, Vector3 right)
            => new Vector3(left.X + right.X, left.Y + right.Y, left.Z + right.Z);

        public static Vector3 operator -(Vector3 left, Vector3 right)
            => new Vector3(left.X - right.X, left.Y - right.Y, left.Z - right.Z);

        public static Vector3 operator *(Vector3 left, float right)
            => new Vector3(left.X * right, left.Y * right, left.Z * right);

        public static bool operator ==(Vector3 left, Vector3 right)
            => left.X == right.X && left.Y == right.Y && left.Z == right.Z;

        public static bool operator !=(Vector3 left, Vector3 right)
            => !(left == right);

        public bool Equals(Vector3 other) => this == other;

        public override bool Equals(object obj) => obj is Vector3 v && this == v;

        public override int GetHashCode()
        {
            // No HashCode.Combine in this std; fold the raw bits.
            unsafe
            {
                float x = X, y = Y, z = Z;
                int hx = *(int*)&x, hy = *(int*)&y, hz = *(int*)&z;
                return ((hx << 5) + hx) ^ ((hy << 3) + hy) ^ hz;
            }
        }
    }
}
