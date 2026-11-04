using System;

namespace SharpOS.AppSdk
{
    /// <summary>
    /// The sound card (step246): PCM out, 16-bit signed, stereo, interleaved
    /// (left, right, left, …) at <see cref="SampleRate"/>. Nothing waits: a
    /// write takes what fits in the kernel's queue (about 680 ms) and says how
    /// much; a program paced by frames writes each frame's samples and is
    /// never ahead by more than that.
    /// </summary>
    public static unsafe class Audio
    {
        /// <summary>Whether there is a card to play to.</summary>
        public static bool Available => Control(0) > 0;

        /// <summary>Samples per second per channel; 0 without a card.</summary>
        public static int SampleRate { get { int r = Control(0); return r > 0 ? r : 0; } }

        /// <summary>Stereo frames waiting to be played.</summary>
        public static int QueuedFrames { get { int b = Control(1); return b > 0 ? b / 4 : 0; } }

        /// <summary>Stereo frames that fit in the queue now.</summary>
        public static int FreeFrames { get { int b = Control(2); return b > 0 ? b / 4 : 0; } }

        /// <summary>
        /// Queues interleaved stereo samples; returns how many samples were
        /// taken (whole frames: an even number), 0 without a card.
        /// </summary>
        public static int Write(ReadOnlySpan<short> samples)
        {
            ulong address = AppRuntime.Services == null ? 0 : AppRuntime.Services->AudioWriteAddress;
            if (address == 0 || samples.Length < 2) return 0;
            fixed (short* p = samples)
            {
                int taken = ((delegate* unmanaged<byte*, int, int>)address)((byte*)p, (samples.Length & ~1) * 2);
                return taken > 0 ? taken / 2 : 0;
            }
        }

        /// <summary>Drops what is queued and not yet played.</summary>
        public static void Clear() => Control(3);

        /// <summary>
        /// The PC speaker (step248): a square wave of <paramref name="hz"/> for
        /// <paramref name="ms"/>, without waiting (the kernel ends it); 0 Hz
        /// stops one. False without the speaker.
        /// </summary>
        public static bool Speaker(int hz, int ms)
        {
            ulong address = AppRuntime.Services == null ? 0 : AppRuntime.Services->SpeakerAddress;
            if (address == 0) return false;
            return ((delegate* unmanaged<int, int, int>)address)(hz, ms) == 0;
        }

        private static int Control(int op)
        {
            ulong address = AppRuntime.Services == null ? 0 : AppRuntime.Services->AudioControlAddress;
            if (address == 0) return -1;
            return ((delegate* unmanaged<int, int>)address)(op);
        }
    }

    /// <summary>
    /// One more stream into the kernel's mixer (step250), beside the default
    /// one <see cref="Audio.Write"/> uses: a program with two sounds — DOOM's
    /// effects and music — writes each to its own, and the kernel sums them.
    /// Same format as <see cref="Audio"/>; closed by Dispose or when the
    /// process ends.
    /// </summary>
    public sealed unsafe class AudioStream : IDisposable
    {
        private int _id;

        private AudioStream(int id) => _id = id;

        /// <summary>A new stream, or null without a card or with every stream taken.</summary>
        public static AudioStream Open()
        {
            int id = Call(0, 0, null, 0);
            return id > 0 ? new AudioStream(id) : null;
        }

        /// <summary>Queues interleaved stereo samples; returns how many were taken (whole frames).</summary>
        public int Write(ReadOnlySpan<short> samples)
        {
            if (_id <= 0 || samples.Length < 2) return 0;
            fixed (short* p = samples)
            {
                int taken = Call(1, _id, (byte*)p, (samples.Length & ~1) * 2);
                return taken > 0 ? taken / 2 : 0;
            }
        }

        /// <summary>Stereo frames waiting to be played.</summary>
        public int QueuedFrames { get { int b = Call(2, _id, null, 0); return b > 0 ? b / 4 : 0; } }

        /// <summary>Stereo frames that fit in the queue now.</summary>
        public int FreeFrames { get { int b = Call(3, _id, null, 0); return b > 0 ? b / 4 : 0; } }

        /// <summary>Drops what is queued and not yet played.</summary>
        public void Clear() => Call(4, _id, null, 0);

        public void Dispose()
        {
            if (_id > 0) Call(5, _id, null, 0);
            _id = 0;
        }

        private static int Call(int op, int stream, byte* pcm, int length)
        {
            ulong address = AppRuntime.Services == null ? 0 : AppRuntime.Services->AudioStreamAddress;
            if (address == 0) return -1;
            return ((delegate* unmanaged<int, int, byte*, int, int>)address)(op, stream, pcm, length);
        }
    }
}
