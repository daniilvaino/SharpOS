using System;
using System.Collections.Generic;
using OS.Kernel.Memory;
using OS.Kernel.Threading;
using SharpOS.Std.Exchange;
using SharpOS.Std.Pipes;

namespace OS.Kernel.Pipes
{
    // The native pipe's transport (pipe spec Р6, Р11–Р13, Р24–Р27, Р30, Р45).
    //
    // A pipe is one-way: a queue in the kernel and two ends, writer and reader.
    // An end is a handle in the end table with one holder — an app run (its
    // generation) or the kernel (ExchangeHeap.OwnerKernel) — and closes when
    // the holder closes it or ends. Messages are exchange-heap blocks holding
    // regions; the pipe owns a block while it is queued (owner PipeOwnerBase |
    // pipe id), the sender before, the receiver after.
    //
    // The queue holds at most Capacity messages. An app writer at the limit
    // waits for room; the kernel never waits (Р45): its message, or the
    // oldest, is dropped by the pipe's policy and counted, and the next
    // message received carries how many were dropped before it.
    //
    // The pipe owns its type description: the writer's schema is copied into
    // the pipe when it is declared and refers to nothing of the writer's. A
    // reader that declared a type is checked against it when the two ends meet
    // (Р22); a reader that declared none reads by the description.
    //
    // Ends close two ways. Close is the orderly one: a closed writer is "end
    // of stream" to its reader once the queue is drained, and so is the writer
    // of a run that returned normally with the end open — exit closes what the
    // run holds, as a process exit closes its files. A run ended by a failure
    // breaks its ends: "broken", also after the queue is drained. A writer whose reader is gone gets "broken" on its next send,
    // and the queue is freed.
    //
    // Single CPU: every table and queue edit runs with preemption suppressed.
    // Waits are AddressWait on the pipe's version word, bumped on every change.
    internal static unsafe class KernelPipes
    {
        public const uint PipeOwnerBase = 0xF000_0000;
        private const int MaxEnds = 256;
        private const uint MaxCapacity = 4096;

        // Bytes a queue may hold besides its message count (step197): the
        // exchange arena is fixed and shared, and 4096 messages of 64 KiB —
        // READ of a big file — outgrow all of it. A message alone in the queue
        // always goes.
        private const ulong MaxQueuedBytes = 4UL << 20;

        internal sealed class Pipe
        {
            public uint Id;
            public string Name;
            public uint Capacity;
            public PipeOverflow Overflow;
            public int WriterEnd;           // end index, -1 when not attached yet
            public int ReaderEnd;
            public bool WriterGone;          // closed or ended
            public bool WriterBroke;         // ended without Close
            public bool ReaderGone;
            public ulong[] Blocks;
            public ulong[] Lengths;
            public uint[] Dropped;
            public int Head;
            public int Count;
            public ulong QueuedBytes;
            public uint DroppedPending;
            public ulong Sent;
            public ulong Lost;
            public byte[] Schema;            // the pipe's own copy
            public ulong RootKey;
            public byte[] ReaderSchema;      // a reader's declared type, until the writer comes
            public ulong ReaderKey;
            public int Version;

            // The writer's type is not the one the reader declared (step196):
            // the writer was not refused — the type is its — and the reader's
            // receives answer TypeMismatch; it builds the text itself.
            public bool Mismatch;

            public uint BlockOwner => PipeOwnerBase | Id;
        }

        private struct End
        {
            public Pipe Pipe;
            public uint Holder;
            public PipeRole Role;
            public bool Open;
        }

        private static End[] s_ends;
        private static List<Pipe> s_named;
        private static uint s_nextId;
        private static uint s_livePipes;

        public static uint LivePipes => s_livePipes;

        /// <summary>Pipes waiting by name for their second end.</summary>
        public static int NamedWaiting => s_named?.Count ?? 0;

        /// <summary>The run calling a service, or the kernel when no app is running on this thread.</summary>
        public static uint CallerHolder()
        {
            uint generation = Scheduler.Current?.AppGeneration ?? 0;
            return generation == 0 ? ExchangeHeap.OwnerKernel : generation;
        }

        // ---- creating and connecting ----

        /// <summary>A pipe with both ends held by <paramref name="holder"/> (Р27).</summary>
        public static PipeStatus Create(uint holder, uint capacity, PipeOverflow overflow, out int writer, out int reader)
        {
            writer = reader = 0;
            Preemption.Suppress();
            try
            {
                Pipe p = NewPipe(null, capacity, overflow);
                int w = NewEnd(p, holder, PipeRole.Writer);
                int r = w < 0 ? -1 : NewEnd(p, holder, PipeRole.Reader);
                if (r < 0)
                {
                    if (w >= 0) s_ends[w] = default;
                    return PipeStatus.NoMemory;
                }
                p.WriterEnd = w;
                p.ReaderEnd = r;
                s_livePipes++;
                writer = w + 1;
                reader = r + 1;
                return PipeStatus.Ok;
            }
            finally
            {
                Preemption.Allow();
            }
        }

        /// <summary>
        /// One end of the pipe called <paramref name="name"/> (Р30). The first
        /// side to come creates the pipe and waits for its peer without
        /// blocking; the second joins it, and the name is free again. A writer
        /// declares its schema and root key; a reader its own description of
        /// the type it expects, or none (root key 0) to read by the writer's.
        /// </summary>
        public static PipeStatus Connect(uint holder, string name, PipeRole role, uint capacity, PipeOverflow overflow,
                                         byte[] schema, ulong rootKey, out int handle, out string error)
        {
            handle = 0;
            error = null;
            if (string.IsNullOrEmpty(name) || (role != PipeRole.Writer && role != PipeRole.Reader))
                return PipeStatus.BadHandle;

            Preemption.Suppress();
            try
            {
                s_named ??= new List<Pipe>();
                Pipe p = null;
                int index = -1;
                for (int i = 0; i < s_named.Count; i++)
                    if (s_named[i].Name == name) { p = s_named[i]; index = i; break; }

                if (p == null)
                {
                    p = NewPipe(name, capacity, overflow);
                    int e = NewEnd(p, holder, role);
                    if (e < 0) return PipeStatus.NoMemory;
                    Attach(p, e, role, schema, rootKey);
                    s_named.Add(p);
                    s_livePipes++;
                    handle = e + 1;
                    return PipeStatus.Ok;
                }

                if ((role == PipeRole.Writer ? p.WriterEnd : p.ReaderEnd) >= 0)
                    return PipeStatus.NameTaken;

                byte[] writerSchema = role == PipeRole.Writer ? schema : p.Schema;
                ulong writerKey = role == PipeRole.Writer ? rootKey : p.RootKey;
                byte[] readerSchema = role == PipeRole.Reader ? schema : p.ReaderSchema;
                ulong readerKey = role == PipeRole.Reader ? rootKey : p.ReaderKey;
                error = TypeCheck.Compare(writerSchema, writerKey, readerSchema, readerKey);
                // The type is the writer's: a reader that comes second with
                // another is refused; a writer that comes second is not — the
                // reader learns of it at its first receive.
                if (error != null && role == PipeRole.Reader)
                    return PipeStatus.TypeMismatch;
                if (error != null)
                {
                    p.Mismatch = true;
                    error = null;
                }

                int end = NewEnd(p, holder, role);
                if (end < 0) return PipeStatus.NoMemory;
                Attach(p, end, role, schema, rootKey);
                s_named.RemoveAt(index);
                Bump(p);
                handle = end + 1;
                return PipeStatus.Ok;
            }
            finally
            {
                Preemption.Allow();
            }
        }

        /// <summary>The writer's declaration on a pipe made by Create: the description is copied into the pipe.</summary>
        public static PipeStatus Declare(uint holder, int handle, byte[] schema, ulong rootKey)
            => DeclareEnd(holder, handle, schema, rootKey, out _);

        /// <summary>
        /// The type an end of a pair (Create) is opened with (step194 §5): the
        /// first end opened with a type declares it, the other is checked
        /// against it, as when two ends meet by name. A writer's description
        /// becomes the pipe's; a reader with none (root key 0) reads by it.
        /// </summary>
        public static PipeStatus DeclareEnd(uint holder, int handle, byte[] schema, ulong rootKey, out string error)
        {
            error = null;
            Preemption.Suppress();
            try
            {
                if (!TryEnd(holder, handle, (PipeRole)0, out Pipe p)) return PipeStatus.BadHandle;
                if (s_ends[handle - 1].Role == PipeRole.Writer)
                {
                    // Never refused: a reader that declared another type is
                    // told at its next receive (step196).
                    if (p.ReaderSchema != null)
                        p.Mismatch = TypeCheck.Compare(schema, rootKey, p.ReaderSchema, p.ReaderKey) != null;
                    p.Schema = Copy(schema);
                    p.RootKey = rootKey;
                }
                else if (rootKey != 0)
                {
                    if (p.Schema != null)
                    {
                        error = TypeCheck.Compare(p.Schema, p.RootKey, schema, rootKey);
                        if (error != null) return PipeStatus.TypeMismatch;
                    }
                    p.ReaderSchema = Copy(schema);
                    p.ReaderKey = rootKey;
                }
                Bump(p);
                return PipeStatus.Ok;
            }
            finally
            {
                Preemption.Allow();
            }
        }

        /// <summary>Whether <paramref name="handle"/> is an open end of <paramref name="role"/> held by <paramref name="holder"/>.</summary>
        public static bool IsEnd(uint holder, int handle, PipeRole role)
        {
            Preemption.Suppress();
            try { return TryEnd(holder, handle, role, out _); }
            finally { Preemption.Allow(); }
        }

        /// <summary>
        /// Hands an end to another holder (a process being started, step194
        /// §5): the handle stays the same number and is the new holder's now;
        /// the old one's calls on it fail as on a closed end.
        /// </summary>
        public static PipeStatus Transfer(uint from, int handle, uint to)
        {
            Preemption.Suppress();
            try
            {
                if (!TryEnd(from, handle, (PipeRole)0, out _)) return PipeStatus.BadHandle;
                s_ends[handle - 1].Holder = to;
                return PipeStatus.Ok;
            }
            finally
            {
                Preemption.Allow();
            }
        }

        /// <summary>The description the writer declared; null when none yet.</summary>
        public static byte[] SchemaOf(uint holder, int handle, out ulong rootKey)
        {
            rootKey = 0;
            Preemption.Suppress();
            try
            {
                if (!TryEnd(holder, handle, (PipeRole)0, out Pipe p)) return null;
                rootKey = p.RootKey;
                return p.Schema;
            }
            finally
            {
                Preemption.Allow();
            }
        }

        // ---- sending and receiving ----

        /// <summary>
        /// Queues <paramref name="block"/> (an exchange block the holder owns).
        /// An app writer at the limit waits; the kernel drops by the pipe's
        /// policy and returns Ok — the loss is the reader's to see.
        /// </summary>
        public static PipeStatus Send(uint holder, int handle, void* block, ulong length)
        {
            Preemption.Suppress();
            try
            {
                if (!TryEnd(holder, handle, PipeRole.Writer, out Pipe p)) return PipeStatus.BadHandle;
                if (ExchangeHeap.OwnerOf(block) != holder || ExchangeHeap.SizeOf(block) < length || length == 0)
                    return PipeStatus.BadBlock;

                while (true)
                {
                    if (p.ReaderGone) return PipeStatus.Broken;
                    if ((uint)p.Count < p.Capacity && (p.Count == 0 || p.QueuedBytes + length <= MaxQueuedBytes)) break;

                    if (holder == ExchangeHeap.OwnerKernel)
                    {
                        if (p.Overflow == PipeOverflow.DropNewest)
                        {
                            ExchangeHeap.Free(block);
                            p.DroppedPending++;
                            p.Lost++;
                            p.Sent++;
                            return PipeStatus.Ok;
                        }
                        DropHead(p);
                        break;
                    }

                    // An app writer waits for room, the reader leaving, or its own end closing.
                    // A thread being ended stops waiting: it leaves on the way back.
                    if (Scheduler.Current?.KillRequested ?? false) return PipeStatus.Broken;
                    int seen = p.Version;
                    Preemption.Allow();
                    fixed (int* version = &p.Version)
                        AddressWait.WaitOnAddress(version, &seen, 4, 0xFFFFFFFFu);
                    Preemption.Suppress();
                    if (!TryEnd(holder, handle, PipeRole.Writer, out _)) return PipeStatus.BadHandle;
                }

                ExchangeHeap.SetOwner(block, p.BlockOwner);
                int tail = (p.Head + p.Count) % p.Blocks.Length;
                p.Blocks[tail] = (ulong)block;
                p.Lengths[tail] = length;
                p.Dropped[tail] = p.DroppedPending;
                p.DroppedPending = 0;
                p.Count++;
                p.QueuedBytes += length;
                p.Sent++;
                Bump(p);
                return PipeStatus.Ok;
            }
            finally
            {
                Preemption.Allow();
            }
        }

        /// <summary>
        /// The next message, now the receiver's. With <paramref name="wait"/>
        /// the caller sleeps on an empty queue; without it Empty comes back.
        /// After the writer is gone the queue is drained first, then EndOfStream
        /// or Broken.
        /// </summary>
        public static PipeStatus Receive(uint holder, int handle, bool wait,
                                         out void* block, out ulong length, out uint dropped)
        {
            block = null;
            length = 0;
            dropped = 0;
            Preemption.Suppress();
            try
            {
                if (!TryEnd(holder, handle, PipeRole.Reader, out Pipe p)) return PipeStatus.BadHandle;
                if (p.Mismatch) return PipeStatus.TypeMismatch;
                while (p.Count == 0)
                {
                    if (p.WriterGone)
                    {
                        dropped = p.DroppedPending;
                        p.DroppedPending = 0;
                        return p.WriterBroke ? PipeStatus.Broken : PipeStatus.EndOfStream;
                    }
                    if (!wait) return PipeStatus.Empty;

                    // A thread being ended stops waiting: it leaves on the way back.
                    if (Scheduler.Current?.KillRequested ?? false) return PipeStatus.Broken;
                    int seen = p.Version;
                    Preemption.Allow();
                    fixed (int* version = &p.Version)
                        AddressWait.WaitOnAddress(version, &seen, 4, 0xFFFFFFFFu);
                    Preemption.Suppress();
                    if (!TryEnd(holder, handle, PipeRole.Reader, out _)) return PipeStatus.BadHandle;
                    if (p.Mismatch) return PipeStatus.TypeMismatch;
                }

                block = (void*)p.Blocks[p.Head];
                length = p.Lengths[p.Head];
                dropped = p.Dropped[p.Head];
                p.Blocks[p.Head] = 0;
                p.Head = (p.Head + 1) % p.Blocks.Length;
                p.Count--;
                p.QueuedBytes -= length;
                ExchangeHeap.SetOwner(block, holder);
                Bump(p);
                return PipeStatus.Ok;
            }
            finally
            {
                Preemption.Allow();
            }
        }

        /// <summary>Messages sent and lost on the pipe of this end, for the probes.</summary>
        public static bool Counters(uint holder, int handle, out ulong sent, out ulong lost, out int queued)
        {
            sent = lost = 0;
            queued = 0;
            Preemption.Suppress();
            try
            {
                if (!TryEnd(holder, handle, (PipeRole)0, out Pipe p)) return false;
                sent = p.Sent;
                lost = p.Lost;
                queued = p.Count;
                return true;
            }
            finally
            {
                Preemption.Allow();
            }
        }

        /// <summary>
        /// Waits until the other end of a pipe by name has come (or gone):
        /// a stage that closes its output must not close before its reader
        /// came, or the end of the stream is lost with the pipe (step196).
        /// </summary>
        public static PipeStatus WaitPeer(uint holder, int handle)
        {
            Preemption.Suppress();
            try
            {
                if (!TryEnd(holder, handle, (PipeRole)0, out Pipe p)) return PipeStatus.BadHandle;
                bool writer = s_ends[handle - 1].Role == PipeRole.Writer;
                while ((writer ? p.ReaderEnd : p.WriterEnd) == -1)
                {
                    if (Scheduler.Current?.KillRequested ?? false) return PipeStatus.Broken;
                    int seen = p.Version;
                    Preemption.Allow();
                    fixed (int* version = &p.Version)
                        AddressWait.WaitOnAddress(version, &seen, 4, 0xFFFFFFFFu);
                    Preemption.Suppress();
                    if (!TryEnd(holder, handle, (PipeRole)0, out _)) return PipeStatus.BadHandle;
                }
                return PipeStatus.Ok;
            }
            finally
            {
                Preemption.Allow();
            }
        }

        /// <summary>Whether the other end of this one is gone (closed or its holder ended).</summary>
        public static bool PeerGone(uint holder, int handle)
        {
            Preemption.Suppress();
            try
            {
                if (!TryEnd(holder, handle, (PipeRole)0, out Pipe p)) return true;
                return s_ends[handle - 1].Role == PipeRole.Reader ? p.WriterGone : p.ReaderGone;
            }
            finally
            {
                Preemption.Allow();
            }
        }

        // ---- closing ----

        public static PipeStatus Close(uint holder, int handle)
        {
            Preemption.Suppress();
            try
            {
                if (!TryEnd(holder, handle, (PipeRole)0, out _)) return PipeStatus.BadHandle;
                CloseEnd(handle - 1, broke: false);
                return PipeStatus.Ok;
            }
            finally
            {
                Preemption.Allow();
            }
        }

        /// <summary>
        /// Closes an end as a holder that died would leave it: the reader sees
        /// the stream broken, not ended. For tests of the break.
        /// </summary>
        public static PipeStatus Break(uint holder, int handle)
        {
            Preemption.Suppress();
            try
            {
                if (!TryEnd(holder, handle, (PipeRole)0, out _)) return PipeStatus.BadHandle;
                CloseEnd(handle - 1, broke: true);
                return PipeStatus.Ok;
            }
            finally
            {
                Preemption.Allow();
            }
        }

        /// <summary>
        /// A run ended: every end it still holds closes — as Close would after a
        /// normal exit, broken after a failed one (pipe spec Р25). Returns how many.
        /// </summary>
        public static int OnHolderEnded(uint holder, bool failed)
        {
            int closed = 0;
            Preemption.Suppress();
            try
            {
                if (s_ends == null) return 0;
                for (int i = 0; i < s_ends.Length; i++)
                {
                    if (!s_ends[i].Open || s_ends[i].Holder != holder) continue;
                    CloseEnd(i, broke: failed);
                    closed++;
                }
            }
            finally
            {
                Preemption.Allow();
            }
            return closed;
        }

        private static void CloseEnd(int index, bool broke)
        {
            Pipe p = s_ends[index].Pipe;
            PipeRole role = s_ends[index].Role;
            s_ends[index] = default;

            if (role == PipeRole.Writer)
            {
                p.WriterGone = true;
                p.WriterBroke = broke;
                p.WriterEnd = -2;
            }
            else
            {
                // Nobody will read the queue: it goes back now.
                p.ReaderGone = true;
                p.ReaderEnd = -2;
                ExchangeHeap.ReleaseOwner(p.BlockOwner);
                p.Count = 0;
                p.QueuedBytes = 0;
                p.Head = 0;
            }

            // A pipe still waiting for its peer by name loses the name with its only end.
            if (p.Name != null && s_named != null && s_named.Contains(p))
            {
                s_named.Remove(p);
                ReleasePipe(p);
            }
            else if (p.WriterGone && p.ReaderGone)
            {
                ReleasePipe(p);
            }
            Bump(p);
        }

        private static void ReleasePipe(Pipe p)
        {
            ExchangeHeap.ReleaseOwner(p.BlockOwner);
            p.Count = 0;
            p.QueuedBytes = 0;
            if (s_livePipes > 0) s_livePipes--;
        }

        // ---- internals; callers hold the suppression ----

        private static Pipe NewPipe(string name, uint capacity, PipeOverflow overflow)
        {
            if (capacity == 0) capacity = 1;
            if (capacity > MaxCapacity) capacity = MaxCapacity;
            return new Pipe
            {
                Id = ++s_nextId,
                Name = name,
                Capacity = capacity,
                Overflow = overflow,
                WriterEnd = -1,
                ReaderEnd = -1,
                Blocks = new ulong[capacity],
                Lengths = new ulong[capacity],
                Dropped = new uint[capacity],
            };
        }

        private static int NewEnd(Pipe p, uint holder, PipeRole role)
        {
            s_ends ??= new End[MaxEnds];
            for (int i = 0; i < s_ends.Length; i++)
            {
                if (s_ends[i].Open) continue;
                s_ends[i] = new End { Pipe = p, Holder = holder, Role = role, Open = true };
                return i;
            }
            return -1;
        }

        private static void Attach(Pipe p, int end, PipeRole role, byte[] schema, ulong rootKey)
        {
            if (role == PipeRole.Writer)
            {
                p.WriterEnd = end;
                p.Schema = Copy(schema);
                p.RootKey = rootKey;
            }
            else
            {
                p.ReaderEnd = end;
                p.ReaderSchema = Copy(schema);
                p.ReaderKey = rootKey;
            }
        }

        private static bool TryEnd(uint holder, int handle, PipeRole role, out Pipe pipe)
        {
            pipe = null;
            int i = handle - 1;
            if (s_ends == null || i < 0 || i >= s_ends.Length) return false;
            ref End e = ref s_ends[i];
            if (!e.Open || e.Holder != holder) return false;
            if (role != 0 && e.Role != role) return false;
            pipe = e.Pipe;
            return true;
        }

        // The oldest message goes; the one now at the head inherits its count
        // plus itself, or the pending count does when the queue empties.
        private static void DropHead(Pipe p)
        {
            uint carried = p.Dropped[p.Head] + 1;
            ExchangeHeap.Free((void*)p.Blocks[p.Head]);
            p.Blocks[p.Head] = 0;
            p.QueuedBytes -= p.Lengths[p.Head];
            p.Head = (p.Head + 1) % p.Blocks.Length;
            p.Count--;
            p.Lost++;
            if (p.Count > 0) p.Dropped[p.Head] += carried;
            else p.DroppedPending += carried;
        }

        private static void Bump(Pipe p)
        {
            p.Version++;
            fixed (int* version = &p.Version)
                AddressWait.WakeByAddressAll(version);
        }

        private static byte[] Copy(byte[] bytes)
        {
            if (bytes == null) return null;
            var copy = new byte[bytes.Length];
            for (int i = 0; i < bytes.Length; i++) copy[i] = bytes[i];
            return copy;
        }
    }
}
