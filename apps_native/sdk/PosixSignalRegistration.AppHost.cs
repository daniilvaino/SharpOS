// PosixSignalRegistration, the app tier's half — in the place of dotnet/runtime
// v8.0 PosixSignalRegistration.Windows.cs, whose shape it keeps: a set of
// tokens, handlers run in a thread of their own, the default action unless
// one cancels.
//
// SIGINT is Ctrl+C, which the kernel sends to the process group that owns the
// terminal (TerminalInterrupt). The kernel ends a process that does not
// listen with 130; one that does gets the signal on its listener thread,
// started with the first registration and waiting in the kernel. SIGQUIT,
// SIGTERM and SIGHUP register, as on Windows, and nothing raises them yet.
// SIGWINCH — as on Unix, not on Windows — is the main panel changing size;
// its default action is none, and it has a listener of its own, so a handler
// stuck in it does not hold Ctrl+C up.

using System.Collections.Generic;
using SharpOS.AppSdk;

namespace System.Runtime.InteropServices
{
    public sealed partial class PosixSignalRegistration
    {
        private static readonly HashSet<Token> s_registrations = new();
        private static System.Threading.Thread? s_listener, s_resizeListener;
        // Registrations of the signals the Ctrl+C listener runs, and of SIGWINCH.
        private static int s_interrupts, s_resizes;

        private static PosixSignalRegistration Register(PosixSignal signal, Action<PosixSignalContext> handler)
        {
            switch (signal)
            {
                case PosixSignal.SIGINT:
                case PosixSignal.SIGQUIT:
                case PosixSignal.SIGTERM:
                case PosixSignal.SIGHUP:
                case PosixSignal.SIGWINCH:
                    break;

                default:
                    throw new PlatformNotSupportedException();
            }

            EnsureThreads();

            var token = new Token(signal, handler);
            var registration = new PosixSignalRegistration(token);

            lock (s_registrations)
            {
                if (signal == PosixSignal.SIGWINCH)
                {
                    if (s_resizes == 0)
                    {
                        if (!Process.ResizeListening(true))
                            throw new System.IO.IOException("The kernel offers no SIGWINCH service.");
                        if (s_resizeListener == null)
                        {
                            s_resizeListener = new System.Threading.Thread(ListenResize) { IsBackground = true };
                            s_resizeListener.Start();
                        }
                    }
                    s_resizes++;
                }
                else
                {
                    if (s_interrupts == 0)
                    {
                        if (!Process.InterruptSettings(2, true, out _, out _))
                            throw new System.IO.IOException("The kernel offers no Ctrl+C service.");
                        if (s_listener == null)
                        {
                            s_listener = new System.Threading.Thread(Listen) { IsBackground = true };
                            s_listener.Start();
                        }
                    }
                    s_interrupts++;
                }

                s_registrations.Add(token);
            }

            return registration;
        }

        private static bool s_threads;

        /// <summary>
        /// The listener is a thread, and an app has threads once it asks (the
        /// shell, the launcher do at start). Before any lock is taken: a lock
        /// held while threads come in is held by a thread id that changes.
        /// </summary>
        internal static void EnsureThreads()
        {
            if (s_threads) return;
            if (!TaskBackendInstaller.Install())
                throw new System.IO.IOException("No threads for the Ctrl+C listener: the kernel offers none.");
            s_threads = true;
        }

        private void Unregister()
        {
            lock (s_registrations)
            {
                if (_token is Token token)
                {
                    _token = null;

                    s_registrations.Remove(token);
                    if (token.Signal == PosixSignal.SIGWINCH)
                    {
                        if (--s_resizes == 0)
                            Process.ResizeListening(false);
                    }
                    else if (--s_interrupts == 0)
                    {
                        Process.InterruptSettings(2, false, out _, out _);
                    }
                }
            }
        }

        // The listener: one pass a Ctrl+C (those that came while the handlers
        // ran count as one, as a pending signal does). Back from the kernel
        // with false when the process is being ended.
        private static void Listen()
        {
            uint handled = 0;
            while (Process.WaitInterrupt(handled, out uint count))
            {
                handled = count;
                if (!HandlerRoutine(PosixSignal.SIGINT))
                    Process.EndInterrupted();
            }
        }

        // SIGWINCH: nothing to do by default, so what the handlers return is
        // not looked at.
        private static void ListenResize()
        {
            uint handled = 0;
            while (Process.WaitResize(handled, out uint count))
            {
                handled = count;
                HandlerRoutine(PosixSignal.SIGWINCH);
            }
        }

        // As HandlerRoutine of the Windows half: true when a handler cancelled
        // the default action.
        private static bool HandlerRoutine(PosixSignal signal)
        {
            List<Token>? tokens = null;
            lock (s_registrations)
            {
                foreach (Token token in s_registrations)
                {
                    if (token.Signal == signal)
                    {
                        (tokens ??= new List<Token>()).Add(token);
                    }
                }
            }

            if (tokens is null)
            {
                return false;
            }

            var context = new PosixSignalContext(signal);
            foreach (Token handler in tokens)
            {
                handler.Handler(context);
            }

            return context.Cancel;
        }
    }
}
