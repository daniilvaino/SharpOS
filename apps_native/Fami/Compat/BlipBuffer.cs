using System;

namespace BizHawk.Emulation.Common
{
    // Managed stand-in for Fami\Fami.Core\Audio\BlipBuffer.cs, which is a thin
    // P/Invoke wrapper over the native blip_buf library. This project compiles
    // that file out and uses this one instead, in both the SharpOS build and
    // the grown-up .NET build.
    //
    // Two reasons, and the second is the one that matters:
    //
    //  - SharpOS has no native code by construction, and no DLL to load one
    //    from. The upstream class throws before the first instruction is
    //    emulated: the CPU allocates a BlipBuffer in its constructor, so a
    //    missing blip_buf.dll takes the whole emulator down whether or not
    //    anyone wants sound.
    //  - Nothing consumes the samples. There is no audio output on SharpOS
    //    yet, so what this needs to be is cheap and harmless, not accurate.
    //
    // So this resamples by point-sampling rather than band-limiting: it bins
    // the amplitude deltas by output sample and integrates them. Real blip_buf
    // convolves each delta with a windowed sinc step, which is what removes the
    // aliasing that makes square-wave synthesis sound harsh. The difference is
    // audible and this would be the wrong class to keep once sound is real —
    // at that point port the synthesis properly (it is public-domain C by Shay
    // Green, and the algorithm is a few dozen lines) rather than extending this.
    //
    // Faithful in the ways the emulator can observe: sample counts, clock
    // arithmetic and buffer capacity all behave as upstream's callers expect,
    // because the APU asks how many samples are ready and reads exactly that
    // many.
    public sealed class BlipBuffer : IDisposable
    {
        public const int MaxRatio = 1 << 20;
        public const int MaxFrame = 4000;

        private readonly int _sampleCount;

        // Deltas binned per output sample for the frame being built. Integrated
        // on EndFrame; index is the output sample, not the clock.
        private readonly int[] _bins;
        private readonly short[] _ready;

        private double _clocksPerSample = 1.0;

        // Amplitude carried across frames. Deltas are relative, so dropping
        // this between frames would put a step at every frame boundary.
        private int _amplitude;
        private int _readyCount;

        public BlipBuffer(int sampleCount)
        {
            _sampleCount = sampleCount;
            _bins = new int[sampleCount + 1];
            _ready = new short[sampleCount];
        }

        public void Dispose()
        {
        }

        public void SetRates(double clockRate, double sampleRate)
        {
            if (sampleRate > 0)
            {
                _clocksPerSample = clockRate / sampleRate;
            }
        }

        public void Clear()
        {
            Array.Clear(_bins, 0, _bins.Length);
            _amplitude = 0;
            _readyCount = 0;
        }

        public void AddDelta(uint clockTime, int delta)
        {
            int bin = (int)(clockTime / _clocksPerSample);
            if (bin < 0) bin = 0;
            if (bin >= _bins.Length) bin = _bins.Length - 1;
            _bins[bin] += delta;
        }

        // Upstream's fast path skips the sinc interpolation; here there is no
        // interpolation to skip, so the two are the same call.
        public void AddDeltaFast(uint clockTime, int delta) => AddDelta(clockTime, delta);

        public int ClocksNeeded(int sampleCount) => (int)(sampleCount * _clocksPerSample);

        public void EndFrame(uint clockDuration)
        {
            int samples = (int)(clockDuration / _clocksPerSample);
            if (samples > _sampleCount) samples = _sampleCount;

            for (int i = 0; i < samples; i++)
            {
                _amplitude += _bins[i];

                int value = _amplitude >> 8;
                if (value > short.MaxValue) value = short.MaxValue;
                else if (value < short.MinValue) value = short.MinValue;
                _ready[i] = (short)value;
            }

            // Deltas past the end of the frame belong to the next one and would
            // otherwise be integrated twice.
            for (int i = samples; i < _bins.Length; i++)
            {
                _amplitude += _bins[i];
            }

            Array.Clear(_bins, 0, _bins.Length);
            _readyCount = samples;
        }

        public int SamplesAvailable() => _readyCount;

        public int ReadSamples(short[] output, int count, bool stereo)
        {
            if (output.Length < count * (stereo ? 2 : 1))
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            if (count > _readyCount) count = _readyCount;

            for (int i = 0; i < count; i++)
            {
                output[stereo ? i * 2 : i] = _ready[i];
            }

            _readyCount -= count;
            return count;
        }

        public int ReadSamplesLeft(short[] output, int count) => ReadSamples(output, count, true);

        public int ReadSamplesRight(short[] output, int count)
        {
            if (output.Length < count * 2)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            if (count > _readyCount) count = _readyCount;

            for (int i = 0; i < count; i++)
            {
                output[(i * 2) + 1] = _ready[i];
            }

            _readyCount -= count;
            return count;
        }
    }
}
