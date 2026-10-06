using System;
using System.Runtime.CompilerServices;
using SharpOS.Std.Exchange;

namespace SharpOS.Std.Pipes
{
    /// <summary>Pipe-wide settings and the pair constructor.</summary>
    public static partial class Pipe
    {
        /// <summary>
        /// Overwrite the table words of a block returned to the exchange heap
        /// (Region.Scrub). On in debug builds; tests switch it on.
        /// </summary>
#if DEBUG
        public static bool ScrubOnDispose = true;
#else
        public static bool ScrubOnDispose;
#endif

        /// <summary>Both ends of a new pipe, held by this image (pipe spec Р27).</summary>
        public static PipeStatus Create<T>(uint capacity, PipeOverflow overflow,
                                           out PipeWriter<T> writer, out PipeReader<T> reader) where T : class
        {
            writer = null;
            reader = null;
            PipeStatus status = PipeTransport.Create(capacity, overflow, out int w, out int r);
            if (status != PipeStatus.Ok) return status;
            ulong key = MessageCatalog.KeyOf(typeof(T));
            if (key == 0)
            {
                PipeTransport.Close(w);
                PipeTransport.Close(r);
                return PipeStatus.Refused;
            }
            PipeTransport.Declare(w, MessageCatalog.Schema, key);
            writer = new PipeWriter<T>(w, key);
            reader = new PipeReader<T>(r, key);
            return PipeStatus.Ok;
        }
    }

    /// <summary>
    /// A received region: a graph in an exchange block this image owns,
    /// translated to its own types (pipe spec Р9, Р15). Read it in place through
    /// Root, copy it out with ToHeap, send it on with PipeWriter.Move, or give
    /// it back with Dispose. After Dispose or Move every member throws.
    /// </summary>
    public sealed unsafe class Region<T> : IDisposable where T : class
    {
        private const int Live = 0, Disposed = 1, Moved = 2;

        internal byte* Block;
        internal ulong Length;
        private int _state;
        private object _root;

        internal Region(byte* block, ulong length, object root, uint droppedBefore)
        {
            Block = block;
            Length = length;
            _root = root;
            DroppedBefore = droppedBefore;
        }

        /// <summary>Messages the pipe dropped right before this one (a writer that never waits).</summary>
        public uint DroppedBefore { get; }

        /// <summary>The root object, read in place.</summary>
        public T Root
        {
            get
            {
                ThrowIfGone();
                return Unsafe.As<T>(_root);
            }
        }

        /// <summary>The whole graph copied into this image's heap: ordinary objects, free to change.</summary>
        public T ToHeap()
        {
            ThrowIfGone();
            return Unsafe.As<T>(Region.ToHeap(Block, Length));
        }

        /// <summary>Gives the block back to the exchange heap. A second call does nothing.</summary>
        public void Dispose()
        {
            if (_state != Live) return;
            _state = Disposed;
            _root = null;
            if (Pipe.ScrubOnDispose)
                Region.Scrub(Block, Length);
            PipeTransport.Free(Block);
        }

        internal void MarkMoved()
        {
            _state = Moved;
            _root = null;
        }

        internal void ThrowIfGone()
        {
            if (_state == Disposed) throw new ObjectDisposedException("Region", "the region was disposed");
            if (_state == Moved) throw new ObjectDisposedException("Region", "the region was moved to another pipe");
        }
    }

    /// <summary>
    /// A region received by a reader without the class (pipe spec Р33): left
    /// untranslated, read by the pipe's description.
    /// </summary>
    public sealed unsafe class RawRegion : IDisposable
    {
        private bool _gone;
        private readonly ViewScope _scope;

        internal RawRegion(byte* block, ulong length, byte[] schema, uint droppedBefore, RegionShapes shapes)
        {
            Block = block;
            Length = length;
            Schema = schema;
            DroppedBefore = droppedBefore;
            if (shapes != null) _scope = new ViewScope(block, length, shapes);
        }

        /// <summary>The root object, as a view: read and written in place, never translated.</summary>
        public View Root
        {
            get
            {
                if (_gone) throw new ObjectDisposedException("RawRegion", "the region was disposed");
                if (_scope == null) throw new InvalidOperationException("the region was not checked against its pipe's description");
                return _scope.Root;
            }
        }

        /// <summary>Ends the region's views: the block went on to another pipe, or the loop moved past it.</summary>
        internal void End(string because)
        {
            _gone = true;
            _scope?.End(because);
        }

        public byte* Block { get; }
        public ulong Length { get; }
        public byte[] Schema { get; }
        public uint DroppedBefore { get; }

        /// <summary>One line per object, by the description. The number printed; -1 on a malformed region.</summary>
        public int Print(Action<string> say, out string complaint)
        {
            if (_gone) throw new ObjectDisposedException("RawRegion", "the region was disposed");
            return RegionSchema.Print(Block, Length, Schema, say, out complaint);
        }

        public void Dispose()
        {
            if (_gone) return;
            End("the region was disposed");
            if (Pipe.ScrubOnDispose)
                Region.Scrub(Block, Length);
            PipeTransport.Free(Block);
        }
    }

    /// <summary>The writing end of a native pipe carrying <typeparamref name="T"/>.</summary>
    public sealed unsafe class PipeWriter<T> : IDisposable where T : class
    {
        private int _handle;
        private readonly ulong _key;

        internal PipeWriter(int handle, ulong key)
        {
            _handle = handle;
            _key = key;
        }

        /// <summary>The transport handle; zero once closed.</summary>
        internal int Handle => _handle;

        /// <summary>Why the last Copy or Move was refused.</summary>
        public string LastError { get; private set; }

        /// <summary>
        /// Connects by name (pipe spec Р30) and declares the type: the catalog's
        /// description travels to the pipe once, here.
        /// </summary>
        public static PipeStatus Connect(string name, out PipeWriter<T> writer, out string error,
                                         uint capacity = 16, PipeOverflow overflow = PipeOverflow.DropOldest)
        {
            writer = null;
            ulong key = MessageCatalog.KeyOf(typeof(T));
            if (key == 0)
            {
                error = "the type is not in the catalog";
                return PipeStatus.Refused;
            }
            PipeStatus status = PipeTransport.Connect(name, PipeRole.Writer, capacity, overflow,
                                                      MessageCatalog.Schema, key, out int handle, out error);
            if (status == PipeStatus.Ok)
                writer = new PipeWriter<T>(handle, key);
            return status;
        }

        /// <summary>
        /// Sends a snapshot of the graph under <paramref name="message"/>; the
        /// original stays with the sender (pipe spec Р8). A graph with a type
        /// outside the catalog or a delegate is refused before anything is sent.
        /// </summary>
        public PipeStatus Copy(T message)
        {
            ThrowIfClosed();
            LastError = null;
            MessageCatalog.Ensure();
            Region.Plan plan = Region.Lay(message, out string complaint);
            if (plan == null)
            {
                LastError = complaint;
                return PipeStatus.Refused;
            }
            byte* block = (byte*)PipeTransport.Allocate(plan.Size);
            if (block == null) return PipeStatus.NoMemory;
            Region.Write(plan, block);
            PipeStatus status = PipeTransport.Send(_handle, block, plan.Size);
            if (status != PipeStatus.Ok)
                PipeTransport.Free(block);
            return status;
        }

        /// <summary>
        /// Sends a received region on without copying (pipe spec Р8, Р12): the
        /// reverse pass turns it back into keys and offsets, and the block goes
        /// to the pipe. The region is dead afterwards. On failure the region is
        /// translated back and stays the caller's.
        /// </summary>
        public PipeStatus Move(Region<T> region)
        {
            ThrowIfClosed();
            region.ThrowIfGone();
            LastError = null;
            if (!Region.Release(region.Block, region.Length, out string complaint))
            {
                LastError = complaint;
                return PipeStatus.Refused;
            }
            PipeStatus status = PipeTransport.Send(_handle, region.Block, region.Length);
            if (status == PipeStatus.Ok)
            {
                region.MarkMoved();
                return status;
            }
            Region.Resolve(region.Block, region.Length, out _, out _);
            return status;
        }

        /// <summary>Closes the end: the reader sees the end of the stream after the queue.</summary>
        public void Dispose()
        {
            if (_handle == 0) return;
            PipeTransport.Close(_handle);
            _handle = 0;
        }

        private void ThrowIfClosed()
        {
            if (_handle == 0) throw new ObjectDisposedException("PipeWriter", "the pipe end is closed");
        }
    }

    /// <summary>The reading end of a native pipe carrying <typeparamref name="T"/>.</summary>
    public sealed unsafe partial class PipeReader<T> : IDisposable where T : class
    {
        private int _handle;
        private readonly ulong _key;

        internal PipeReader(int handle, ulong key)
        {
            _handle = handle;
            _key = key;
        }

        /// <summary>The transport handle; zero once closed.</summary>
        internal int Handle => _handle;

        /// <summary>Messages dropped after the last one received, reported with the end of the stream.</summary>
        public uint DroppedAtEnd { get; private set; }

        /// <summary>What the last receive answered: EndOfStream and Broken end the stream.</summary>
        public PipeStatus Status { get; private set; }

        /// <summary>Why the last receive could not deliver a region.</summary>
        public string LastError { get; private set; }

        /// <summary>
        /// Connects by name and declares the type this reader expects; a writer
        /// with another type or layout is refused when the two meet (pipe spec Р22).
        /// </summary>
        public static PipeStatus Connect(string name, out PipeReader<T> reader, out string error,
                                         uint capacity = 16, PipeOverflow overflow = PipeOverflow.DropOldest)
        {
            reader = null;
            ulong key = MessageCatalog.KeyOf(typeof(T));
            if (key == 0)
            {
                error = "the type is not in the catalog";
                return PipeStatus.Refused;
            }
            PipeStatus status = PipeTransport.Connect(name, PipeRole.Reader, capacity, overflow,
                                                      MessageCatalog.SchemaOf(key), key, out int handle, out error);
            if (status == PipeStatus.Ok)
                reader = new PipeReader<T>(handle, key);
            return status;
        }

        /// <summary>The next region, waiting for it; null when the stream ended (see Status).</summary>
        public Region<T> Receive()
        {
            Status = TryReceive(true, out Region<T> region);
            return region;
        }

        /// <summary>The next region if one is queued: Empty when none is.</summary>
        public PipeStatus TryReceive(out Region<T> region)
        {
            Status = TryReceive(false, out region);
            return Status;
        }

        private PipeStatus TryReceive(bool wait, out Region<T> region)
        {
            region = null;
            ThrowIfClosed();
            LastError = null;
            MessageCatalog.Ensure();
            PipeStatus status = PipeTransport.Receive(_handle, wait, out void* raw, out ulong length, out uint dropped);
            if (status == PipeStatus.EndOfStream || status == PipeStatus.Broken)
            {
                DroppedAtEnd = dropped;
                Dropped += dropped;
            }
            if (status != PipeStatus.Ok) return status;
            Dropped += dropped;

            byte* block = (byte*)raw;
            if (*(ulong*)(block + Region.HeaderSize) != _key)
            {
                LastError = "the root is not the declared type";
                PipeTransport.Free(block);
                return PipeStatus.Refused;
            }
            if (!Region.Resolve(block, length, out object root, out string complaint, out ulong missing))
            {
                LastError = missing != 0 ? "type " + WriterTypeName(missing) + " in the message is not this image's: "
                                           + "it is not in the catalog here, or its layout differs" : complaint;
                PipeTransport.Free(block);
                return PipeStatus.Refused;
            }
            region = new Region<T>(block, length, root, dropped);
            return PipeStatus.Ok;
        }

        public void Dispose()
        {
            if (_handle == 0) return;
            PipeTransport.Close(_handle);
            _handle = 0;
        }

        /// <summary>Messages the pipe lost: dropped before the ones received, and after the last.</summary>
        public long Dropped { get; private set; }

        // The writer's name for a key, from the description it declared.
        private string WriterTypeName(ulong key)
        {
            byte[] schema = PipeTransport.Schema(_handle, out _);
            var types = schema == null ? null : RegionSchema.Parse(schema, out _);
            return types != null && types.TryGetValue(key, out TypeKeys.Description d) ? d.Name : "0x" + key.ToString("x");
        }

        private void ThrowIfClosed()
        {
            if (_handle == 0) throw new ObjectDisposedException("PipeReader", "the pipe end is closed");
        }
    }

    /// <summary>A reader without the class: regions stay untranslated and are read by the description.</summary>
    public sealed unsafe partial class RawPipeReader : IDisposable
    {
        private int _handle;

        private RawPipeReader(int handle) => _handle = handle;

        public PipeStatus Status { get; private set; }

        /// <summary>The writer's description, copied from the pipe once it is declared.</summary>
        public byte[] Schema { get; private set; }

        public static PipeStatus Connect(string name, out RawPipeReader reader, out string error,
                                         uint capacity = 16, PipeOverflow overflow = PipeOverflow.DropOldest)
        {
            reader = null;
            PipeStatus status = PipeTransport.Connect(name, PipeRole.Reader, capacity, overflow, null, 0,
                                                      out int handle, out error);
            if (status == PipeStatus.Ok)
                reader = new RawPipeReader(handle);
            return status;
        }

        private RegionShapes _shapes;

        /// <summary>The root type's key the writer declared: what a pipe this one forwards to carries.</summary>
        internal ulong RootKey { get; private set; }

        /// <summary>Why the last receive could not deliver a region.</summary>
        public string LastError { get; private set; }

        /// <summary>Messages the pipe lost: dropped before the ones received, and after the last.</summary>
        public long Dropped { get; private set; }

        /// <summary>
        /// The next region, waiting for it; null when the stream ended or a
        /// message was refused (see Status). The block is checked against the
        /// pipe's description first: a record without a description, a size or
        /// a reference outside the block refuses it, and it goes back.
        /// </summary>
        public RawRegion Receive()
        {
            if (_handle == 0) throw new ObjectDisposedException("RawPipeReader", "the pipe end is closed");
            LastError = null;
            Status = PipeTransport.Receive(_handle, true, out void* raw, out ulong length, out uint dropped);
            Dropped += dropped;
            if (Status != PipeStatus.Ok) return null;
            if (Schema == null)
            {
                Schema = PipeTransport.Schema(_handle, out ulong rootKey);
                RootKey = rootKey;
            }
            if (_shapes == null)
            {
                _shapes = RegionShapes.Parse(Schema, out string bad);
                if (_shapes == null) return Refuse(raw, "the pipe's description is malformed: " + bad);
            }
            if (!_shapes.Validate((byte*)raw, length, out string complaint))
                return Refuse(raw, "a malformed message: " + complaint);
            return new RawRegion((byte*)raw, length, Schema, dropped, _shapes);
        }

        private RawRegion Refuse(void* block, string why)
        {
            PipeTransport.Free(block);
            Status = PipeStatus.Refused;
            LastError = why;
            return null;
        }

        public void Dispose()
        {
            if (_handle == 0) return;
            PipeTransport.Close(_handle);
            _handle = 0;
        }
    }
}
