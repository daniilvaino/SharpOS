namespace SharpOS.Std.Pipes
{
    /// <summary>What a pipe operation answered. Shared by the kernel transport and every image.</summary>
    public enum PipeStatus
    {
        /// <summary>Done.</summary>
        Ok = 0,
        /// <summary>Nothing to receive right now (a receive that does not wait).</summary>
        Empty = 1,
        /// <summary>The writer closed the pipe and the queue is drained.</summary>
        EndOfStream = 2,
        /// <summary>The other end is gone without closing: its holder died, or the reader left.</summary>
        Broken = 3,
        /// <summary>The handle is not an open end held by the caller, or the end has the other role.</summary>
        BadHandle = -1,
        /// <summary>The block is not a live exchange block of the caller, or too small.</summary>
        BadBlock = -2,
        /// <summary>The two ends declared different types; the error text names the difference.</summary>
        TypeMismatch = -3,
        /// <summary>The name already has an end of that role waiting.</summary>
        NameTaken = -4,
        /// <summary>No memory, or no room in the handle table.</summary>
        NoMemory = -5,
        /// <summary>The kernel does not offer pipes.</summary>
        Unsupported = -6,
        /// <summary>The graph cannot be sent: a type outside the catalog, or a delegate.</summary>
        Refused = -7,
    }

    /// <summary>
    /// A reference stored into a region that does not point into the same
    /// block: a heap object, a literal, another region's object. Thrown by the
    /// write barrier; the store did not happen.
    /// </summary>
    public sealed class RegionReferenceException : System.InvalidOperationException
    {
        public RegionReferenceException()
            : base("a region may only refer to null or to objects of its own block")
        {
        }
    }

    /// <summary>
    /// A pipe operation of the convenient layer that failed: the other end is
    /// gone, the types differ, a message cannot be taken. <see cref="Status"/>
    /// is the low layer's answer.
    /// </summary>
    public sealed class PipeException : System.Exception
    {
        public PipeStatus Status { get; }

        public PipeException(PipeStatus status, string message)
            : base(message)
        {
            Status = status;
        }

        internal PipeException(PipeStatus status, string message, int handle)
            : base(message)
        {
            Status = status;
            Handle = handle;
        }

        /// <summary>The transport handle of the end that failed; 0 when not known.</summary>
        internal int Handle { get; }

        /// <summary>
        /// The other end of the standard input or output went away without
        /// closing (step197): a program that leaves this unhandled ends quietly
        /// with 141, as a Unix program does on SIGPIPE.
        /// </summary>
        public bool IsStandardEndBroken
            => Status == PipeStatus.Broken && Handle != 0
               && (Handle == PipeTransport.StandardEnd(0) || Handle == PipeTransport.StandardEnd(1));
    }

    /// <summary>Which end of a pipe.</summary>
    public enum PipeRole
    {
        Writer = 1,
        Reader = 2,
    }

    /// <summary>
    /// What a full queue does with a message from a writer that never waits
    /// (the kernel, pipe spec Р45). An app writer waits instead.
    /// </summary>
    public enum PipeOverflow
    {
        /// <summary>The oldest queued message goes.</summary>
        DropOldest = 0,
        /// <summary>The new message goes.</summary>
        DropNewest = 1,
    }
}
