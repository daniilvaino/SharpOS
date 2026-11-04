using OS.Kernel.Threading;

namespace OS.Kernel.Audio
{
    /// <summary>
    /// The sound programs write (step246): one queue of PCM — 16-bit signed,
    /// stereo, interleaved, at the device's rate — between the AudioWrite
    /// service and the driver that feeds the card.
    /// </summary>
    /// <remarks>
    /// One stream, not a mixer: two programs writing at once interleave
    /// their blocks. A mixer goes here when there is a second producer worth
    /// mixing. A program that writes faster than the card plays is told how
    /// much fit and waits; one that writes too slowly is heard as silence,
    /// never as the last block repeating — the driver clears what it played.
    /// </remarks>
    internal static unsafe class AudioOut
    {
        // About 680 ms at 48 kHz: deeper than any frame-paced producer needs,
        // short enough that a program's sound is not a second behind it.
        private const int QueueBytes = 128 * 1024;

        private static byte[] s_queue;
        private static int s_head, s_count;

        /// <summary>The card's rate, 0 without one.</summary>
        public static int SampleRate { get; private set; }

        public static bool Present => SampleRate != 0;

        /// <summary>Bytes written by programs; bytes the card took; bytes of silence it got instead.</summary>
        public static ulong Written, Played, Starved;

        public static void Attach(int sampleRate)
        {
            s_queue = new byte[QueueBytes];
            SampleRate = sampleRate;
        }

        public static int Queued => s_count;
        public static int Free => s_queue == null ? 0 : QueueBytes - s_count;

        /// <summary>Takes as much of <paramref name="length"/> as fits, whole frames only; returns it.</summary>
        public static int Write(byte* pcm, int length)
        {
            if (s_queue == null || length <= 0) return 0;
            Preemption.Suppress();
            try
            {
                int take = length < QueueBytes - s_count ? length : QueueBytes - s_count;
                take &= ~3;                                   // whole stereo frames
                int tail = (s_head + s_count) % QueueBytes;
                for (int i = 0; i < take; i++)
                {
                    s_queue[tail] = pcm[i];
                    if (++tail == QueueBytes) tail = 0;
                }
                s_count += take;
                Written += (ulong)take;
                return take;
            }
            finally { Preemption.Allow(); }
        }

        /// <summary>Fills <paramref name="length"/> bytes for the card: queued sound, then silence.</summary>
        public static void Read(byte* into, int length)
        {
            Preemption.Suppress();
            try
            {
                int take = s_queue == null ? 0 : (length < s_count ? length : s_count);
                for (int i = 0; i < take; i++)
                {
                    into[i] = s_queue[s_head];
                    if (++s_head == QueueBytes) s_head = 0;
                }
                s_count -= take;
                Played += (ulong)take;
                for (int i = take; i < length; i++) into[i] = 0;
                if (take < length && take > 0) Starved += (ulong)(length - take);
            }
            finally { Preemption.Allow(); }
        }

        /// <summary>Drops what is queued (a program that stops, or starts over).</summary>
        public static void Clear()
        {
            Preemption.Suppress();
            s_head = 0;
            s_count = 0;
            Preemption.Allow();
        }
    }
}
