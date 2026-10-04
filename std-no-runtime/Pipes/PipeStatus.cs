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
