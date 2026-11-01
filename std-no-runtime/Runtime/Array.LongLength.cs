// Array.LongLength (step223, the 7-Zip writer): Length as a long — arrays
// here never pass Array.MaxLength.

namespace System
{
    public abstract partial class Array
    {
        public long LongLength => Length;
    }
}
