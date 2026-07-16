// System.Diagnostics.Stopwatch — app-tier stub. The app service table has
// no time source yet, so Start/Stop track running state but Elapsed always
// reads zero (ManagedDoom uses it for demo-FPS statistics only). TODO:
// back with a kernel tick service when the main loop needs real pacing.

namespace System.Diagnostics
{
    public class Stopwatch
    {
        private bool _isRunning;

        public void Start() => _isRunning = true;

        public void Stop() => _isRunning = false;

        public void Reset() => _isRunning = false;

        public void Restart() => _isRunning = true;

        public bool IsRunning => _isRunning;

        public TimeSpan Elapsed => default;

        public long ElapsedMilliseconds => 0;

        public long ElapsedTicks => 0;

        public static Stopwatch StartNew()
        {
            var sw = new Stopwatch();
            sw.Start();
            return sw;
        }
    }
}
