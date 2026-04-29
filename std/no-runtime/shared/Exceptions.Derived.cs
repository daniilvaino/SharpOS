// Derived exception types for SharpOS std.
//
// Step 1 of the Phase 1 try/catch roadmap. Together with Exception.cs
// and Runtime/ExceptionIDs.cs, this file provides the closed set of
// exception types that the runtime's GetRuntimeException is allowed
// to instantiate, plus the user-facing types Roslyn-emitted code
// commonly throws.
//
// Existing types (previously in Threading.cs):
//   InvalidOperationException
//   NotSupportedException
//   ArgumentException
//   ArgumentNullException
//   ArgumentOutOfRangeException
//   OutOfMemoryException
//   IndexOutOfRangeException
//   FormatException
//
// New types added in step 1 (per sage 2 plan, required so RhThrowEx /
// GetRuntimeException can return concrete instances rather than generic
// Exception):
//   ArithmeticException        — base for DivideByZero/Overflow
//   DivideByZeroException
//   OverflowException
//   InvalidCastException
//   ArrayTypeMismatchException
//   NullReferenceException
//   NotImplementedException

namespace System
{
    public class InvalidOperationException : Exception
    {
        public InvalidOperationException() { }
        public InvalidOperationException(string message) : base(message) { }
        public InvalidOperationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class NotSupportedException : Exception
    {
        public NotSupportedException() { }
        public NotSupportedException(string message) : base(message) { }
        public NotSupportedException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class ArgumentException : Exception
    {
        public ArgumentException() { }
        public ArgumentException(string message) : base(message) { }
        public ArgumentException(string message, string paramName) : base(message) { }
        public ArgumentException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class ArgumentNullException : ArgumentException
    {
        public ArgumentNullException() { }
        public ArgumentNullException(string paramName) : base(null, paramName) { }
        public ArgumentNullException(string paramName, string message) : base(message, paramName) { }

        // BCL helper: `ArgumentNullException.ThrowIfNull(arg)` is a common
        // pattern in verbatim-ported BCL code. Halts via the exception
        // throw path — which itself halts in our runtime until step 5
        // wires the unwinder.
        public static void ThrowIfNull(object argument)
        {
            if (argument == null) Throw();
        }

        public static void ThrowIfNull(object argument, string paramName)
        {
            if (argument == null) Throw(paramName);
        }

        public static void Throw() { while (true) { } }
        public static void Throw(string paramName) { while (true) { } }
    }

    public class ArgumentOutOfRangeException : ArgumentException
    {
        public ArgumentOutOfRangeException() { }
        public ArgumentOutOfRangeException(string paramName) : base(null, paramName) { }
        public ArgumentOutOfRangeException(string paramName, string message)
            : base(message, paramName) { }
        public ArgumentOutOfRangeException(string paramName, object actualValue, string message)
            : base(message, paramName) { }
    }

    public class OutOfMemoryException : Exception
    {
        public OutOfMemoryException() { }
        public OutOfMemoryException(string message) : base(message) { }
        public OutOfMemoryException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class IndexOutOfRangeException : Exception
    {
        public IndexOutOfRangeException() { }
        public IndexOutOfRangeException(string message) : base(message) { }
        public IndexOutOfRangeException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class FormatException : Exception
    {
        public FormatException() { }
        public FormatException(string message) : base(message) { }
        public FormatException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    // ── New in step 1 ───────────────────────────────────────────────

    // Base class for arithmetic-domain errors. BCL sets it as parent of
    // DivideByZeroException, OverflowException, NotFiniteNumberException.
    public class ArithmeticException : Exception
    {
        public ArithmeticException() { }
        public ArithmeticException(string message) : base(message) { }
        public ArithmeticException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class DivideByZeroException : ArithmeticException
    {
        public DivideByZeroException() { }
        public DivideByZeroException(string message) : base(message) { }
        public DivideByZeroException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class OverflowException : ArithmeticException
    {
        public OverflowException() { }
        public OverflowException(string message) : base(message) { }
        public OverflowException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class InvalidCastException : Exception
    {
        public InvalidCastException() { }
        public InvalidCastException(string message) : base(message) { }
        public InvalidCastException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class ArrayTypeMismatchException : Exception
    {
        public ArrayTypeMismatchException() { }
        public ArrayTypeMismatchException(string message) : base(message) { }
        public ArrayTypeMismatchException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class NullReferenceException : Exception
    {
        public NullReferenceException() { }
        public NullReferenceException(string message) : base(message) { }
        public NullReferenceException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class AccessViolationException : Exception
    {
        public AccessViolationException() { }
        public AccessViolationException(string message) : base(message) { }
        public AccessViolationException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class NotImplementedException : Exception
    {
        public NotImplementedException() { }
        public NotImplementedException(string message) : base(message) { }
        public NotImplementedException(string message, Exception innerException)
            : base(message, innerException) { }
    }
}
