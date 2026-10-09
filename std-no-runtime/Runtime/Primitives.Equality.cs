// Equality and ordering of Double and Single — dotnet/runtime main,
// System.Private.CoreLib/src/System/Double.cs and Single.cs (MIT): Equals
// (NaN equals NaN), CompareTo (NaN orders first), GetHashCode (both zeros and
// all NaNs hash alike). Shared by both tiers' MinimalRuntime, where the structs
// are partial with a `_value` field.
//
// Missing until step198: neither struct had IEquatable<T> or Equals(object), so
// EqualityComparer<double>.Default compared the boxes by reference and
// 2.5 == 2.5 was false through it (JsonNode.DeepEquals on a cloned 2.5).

namespace System
{
    public partial struct Double : IEquatable<double>, IComparable<double>, IComparable
    {
        private static unsafe ulong Bits(double d) => *(ulong*)&d;

        public bool Equals(double obj)
        {
            if (obj == _value)
                return true;
            return IsNaN(obj) && IsNaN(_value);
        }

        public override bool Equals(object? obj) => obj is double d && Equals(d);

        public override int GetHashCode()
        {
            ulong bits = Bits(_value);
            // Both zeros and every NaN share one hash code.
            if (((bits - 1) & 0x7FFFFFFFFFFFFFFFul) >= 0x7FF0000000000000ul)
                bits &= 0x7FF0000000000000ul;
            return unchecked((int)bits) ^ ((int)(bits >> 32));
        }

        public int CompareTo(double value)
        {
            if (_value < value) return -1;
            if (_value > value) return 1;
            if (_value == value) return 0;

            // At least one of the values is NaN.
            if (IsNaN(_value))
                return IsNaN(value) ? 0 : -1;
            return 1;
        }

        public int CompareTo(object? value)
        {
            if (value == null) return 1;
            if (value is double d) return CompareTo(d);
            throw new ArgumentException("Object must be of type Double.");
        }
    }

    public partial struct Single : IEquatable<float>, IComparable<float>, IComparable
    {
        private static unsafe uint Bits(float f) => *(uint*)&f;

        private static bool IsNaNBits(float f) => (Bits(f) & 0x7FFFFFFFu) > 0x7F800000u;

        public bool Equals(float obj)
        {
            if (obj == _value)
                return true;
            return IsNaNBits(obj) && IsNaNBits(_value);
        }

        public override bool Equals(object? obj) => obj is float f && Equals(f);

        public override int GetHashCode()
        {
            uint bits = Bits(_value);
            // Both zeros and every NaN share one hash code.
            if (((bits - 1) & 0x7FFFFFFFu) >= 0x7F800000u)
                bits &= 0x7F800000u;
            return (int)bits;
        }

        public int CompareTo(float value)
        {
            if (_value < value) return -1;
            if (_value > value) return 1;
            if (_value == value) return 0;

            // At least one of the values is NaN.
            if (IsNaNBits(_value))
                return IsNaNBits(value) ? 0 : -1;
            return 1;
        }

        public int CompareTo(object? value)
        {
            if (value == null) return 1;
            if (value is float f) return CompareTo(f);
            throw new ArgumentException("Object must be of type Single.");
        }
    }
}
