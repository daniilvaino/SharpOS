// System.HashCode — накопитель хеша.
//
// Не порт: настоящий использует xxHash и случайное зерно на процесс. Здесь
// FNV-1a, детерминированный. Разница видна только тому, кто рассчитывал на
// случайность зерна как на защиту от подобранных коллизий; в ядре и в
// приложениях такого пока нет ни одного, а назвать это xxHash было бы хуже,
// чем написать, что это не он.

namespace System
{
    public struct HashCode
    {
        private const uint Offset = 2166136261;
        private const uint Prime = 16777619;

        private uint _hash;
        private bool _started;

        public void Add<T>(T value)
        {
            Mix(value == null ? 0 : value.GetHashCode());
        }

        public void Add<T>(T value, System.Collections.Generic.IEqualityComparer<T> comparer)
        {
            Mix(value == null ? 0 : (comparer == null ? value.GetHashCode() : comparer.GetHashCode(value)));
        }

        public void AddBytes(ReadOnlySpan<byte> value)
        {
            for (int i = 0; i < value.Length; i++) Mix(value[i]);
        }

        public int ToHashCode() => (int)(_started ? _hash : Offset);

        private void Mix(int value)
        {
            if (!_started) { _hash = Offset; _started = true; }

            unchecked
            {
                _hash = (_hash ^ (uint)(value & 0xFF)) * Prime;
                _hash = (_hash ^ (uint)((value >> 8) & 0xFF)) * Prime;
                _hash = (_hash ^ (uint)((value >> 16) & 0xFF)) * Prime;
                _hash = (_hash ^ (uint)((value >> 24) & 0xFF)) * Prime;
            }
        }

        public static int Combine<T1>(T1 v1)
        {
            var h = new HashCode(); h.Add(v1); return h.ToHashCode();
        }

        public static int Combine<T1, T2>(T1 v1, T2 v2)
        {
            var h = new HashCode(); h.Add(v1); h.Add(v2); return h.ToHashCode();
        }

        public static int Combine<T1, T2, T3>(T1 v1, T2 v2, T3 v3)
        {
            var h = new HashCode(); h.Add(v1); h.Add(v2); h.Add(v3); return h.ToHashCode();
        }

        public static int Combine<T1, T2, T3, T4>(T1 v1, T2 v2, T3 v3, T4 v4)
        {
            var h = new HashCode(); h.Add(v1); h.Add(v2); h.Add(v3); h.Add(v4); return h.ToHashCode();
        }
    }
}
