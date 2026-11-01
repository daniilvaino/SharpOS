// Guid's equality, the string constructor and ordering — BCL's members
// (System.Private.CoreLib, Guid.cs) over the eleven fields (step224).

namespace System
{
    public partial struct Guid : IEquatable<Guid>, IComparable<Guid>, IComparable
    {
        public Guid(string g) => this = Parse(g);

        public bool Equals(Guid g) =>
            _a == g._a && _b == g._b && _c == g._c && _d == g._d && _e == g._e && _f == g._f
            && _g == g._g && _h == g._h && _i == g._i && _j == g._j && _k == g._k;

        public override bool Equals(object? o) => o is Guid g && Equals(g);

        public override int GetHashCode() =>
            _a ^ (((int)_b << 16) | (ushort)_c) ^ (((int)_f << 24) | (_k << 16) | (_d << 8) | _e);

        public static bool operator ==(Guid a, Guid b) => a.Equals(b);

        public static bool operator !=(Guid a, Guid b) => !a.Equals(b);

        // BCL's order: field by field, the unsigned way.
        public int CompareTo(Guid value)
        {
            if ((uint)_a != (uint)value._a) return (uint)_a < (uint)value._a ? -1 : 1;
            if ((ushort)_b != (ushort)value._b) return (ushort)_b < (ushort)value._b ? -1 : 1;
            if ((ushort)_c != (ushort)value._c) return (ushort)_c < (ushort)value._c ? -1 : 1;
            if (_d != value._d) return _d < value._d ? -1 : 1;
            if (_e != value._e) return _e < value._e ? -1 : 1;
            if (_f != value._f) return _f < value._f ? -1 : 1;
            if (_g != value._g) return _g < value._g ? -1 : 1;
            if (_h != value._h) return _h < value._h ? -1 : 1;
            if (_i != value._i) return _i < value._i ? -1 : 1;
            if (_j != value._j) return _j < value._j ? -1 : 1;
            if (_k != value._k) return _k < value._k ? -1 : 1;
            return 0;
        }

        public int CompareTo(object? value)
        {
            if (value == null) return 1;
            if (value is not Guid g) throw new ArgumentException("Object must be of type GUID.", nameof(value));
            return CompareTo(g);
        }
    }
}
