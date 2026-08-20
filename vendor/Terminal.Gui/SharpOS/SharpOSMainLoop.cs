// The main loop driver for SharpOS.
//
// Terminal.Gui's MainLoop handles timers and idle work itself; a driver only
// has to answer "is there input?" and hand it over. Upstream's implementations
// are large because they bridge a foreign event system — a Win32 message queue,
// a poll() on a file descriptor. Ours reads keys from the kernel, so it is the
// small thing the interface actually describes.

using System;
using System.Threading;
using SharpOS.AppSdk;

namespace Terminal.Gui
{
    public class SharpOSMainLoop : IMainLoopDriver
    {
        private readonly SharpOSDriver _driver;
        private MainLoop _mainLoop = null!;

        private Action<KeyEvent>? _keyHandler;
        private Action<KeyEvent>? _keyDownHandler;
        private Action<KeyEvent>? _keyUpHandler;

        // One pending key. The kernel's own input pump already buffers, so a
        // second queue here would only add a place for keys to sit unnoticed.
        private KeyInfo _pending;
        private bool _hasPending;

        private int _lastCols;
        private int _lastRows;

        public SharpOSMainLoop(ConsoleDriver driver)
        {
            _driver = driver as SharpOSDriver
                ?? throw new ArgumentException(
                    "SharpOSMainLoop works with SharpOSDriver — the pair is not interchangeable with the stock ones.",
                    nameof(driver));
        }

        void IMainLoopDriver.Setup(MainLoop mainLoop)
        {
            _mainLoop = mainLoop;
            AppConsole.TryGetSize(out _lastCols, out _lastRows);
        }

        void IMainLoopDriver.Wakeup()
        {
            // Nothing to wake: EventsPending polls rather than blocking, so a
            // thread that posts work is picked up on the next turn. This exists
            // for loops that sleep on a handle, which ours does not.
        }

        bool IMainLoopDriver.EventsPending(bool wait)
        {
            CheckResize();

            if (_hasPending) return true;
            if (ReadKey()) return true;

            // Timers and idle handlers are the loop's own business, and it asks
            // us again straight after — so having none of our own still means
            // "yes, there is something to do".
            if (_mainLoop.Timeouts.Count > 0 || _mainLoop.IdleHandlers.Count > 0) return true;

            if (!wait) return false;

            // Sleep rather than spin. This is the loop's idle state — the
            // interface is waiting for a keystroke — and spinning here would
            // hold the CPU against every other thread on a single core.
            Thread.Sleep(10);
            return _hasPending || ReadKey();
        }

        void IMainLoopDriver.MainIteration()
        {
            if (!_hasPending) return;

            KeyInfo key = _pending;
            _hasPending = false;

            // Releases arrive with no character and are reported as key-up only:
            // passing one to the ordinary handler would type the character twice.
            if (key.HasRaw && !key.RawDown)
            {
                _keyUpHandler?.Invoke(SharpOSKeyMap.ToKeyEvent(key));
                return;
            }

            KeyEvent ev = SharpOSKeyMap.ToKeyEvent(key);
            _keyDownHandler?.Invoke(ev);
            _keyHandler?.Invoke(ev);
        }

        internal void SetKeyHandlers(Action<KeyEvent> keyHandler,
            Action<KeyEvent> keyDownHandler, Action<KeyEvent> keyUpHandler)
        {
            _keyHandler = keyHandler;
            _keyDownHandler = keyDownHandler;
            _keyUpHandler = keyUpHandler;
        }

        private bool ReadKey()
        {
            if (AppHost.TryReadKey(out KeyInfo key) != AppServiceStatus.Ok) return false;

            // A release with nothing else in it still has to travel: the library
            // tracks modifier state, and swallowing key-up leaves Shift stuck on.
            _pending = key;
            _hasPending = true;
            return true;
        }

        private void CheckResize()
        {
            if (!AppConsole.TryGetSize(out int cols, out int rows)) return;
            if (cols == _lastCols && rows == _lastRows) return;

            _lastCols = cols;
            _lastRows = rows;
            _driver.OnResized(cols, rows);
        }
    }
}
