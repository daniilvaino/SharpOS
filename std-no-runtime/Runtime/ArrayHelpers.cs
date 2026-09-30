// Internal.Runtime.CompilerHelpers.ArrayHelpers — the helper ILC calls for
// `newobj` on an array type, which is how multidimensional and jagged array
// creation is expressed in IL.
//
// Ported from dotnet/runtime (MIT):
//   src/coreclr/nativeaot/System.Private.CoreLib/src/Internal/Runtime/
//     CompilerHelpers/ArrayHelpers.cs
//   src/coreclr/nativeaot/System.Private.CoreLib/src/System/
//     Array.NativeAot.cs  (NewMultiDimArray)
//
// ILC looks this type up BY NAME in the system module and fails code
// generation for the whole method if it is missing — the error names the
// method being compiled, not the missing helper, which is why a
// `private byte[,] tbl = new byte[2, 1024];` field initializer reads as
// "Code generation failed for method Ppu..ctor".
//
// Cuts from the original:
//   - EETypePtr replaced with GcMethodTable*, which is the same MethodTable
//     seen through our own parser.
//   - Type.GetTypeFromHandle / MakeArrayType path for rank-1 MdArrays removed:
//     that needs reflection, which does not exist here. A rank-1 MdArray
//     (`int[*]`, only expressible in IL) throws instead of silently allocating
//     something of the wrong shape. C# cannot produce one.
//   - Debug.Assert dropped.
//
// Layout note, since it is the part that would corrupt memory if wrong: a
// multidimensional array is [MethodTable*][total length][pad][lengths[rank]]
// [lowerBounds[rank]][elements...]. The bounds block sits where an SzArray's
// elements would start, offset 16 on x64, and BaseSize already accounts for
// it — so the ordinary array allocator sizes these correctly with no special
// case. Only the lengths are written here; zero lower bounds are what the
// zeroed allocation already gives.

using System;
using SharpOS.Std.NoRuntime;

namespace Internal.Runtime.CompilerHelpers
{
    public static unsafe class ArrayHelpers
    {
        // SzArray base size on x64: MethodTable* + length + padding, rounded as
        // the runtime rounds it. A multidim array's MethodTable stores its
        // shape as a base size grown by two Int32s per dimension, so the excess
        // over this constant is what gives the rank back.
        private const int SzArrayBaseSize = 24;

        /// <summary>
        /// Helper for array allocations via the `newobj` IL instruction.
        /// Dimensions are passed in as a block of integers; the block may be
        /// modified by this method (as upstream's is).
        /// </summary>
        public static Array NewObjArray(IntPtr pEEType, int nDimensions, int* pDimensions)
        {
            GcMethodTable* mt = (GcMethodTable*)pEEType;

            if (mt == null || nDimensions <= 0)
                throw new ArgumentException("Invalid array shape.");

            if (mt->IsSzArray)
            {
                Array result = NewSzArray(mt, pDimensions[0]);

                // Jagged arrays: one constructor per depth, each level
                // allocating the level below it.
                if (nDimensions > 1)
                {
                    GcMethodTable* elementType = mt->RelatedType;
                    Array[] arrayOfArrays = (Array[])(object)result;
                    for (int i = 0; i < arrayOfArrays.Length; i++)
                    {
                        arrayOfArrays[i] = NewObjArray(
                            (IntPtr)elementType, nDimensions - 1, pDimensions + 1);
                    }
                }

                return result;
            }

            int rank = ArrayRank(mt);

            // Two constructors exist, with and without lower bounds. The
            // with-bounds form passes 2*rank integers interleaved as
            // (lowerBound, length) pairs; collapse it to plain lengths.
            if (rank < nDimensions)
            {
                for (int i = 0; i < rank; i++)
                {
                    if (pDimensions[2 * i] != 0)
                        throw new NotSupportedException("Arrays with non-zero lower bounds are not supported.");

                    pDimensions[i] = pDimensions[(2 * i) + 1];
                }
            }

            if (rank == 1)
            {
                // The runtime allocates rank-1 zero-lower-bound MdArrays as
                // SzArrays and relies on the cast between them. Doing that here
                // needs reflection to name the SzArray type; C# cannot produce
                // this shape, so refuse rather than guess.
                throw new NotSupportedException("Rank-1 multidimensional arrays are not supported.");
            }

            return NewMultiDimArray(mt, pDimensions, rank);
        }

        private static int ArrayRank(GcMethodTable* mt)
        {
            // The MethodTable's base size doubles as the shape of a
            // parameterized type: anything above the SzArray base size is the
            // bounds block, two Int32s per dimension.
            int boundsSize = (int)mt->BaseSize - SzArrayBaseSize;
            return boundsSize > 0 ? boundsSize / (2 * sizeof(int)) : 1;
        }

        private static Array NewMultiDimArray(GcMethodTable* mt, int* pLengths, int rank)
        {
            ulong totalLength = 1;

            for (int i = 0; i < rank; i++)
            {
                int length = pLengths[i];
                if (length < 0)
                    throw new OverflowException();

                totalLength *= (ulong)length;
                if (totalLength > int.MaxValue)
                    throw new OutOfMemoryException();
            }

            Array result = NewSzArray(mt, (int)totalLength);

            // Lengths go where an SzArray keeps its first element. Lower bounds
            // follow at [rank..2*rank) and stay zero.
            int* bounds = (int*)(*(byte**)&result + 16);
            for (int i = 0; i < rank; i++)
            {
                bounds[i] = pLengths[i];
            }

            return result;
        }

        // Allocation itself is the ordinary array path: the element count is
        // the TOTAL number of elements for a multidim array, and BaseSize
        // already covers the bounds block.
        private static Array NewSzArray(GcMethodTable* mt, int numElements)
        {
            if (numElements < 0)
                throw new OverflowException();

            // size = BaseSize + numElements * ComponentSize, pointer-aligned —
            // the same arithmetic RhpNewArray does, reached from managed code
            // here rather than through the runtime export.
            ulong size = (ulong)mt->BaseSize + ((ulong)(uint)numElements * mt->ComponentSize);
            size = (size + 7UL) & ~7UL;
            if (size > 0xFFFFFFFFUL)
                throw new OutOfMemoryException();

            void* allocated = GcHeap.AllocateRaw((uint)size);
            if (allocated == null)
                throw new OutOfMemoryException();

            *(GcMethodTable**)allocated = mt;
            *(int*)((byte*)allocated + 8) = numElements;

            Array result = null;
            *(void**)&result = allocated;
            return result;
        }
    }
}
