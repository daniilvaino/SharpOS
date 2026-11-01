// System.Threading.Thread — the handle, and since step223 a way to start one.
//
// Most callers ask only which thread am I, what is its id, put me to sleep,
// and what culture is it using (Terminal.Gui wants all four). Start and Join
// came with ZstdSharp's worker pool. They go through the same door as
// Task.Run — ThreadBackend.Spawn, the tier's scheduler — so there is still
// one way to make a thread, not two with their own bugs.
//
// Kept in step with ManagedThreadIds: the id here is the same id Monitor keys
// ownership on, so "am I the main thread?" and "do I hold this lock?" cannot
// disagree. A thread made with a constructor learns its id when it starts.

using System.Globalization;

namespace System.Threading
{
    public delegate void ThreadStart();

    public sealed class ThreadStateException : Exception
    {
        public ThreadStateException() : base("Thread was in an invalid state for the operation being executed.") { }
        public ThreadStateException(string? message) : base(message) { }
        public ThreadStateException(string? message, Exception? innerException) : base(message, innerException) { }
    }

    public sealed class ThreadStartException : Exception
    {
        internal ThreadStartException(string? message) : base(message) { }
    }

    public delegate void ParameterizedThreadStart(object? obj);

    public sealed class Thread
    {
        private int _id;

        // A thread made here: what it runs, and 0 not started / 1 running /
        // 2 finished — the word Join blocks on.
        private readonly Delegate? _start;
        private object? _startArgument;
        private int _state;

        private Thread(int id) { _id = id; }

        public Thread(ThreadStart start) : this(start, 0) { }

        public Thread(ThreadStart start, int maxStackSize)
        {
            ArgumentNullException.ThrowIfNull(start);
            if (maxStackSize < 0) throw new ArgumentOutOfRangeException(nameof(maxStackSize), "Non-negative number required.");
            _start = start;
        }

        public Thread(ParameterizedThreadStart start) : this(start, 0) { }

        public Thread(ParameterizedThreadStart start, int maxStackSize)
        {
            ArgumentNullException.ThrowIfNull(start);
            if (maxStackSize < 0) throw new ArgumentOutOfRangeException(nameof(maxStackSize), "Non-negative number required.");
            _start = start;
        }

        public void Start() => StartCore(null);

        public void Start(object? parameter)
        {
            if (_start is ThreadStart)
                throw new InvalidOperationException("The thread was created with a ThreadStart delegate that does not accept a parameter.");
            StartCore(parameter);
        }

        private void StartCore(object? parameter)
        {
            if (_start == null || Interlocked.CompareExchange(ref _state, 1, 0) != 0)
                throw new ThreadStateException("Thread is running or terminated; it cannot restart.");
            _startArgument = parameter;
            if (!ThreadBackend.Spawn(Run))
            {
                Volatile.Write(ref _state, 0);
                throw new ThreadStartException("Thread failed to start: this tier has no threads to give.");
            }
        }

        private void Run()
        {
            _id = ManagedThreadIds.Current;
            try
            {
                if (_start is ThreadStart start) start();
                else ((ParameterizedThreadStart)_start!)(_startArgument);
            }
            finally
            {
                _startArgument = null;
                Volatile.Write(ref _state, 2);
                ThreadBackend.WakeAll(ref _state);
            }
        }

        public void Join() => Join(Timeout.Infinite);

        public bool Join(int millisecondsTimeout)
        {
            if (millisecondsTimeout < -1) throw new ArgumentOutOfRangeException(nameof(millisecondsTimeout), "Number must be either non-negative and less than or equal to Int32.MaxValue or -1.");
            if (_start == null) throw new ThreadStateException("Join on a handle for a running thread is not supported here.");
            if (Volatile.Read(ref _state) == 0) throw new ThreadStateException("Thread has not been started.");
            if (millisecondsTimeout == Timeout.Infinite)
            {
                while (Volatile.Read(ref _state) == 1) ThreadBackend.WaitWhile(ref _state, 1, ThreadBackend.Infinite);
                return true;
            }
            for (int waited = 0; waited < millisecondsTimeout && Volatile.Read(ref _state) == 1; waited++)
                ThreadBackend.WaitWhile(ref _state, 1, (uint)(millisecondsTimeout - waited));
            return Volatile.Read(ref _state) == 2;
        }

        public bool Join(TimeSpan timeout) => Join((int)timeout.TotalMilliseconds);

        /// <summary>
        /// A handle for the running thread. A fresh object each time rather
        /// than a cached one per thread: caching would need per-thread storage,
        /// which this environment does not have (see Threading.ManagedThreadId.cs),
        /// and every caller uses it for its id and then drops it.
        /// </summary>
        public static Thread CurrentThread => new Thread(ManagedThreadIds.Current);

        public int ManagedThreadId => _id;

        /// <summary>
        /// A thread made here is alive between Start and the end of its
        /// delegate. A handle for the running thread (CurrentThread) always is.
        /// </summary>
        public bool IsAlive => _start == null || Volatile.Read(ref _state) == 1;

        public bool IsBackground { get; set; }

        public string? Name { get; set; }

        // One culture in the system, so these are answers rather than settings.
        // Assignment is accepted and dropped, which is the honest behaviour for
        // a system that has nothing to switch to — see std Globalization.cs.
        public CultureInfo CurrentCulture
        {
            get => CultureInfo.InvariantCulture;
            set { }
        }

        public CultureInfo CurrentUICulture
        {
            get => CultureInfo.InvariantCulture;
            set { }
        }

        public static void Sleep(int millisecondsTimeout)
        {
            if (millisecondsTimeout <= 0) return;
            ThreadBackend.Sleep((uint)millisecondsTimeout);
        }

        public static void Yield() => ThreadBackend.Sleep(0);
    }
}
