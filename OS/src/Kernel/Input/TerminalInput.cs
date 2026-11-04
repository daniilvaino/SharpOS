using System;
using OS.Hal;
using SharpOS.Std;

namespace OS.Kernel.Input
{
    /// <summary>
    /// The terminal's input as bytes (step217): what a program reads from its
    /// terminal on Unix — keys as a terminal sends them, and the terminal's
    /// answers to the queries the program wrote.
    /// </summary>
    /// <remarks>
    /// Keys come from the same events as the raw key service
    /// (Keyboard.TryReadKeyRaw): one source, one modifier latch, so a game
    /// reading raw events and a shell reading bytes never take each other's
    /// half of a key. A press becomes what xterm sends for it
    /// (SharpOS.Std.ConsoleKeys, shared with the readers): a printable
    /// key UTF-8, Enter CR, Backspace DEL, Ctrl+letter its control byte, Alt
    /// an ESC before the key, the arrows and the navigation cluster CSI
    /// sequences, F1–F4 SS3 and F5–F12 CSI n ~, modifiers on those as
    /// "1;m". Releases are not bytes. The keys the screen takes for itself
    /// never get here (InputPump).
    ///
    /// The terminal engine's answers (cursor position, device attributes,
    /// sizes) wait in a queue of their own and go out ahead of keys: a
    /// program that asked is waiting for exactly that.
    ///
    /// Fixed rings, no allocation: answers are queued from inside the
    /// terminal's write path, with switching suppressed.
    /// </remarks>
    internal static unsafe class TerminalInput
    {
        private const int AnswerSize = 512;
        private static readonly byte[] s_answers = new byte[AnswerSize];
        private static int s_answerHead, s_answerCount;

        // A key's bytes that did not fit the reader's buffer.
        private const int CarrySize = 32;
        private static readonly byte[] s_carry = new byte[CarrySize];
        private static int s_carryHead, s_carryCount;

        public static ulong AnswersDropped { get; private set; }

        /// <summary>The terminal's answer to a query, queued for the reader (TerminalConsole's delegate).</summary>
        public static void Answer(byte[] data)
        {
            if (data == null) return;
            for (int i = 0; i < data.Length; i++)
            {
                if (s_answerCount == AnswerSize) { AnswersDropped++; continue; }
                s_answers[(s_answerHead + s_answerCount) % AnswerSize] = data[i];
                s_answerCount++;
            }
        }

        /// <summary>
        /// Up to <paramref name="capacity"/> bytes into <paramref name="buffer"/>,
        /// without waiting: the terminal's answers first, then keys. 0 when
        /// there is nothing; negative when the keyboard is unsupported.
        /// </summary>
        public static int Read(byte* buffer, int capacity)
        {
            int n = 0;
            while (n < capacity && s_answerCount > 0)
            {
                buffer[n++] = s_answers[s_answerHead];
                s_answerHead = (s_answerHead + 1) % AnswerSize;
                s_answerCount--;
            }
            while (n < capacity && s_carryCount > 0)
            {
                buffer[n++] = s_carry[s_carryHead];
                s_carryHead = (s_carryHead + 1) % CarrySize;
                s_carryCount--;
            }

            byte* key = stackalloc byte[CarrySize];
            while (n < capacity)
            {
                KeyReadStatus status = Keyboard.TryReadKeyRaw(out KeyInfo info, out uint raw);
                if (status == KeyReadStatus.Unsupported) return n > 0 ? n : -1;
                if (status != KeyReadStatus.KeyAvailable) break;

                int length = Encode(info.UnicodeChar, info.ScanCode, raw, key);
                if (length > 0) TerminalConsole.KeyForProgram();
                int i = 0;
                for (; i < length && n < capacity; i++) buffer[n++] = key[i];
                for (; i < length && s_carryCount < CarrySize; i++)
                {
                    s_carry[(s_carryHead + s_carryCount) % CarrySize] = key[i];
                    s_carryCount++;
                }
            }
            return n;
        }

        // One key press as a terminal's bytes; 0 for a release or a modifier.
        // The key is the one the raw key service hands out (step243), with the
        // modifiers stamped on the event when it was decoded.
        private static int Encode(ushort unicode, ushort scan, uint raw, byte* o)
        {
            if (raw != 0 && (raw & 0x200u) == 0) return 0;              // a release
            ConsoleModifiers modifiers = 0;
            if ((raw & 0x400u) != 0) modifiers |= ConsoleModifiers.Shift;
            if ((raw & 0x800u) != 0) modifiers |= ConsoleModifiers.Control;
            if ((raw & 0x1000u) != 0) modifiers |= ConsoleModifiers.Alt;
            ConsoleKeyInfo key = ConsoleKeys.FromKeyboard((char)unicode, scan, modifiers);
            return ConsoleKeys.ToTerminal(key, new Span<byte>(o, CarrySize));
        }
    }
}
