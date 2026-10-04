using System.Runtime.InteropServices;
using OS.Hal;
using OS.Hal.Idt;
using static Iced.Intel.AssemblerRegisters;

namespace OS.Kernel.Diagnostics
{
    // A hardware write watch for hunting heap corruption: DR0 holds an
    // address, DR7 arms it for 8-byte writes, and the #DB that follows the
    // write is logged — RIP of the instruction after the store, the stack
    // above it — then the watch is disarmed and the code resumes. The
    // registers are per CPU and the scheduler does not save them, which on
    // one CPU makes the watch global: whichever thread writes is caught.
    internal static unsafe class WriteWatch
    {
        private static delegate* unmanaged<ulong, void> s_set;
        private static delegate* unmanaged<void> s_clear;
        private static ulong s_address;
        public static int Hits;

        private sealed class Writer : Iced.Intel.CodeWriter
        {
            private readonly byte* _p;
            public int Count;
            public Writer(byte* p) { _p = p; }
            public override void WriteByte(byte value) => _p[Count++] = value;
        }

        /// <summary>Two stubs in <paramref name="buffer"/> (at least 64 bytes, executable).</summary>
        public static bool Install(byte* buffer)
        {
            if (buffer == null) return false;
            var a = new Iced.Intel.Assembler(64);
            a.mov(dr0, rcx);
            a.mov(rax, 0x90001UL);          // L0, RW0 = write, LEN0 = 8 bytes
            a.mov(dr7, rax);
            a.ret();
            var w = new Writer(buffer);
            a.Assemble(w, (ulong)buffer);
            int setLength = (w.Count + 15) & ~15;

            var c = new Iced.Intel.Assembler(64);
            c.xor(eax, eax);
            c.mov(dr7, rax);
            c.mov(dr6, rax);
            c.ret();
            var w2 = new Writer(buffer + setLength);
            c.Assemble(w2, (ulong)(buffer + setLength));

            s_set = (delegate* unmanaged<ulong, void>)buffer;
            s_clear = (delegate* unmanaged<void>)(buffer + setLength);
            Console.Write("[watch] installed at 0x"); Console.WriteHex((ulong)buffer);
            Console.Write(" set "); Console.WriteUInt((uint)w.Count);
            Console.Write(" clear "); Console.WriteUInt((uint)w2.Count);
            Console.WriteLine("");
            return true;
        }

        public static void Arm(ulong address)
        {
            if (s_set == null || address == 0)
            {
                Console.WriteLine("[watch] not installed, cannot arm");
                return;
            }
            s_address = address;
            s_set(address);
            Console.Write("[watch] armed on 0x"); Console.WriteHex(address); Console.WriteLine("");
        }

        private struct Seen { public fixed ulong Rip[32]; }
        private static Seen s_seen;
        private static int s_seenCount;

        /// <summary>The #DB after a watched write: one line each, the stack once per writer.</summary>
        public static void OnDebugTrap(InterruptFrame* frame)
        {
            Hits++;
            if (Hits >= 2000 && s_clear != null) s_clear();
            else if (s_set != null) s_set(s_address);

            Console.Write("[watch] write 0x"); Console.WriteHex(s_address);
            Console.Write(" = 0x"); Console.WriteHex(*(ulong*)s_address);
            Console.Write(" rip=0x"); Console.WriteHex(frame->Rip);
            Console.WriteLine("");

            bool known = false;
            fixed (ulong* seen = s_seen.Rip)
            {
                for (int i = 0; i < s_seenCount; i++) if (seen[i] == frame->Rip) { known = true; break; }
                if (!known && s_seenCount < 32) seen[s_seenCount++] = frame->Rip;
            }
            if (known) return;

            Console.Write("[watch]   new writer rsp=0x"); Console.WriteHex(frame->Rsp); Console.WriteLine("");
            ulong* sp = (ulong*)frame->Rsp;
            for (int i = 0; i < 32; i++)
            {
                ulong v = sp[i];
                if ((v >= 0x100000000UL && v < 0x110000000UL) || (v >= 0x7D000000UL && v < 0x7F000000UL))
                {
                    Console.Write("[watch]     [rsp+0x"); Console.WriteHex((ulong)(i * 8));
                    Console.Write("] 0x"); Console.WriteHex(v);
                    Console.WriteLine("");
                }
            }
        }
    }
}
