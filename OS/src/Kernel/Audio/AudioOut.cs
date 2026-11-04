using OS.Kernel.Threading;

namespace OS.Kernel.Audio
{
    /// <summary>
    /// The sound programs write (step246), mixed (step250): up to MaxStreams
    /// streams of PCM — 16-bit signed, stereo, interleaved, at the card's rate
    /// — each with its own queue and owner process, summed with clipping when
    /// the driver asks for a buffer.
    /// </summary>
    /// <remarks>
    /// A process has a default stream, opened by its first AudioWrite: the
    /// one-stream programs (tone, beep, Fami, DOOM's effects) never name one.
    /// A program with more than one sound (DOOM: effects and music) opens
    /// more. Streams end with their process, so a program that dies is not
    /// heard after it. A stream with nothing queued adds silence; one that
    /// runs dry mid-buffer adds what it had — the others play on.
    /// </remarks>
    internal static unsafe class AudioOut
    {
        public const int MaxStreams = 16;

        // About 680 ms at 48 kHz per stream: deeper than any frame-paced
        // producer needs, short enough that a program's sound is not a second
        // behind it.
        private const int QueueBytes = 128 * 1024;

        private sealed class Stream
        {
            public int Id;
            public uint Owner;
            public bool IsDefault;
            public byte[] Queue;
            public int Head, Count;
        }

        private static Stream[] s_streams;
        private static int s_nextId = 1;
        private static int[] s_mix;

        /// <summary>The card's rate, 0 without one.</summary>
        public static int SampleRate { get; private set; }

        public static bool Present => SampleRate != 0;

        /// <summary>Bytes written by programs; bytes the card took from them; bytes of silence a dry stream left.</summary>
        public static ulong Written, Played, Starved;

        public static void Attach(int sampleRate)
        {
            s_streams = new Stream[MaxStreams];
            s_mix = new int[8192];
            SampleRate = sampleRate;
        }

        /// <summary>A new stream for <paramref name="owner"/>; its id, or -1 when all are taken.</summary>
        public static int Open(uint owner) => OpenCore(owner, isDefault: false);

        private static int OpenCore(uint owner, bool isDefault)
        {
            if (s_streams == null) return -1;
            var queue = new byte[QueueBytes];     // outside the suppression: allocation may collect
            Preemption.Suppress();
            try
            {
                for (int i = 0; i < MaxStreams; i++)
                {
                    if (s_streams[i] != null) continue;
                    int id = s_nextId++;
                    s_streams[i] = new Stream { Id = id, Owner = owner, IsDefault = isDefault, Queue = queue };
                    return id;
                }
                return -1;
            }
            finally { Preemption.Allow(); }
        }

        /// <summary>The owner's default stream, opened on first use; -1 when none is free.</summary>
        public static int DefaultOf(uint owner)
        {
            if (s_streams == null) return -1;
            for (int i = 0; i < MaxStreams; i++)
            {
                Stream s = s_streams[i];
                if (s != null && s.Owner == owner && s.IsDefault) return s.Id;
            }
            return OpenCore(owner, isDefault: true);
        }

        private static Stream Find(int id, uint owner)
        {
            if (s_streams == null) return null;
            for (int i = 0; i < MaxStreams; i++)
            {
                Stream s = s_streams[i];
                if (s != null && s.Id == id && s.Owner == owner) return s;
            }
            return null;
        }

        public static bool Close(int id, uint owner)
        {
            if (s_streams == null) return false;
            Preemption.Suppress();
            try
            {
                for (int i = 0; i < MaxStreams; i++)
                    if (s_streams[i] != null && s_streams[i].Id == id && s_streams[i].Owner == owner) { s_streams[i] = null; return true; }
                return false;
            }
            finally { Preemption.Allow(); }
        }

        /// <summary>Every stream of a process that ended: its sound stops with it.</summary>
        public static void OnProcessEnded(uint owner)
        {
            if (s_streams == null) return;
            Preemption.Suppress();
            for (int i = 0; i < MaxStreams; i++)
                if (s_streams[i] != null && s_streams[i].Owner == owner) s_streams[i] = null;
            Preemption.Allow();
        }

        /// <summary>Bytes queued in the stream, -1 for no such stream.</summary>
        public static int Queued(int id, uint owner) => Find(id, owner)?.Count ?? -1;

        /// <summary>Bytes that fit in the stream now, -1 for no such stream.</summary>
        public static int Free(int id, uint owner)
        {
            Stream s = Find(id, owner);
            return s == null ? -1 : QueueBytes - s.Count;
        }

        /// <summary>Takes as much of <paramref name="length"/> as fits, whole frames only; returns it, -1 for no such stream.</summary>
        public static int Write(int id, uint owner, byte* pcm, int length)
        {
            Stream s = Find(id, owner);
            if (s == null) return -1;
            if (length <= 0) return 0;
            Preemption.Suppress();
            try
            {
                int take = length < QueueBytes - s.Count ? length : QueueBytes - s.Count;
                take &= ~3;                                   // whole stereo frames
                byte[] q = s.Queue;
                int tail = (s.Head + s.Count) % QueueBytes;
                for (int i = 0; i < take; i++)
                {
                    q[tail] = pcm[i];
                    if (++tail == QueueBytes) tail = 0;
                }
                s.Count += take;
                Written += (ulong)take;
                return take;
            }
            finally { Preemption.Allow(); }
        }

        /// <summary>Drops what the stream has queued.</summary>
        public static bool Clear(int id, uint owner)
        {
            Stream s = Find(id, owner);
            if (s == null) return false;
            Preemption.Suppress();
            s.Head = 0;
            s.Count = 0;
            Preemption.Allow();
            return true;
        }

        /// <summary>Fills <paramref name="length"/> bytes for the card: every stream's sound, summed and clipped.</summary>
        public static void Read(byte* into, int length)
        {
            int samples = length / 2;
            short* output = (short*)into;
            Preemption.Suppress();
            try
            {
                if (s_streams == null || s_mix == null || samples > s_mix.Length)
                {
                    for (int i = 0; i < length; i++) into[i] = 0;
                    return;
                }

                int[] mix = s_mix;
                for (int i = 0; i < samples; i++) mix[i] = 0;

                for (int n = 0; n < MaxStreams; n++)
                {
                    Stream s = s_streams[n];
                    if (s == null || s.Count == 0) continue;
                    int take = length < s.Count ? length : s.Count;
                    byte[] q = s.Queue;
                    int head = s.Head;
                    for (int b = 0; b + 1 < take; b += 2)
                    {
                        int lo = q[head];
                        if (++head == QueueBytes) head = 0;
                        int hi = q[head];
                        if (++head == QueueBytes) head = 0;
                        mix[b >> 1] += (short)(lo | (hi << 8));
                    }
                    s.Head = head;
                    s.Count -= take & ~1;
                    Played += (ulong)take;
                    if (take < length) Starved += (ulong)(length - take);
                }

                for (int i = 0; i < samples; i++)
                {
                    int v = mix[i];
                    output[i] = (short)(v > 32767 ? 32767 : v < -32768 ? -32768 : v);
                }
            }
            finally { Preemption.Allow(); }
        }
    }
}
