// Ported from dotnet/runtime v8.0.27:
//   src/libraries/System.Private.CoreLib/src/System/Tuple.cs (MIT)
//
// Cuts vs original:
//   - Arities 3..8 + TRest nesting — ManagedDoom only constructs pairs;
//     add higher arities when a caller appears.
//   - ITupleInternal / ITuple / IStructuralEquatable / IStructuralComparable /
//     IComparable — structural-comparer plumbing; Equals/GetHashCode below
//     go through EqualityComparer<T>.Default directly, same observable
//     result for the default-comparer case.
// Field names (m_Item1/m_Item2) kept.

using System.Collections.Generic;

namespace System
{
    public static class Tuple
    {
        public static Tuple<T1> Create<T1>(T1 item1)
        {
            return new Tuple<T1>(item1);
        }

        public static Tuple<T1, T2> Create<T1, T2>(T1 item1, T2 item2)
        {
            return new Tuple<T1, T2>(item1, item2);
        }

        public static Tuple<T1, T2, T3> Create<T1, T2, T3>(T1 item1, T2 item2, T3 item3)
        {
            return new Tuple<T1, T2, T3>(item1, item2, item3);
        }
    }

    public class Tuple<T1>
    {
        private readonly T1 m_Item1;

        public T1 Item1 => m_Item1;

        public Tuple(T1 item1)
        {
            m_Item1 = item1;
        }

        public override bool Equals(object obj)
        {
            return obj is Tuple<T1> other
                && EqualityComparer<T1>.Default.Equals(m_Item1, other.m_Item1);
        }

        public override int GetHashCode()
        {
            return m_Item1 == null ? 0 : m_Item1.GetHashCode();
        }

        public override string ToString()
        {
            return "(" + (m_Item1?.ToString() ?? "") + ")";
        }
    }

    public class Tuple<T1, T2>
    {
        private readonly T1 m_Item1;
        private readonly T2 m_Item2;

        public T1 Item1 => m_Item1;
        public T2 Item2 => m_Item2;

        public Tuple(T1 item1, T2 item2)
        {
            m_Item1 = item1;
            m_Item2 = item2;
        }

        public override bool Equals(object obj)
        {
            return obj is Tuple<T1, T2> other
                && EqualityComparer<T1>.Default.Equals(m_Item1, other.m_Item1)
                && EqualityComparer<T2>.Default.Equals(m_Item2, other.m_Item2);
        }

        public override int GetHashCode()
        {
            int h1 = m_Item1 == null ? 0 : m_Item1.GetHashCode();
            int h2 = m_Item2 == null ? 0 : m_Item2.GetHashCode();
            return ((h1 << 5) + h1) ^ h2;
        }

        public override string ToString()
        {
            return "(" + (m_Item1?.ToString() ?? "") + ", " + (m_Item2?.ToString() ?? "") + ")";
        }
    }

    public class Tuple<T1, T2, T3>
    {
        private readonly T1 m_Item1;
        private readonly T2 m_Item2;
        private readonly T3 m_Item3;

        public T1 Item1 => m_Item1;
        public T2 Item2 => m_Item2;
        public T3 Item3 => m_Item3;

        public Tuple(T1 item1, T2 item2, T3 item3)
        {
            m_Item1 = item1;
            m_Item2 = item2;
            m_Item3 = item3;
        }

        public override bool Equals(object obj)
        {
            return obj is Tuple<T1, T2, T3> other
                && EqualityComparer<T1>.Default.Equals(m_Item1, other.m_Item1)
                && EqualityComparer<T2>.Default.Equals(m_Item2, other.m_Item2)
                && EqualityComparer<T3>.Default.Equals(m_Item3, other.m_Item3);
        }

        public override int GetHashCode()
        {
            int h1 = m_Item1 == null ? 0 : m_Item1.GetHashCode();
            int h2 = m_Item2 == null ? 0 : m_Item2.GetHashCode();
            int h3 = m_Item3 == null ? 0 : m_Item3.GetHashCode();
            int h12 = ((h1 << 5) + h1) ^ h2;
            return ((h12 << 5) + h12) ^ h3;
        }

        public override string ToString()
        {
            return "(" + (m_Item1?.ToString() ?? "") + ", " + (m_Item2?.ToString() ?? "") + ", " + (m_Item3?.ToString() ?? "") + ")";
        }
    }
}
