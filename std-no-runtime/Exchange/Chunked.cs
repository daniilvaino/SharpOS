using System;

namespace SharpOS.Std.Exchange
{
    /// <summary>
    /// A growable array in pieces of 8192 elements (step195): the buffers of
    /// the region writer and reader grow with the largest graph they see, and
    /// an app's heap is a pool cut into 256 KiB segments — past a few hundred
    /// thousand bytes nothing fits in one piece once the pool is carved.
    /// Indexing is a shift and a mask.
    /// </summary>
    public sealed class Chunked<T>
    {
        private const int Shift = 13;
        private const int Piece = 1 << Shift;
        private const int Mask = Piece - 1;

        private T[][] _pieces = new T[1][];
        private int _capacity;

        public Chunked(int capacity) => Ensure(capacity);

        public int Capacity => _capacity;

        public ref T this[int index]
        {
            [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
            get => ref _pieces[index >> Shift][index & Mask];
        }

        /// <summary>Room for <paramref name="count"/> elements; the ones already there stay.</summary>
        /// <remarks>
        /// The first piece starts small and doubles up to its full size: a
        /// writer of small messages keeps a few hundred bytes, not pieces.
        /// </remarks>
        public void Ensure(int count)
        {
            if (count <= _capacity) return;
            if (count <= Piece)
            {
                int size = _capacity == 0 ? 64 : _capacity;
                while (size < count) size *= 2;
                var first = new T[size];
                if (_pieces[0] != null) Array.Copy(_pieces[0], first, _capacity);
                _pieces[0] = first;
                _capacity = size;
                return;
            }
            if (_capacity < Piece)
            {
                var full = new T[Piece];
                if (_pieces[0] != null) Array.Copy(_pieces[0], full, _capacity);
                _pieces[0] = full;
                _capacity = Piece;
            }
            int need = (count + Mask) >> Shift;
            if (need > _pieces.Length)
            {
                var pieces = new T[Math.Max(need, _pieces.Length * 2)][];
                Array.Copy(_pieces, pieces, _pieces.Length);
                _pieces = pieces;
            }
            for (int i = 0; i < need; i++)
                if (_pieces[i] == null) _pieces[i] = new T[Piece];
            _capacity = need * Piece;
        }

        /// <summary>Zeroes the first <paramref name="count"/> elements.</summary>
        public void Clear(int count)
        {
            for (int i = 0; count > 0; i++, count -= Piece)
                Array.Clear(_pieces[i], 0, count < _pieces[i].Length ? count : _pieces[i].Length);
        }
    }
}
