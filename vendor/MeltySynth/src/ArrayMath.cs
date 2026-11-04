using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace MeltySynth
{
    internal static class ArrayMath
    {
        public static void MultiplyAdd(float a, float[] x, float[] destination)
        {
            /*
            for (var i = 0; i < destination.Length; i++)
            {
                destination[i] += a * x[i];
            }
            */

            // SharpOS cut: AsSpan() on destination. C# 14's first-class spans
            // pick Cast's ReadOnlySpan overload for an array, and the += below
            // then writes through a readonly span; MeltySynth predates the rule.
            var vx = MemoryMarshal.Cast<float, Vector<float>>(x);
            var vd = MemoryMarshal.Cast<float, Vector<float>>(destination.AsSpan());

            var count = 0;

            for (var i = 0; i < vd.Length; i++)
            {
                vd[i] += a * vx[i];
                count += Vector<float>.Count;
            }

            for (var i = count; i < destination.Length; i++)
            {
                destination[i] += a * x[i];
            }
        }

        public static void MultiplyAdd(float a, float step, float[] x, float[] destination)
        {
            for (var i = 0; i < destination.Length; i++)
            {
                destination[i] += a * x[i];
                a += step;
            }
        }
    }
}
