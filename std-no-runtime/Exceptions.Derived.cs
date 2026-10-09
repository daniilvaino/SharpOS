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

        // BCL (.NET 7) helper. Without CallerArgumentExpression the default
        // paramName stays null, and this ArgumentException keeps no paramName
        // anyway; the throw itself is the BCL's.
        public static void ThrowIfNullOrEmpty(string argument, string paramName = null)
        {
            if (string.IsNullOrEmpty(argument))
            {
                ArgumentNullException.ThrowIfNull(argument, paramName);
                throw new ArgumentException("The value cannot be an empty string.", paramName);
            }
        }
    }

    public class ArgumentNullException : ArgumentException
    {
        public ArgumentNullException() { }
        public ArgumentNullException(string paramName) : base(null, paramName) { }
        public ArgumentNullException(string paramName, string message) : base(message, paramName) { }

        // BCL helper: `ArgumentNullException.ThrowIfNull(arg)` is a common
        // pattern in verbatim-ported BCL code. Throws for real: these used to
        // spin in place from before the unwinder existed.
        public static void ThrowIfNull(object argument)
        {
            if (argument == null) Throw(null);
        }

        public static void ThrowIfNull(object argument, string paramName)
        {
            if (argument == null) Throw(paramName);
        }

        public static void Throw() => throw new ArgumentNullException();
        public static void Throw(string paramName) => throw new ArgumentNullException(paramName);
    }

    public class ArgumentOutOfRangeException : ArgumentException
    {
        public ArgumentOutOfRangeException() { }
        public ArgumentOutOfRangeException(string paramName) : base(null, paramName) { }
        public ArgumentOutOfRangeException(string paramName, string message)
            : base(message, paramName) { }
        public ArgumentOutOfRangeException(string paramName, object actualValue, string message)
            : base(message, paramName) { }

        // The ThrowIf family (System.Private.CoreLib ArgumentOutOfRangeException.cs,
        // .NET 8+, MIT; messages from its Strings.resx). The comparison ones are
        // upstream's, generic over IComparable<T>/IEquatable<T>. ThrowIfZero,
        // ThrowIfNegative and ThrowIfNegativeOrZero are generic over INumberBase<T>
        // upstream; std has no generic math, so they come as overloads for the
        // types callers pass (int, long, nint, double) — source-compatible for those.
        public static void ThrowIfZero(int value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? paramName = null) { if (value == 0) ThrowZero(value, paramName); }
        public static void ThrowIfZero(long value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? paramName = null) { if (value == 0) ThrowZero(value, paramName); }
        public static void ThrowIfZero(nint value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? paramName = null) { if (value == 0) ThrowZero(value, paramName); }
        public static void ThrowIfZero(double value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? paramName = null) { if (value == 0) ThrowZero(value, paramName); }

        public static void ThrowIfNegative(int value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? paramName = null) { if (value < 0) ThrowNegative(value, paramName); }
        public static void ThrowIfNegative(long value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? paramName = null) { if (value < 0) ThrowNegative(value, paramName); }
        public static void ThrowIfNegative(nint value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? paramName = null) { if (value < 0) ThrowNegative(value, paramName); }
        public static void ThrowIfNegative(double value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? paramName = null) { if (value < 0) ThrowNegative(value, paramName); }

        public static void ThrowIfNegativeOrZero(int value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? paramName = null) { if (value <= 0) ThrowNegativeOrZero(value, paramName); }
        public static void ThrowIfNegativeOrZero(long value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? paramName = null) { if (value <= 0) ThrowNegativeOrZero(value, paramName); }
        public static void ThrowIfNegativeOrZero(nint value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? paramName = null) { if (value <= 0) ThrowNegativeOrZero(value, paramName); }
        public static void ThrowIfNegativeOrZero(double value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? paramName = null) { if (value <= 0) ThrowNegativeOrZero(value, paramName); }

        public static void ThrowIfEqual<T>(T value, T other, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? paramName = null) where T : IEquatable<T>?
        {
            if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(value, other))
                Throw(paramName, value, "{0} ('{1}') must not be equal to '{2}'.", (object?)value ?? "null", (object?)other ?? "null");
        }

        public static void ThrowIfNotEqual<T>(T value, T other, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? paramName = null) where T : IEquatable<T>?
        {
            if (!System.Collections.Generic.EqualityComparer<T>.Default.Equals(value, other))
                Throw(paramName, value, "{0} ('{1}') must be equal to '{2}'.", (object?)value ?? "null", (object?)other ?? "null");
        }

        public static void ThrowIfGreaterThan<T>(T value, T other, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? paramName = null) where T : IComparable<T>
        {
            if (value.CompareTo(other) > 0) Throw(paramName, value, "{0} ('{1}') must be less than or equal to '{2}'.", value, other);
        }

        public static void ThrowIfGreaterThanOrEqual<T>(T value, T other, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? paramName = null) where T : IComparable<T>
        {
            if (value.CompareTo(other) >= 0) Throw(paramName, value, "{0} ('{1}') must be less than '{2}'.", value, other);
        }

        public static void ThrowIfLessThan<T>(T value, T other, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? paramName = null) where T : IComparable<T>
        {
            if (value.CompareTo(other) < 0) Throw(paramName, value, "{0} ('{1}') must be greater than or equal to '{2}'.", value, other);
        }

        public static void ThrowIfLessThanOrEqual<T>(T value, T other, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? paramName = null) where T : IComparable<T>
        {
            if (value.CompareTo(other) <= 0) Throw(paramName, value, "{0} ('{1}') must be greater than '{2}'.", value, other);
        }

        private static void ThrowZero(object value, string? paramName) => Throw(paramName, value, "{0} ('{1}') must be a non-zero value.", value, null);
        private static void ThrowNegative(object value, string? paramName) => Throw(paramName, value, "{0} ('{1}') must be a non-negative value.", value, null);
        private static void ThrowNegativeOrZero(object value, string? paramName) => Throw(paramName, value, "{0} ('{1}') must be a non-negative and non-zero value.", value, null);

        private static void Throw(string? paramName, object? value, string format, object? shown, object? other) =>
            throw new ArgumentOutOfRangeException(paramName, value, string.Format(format, paramName, shown, other));
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

    // ── Для ThrowHelpers ────────────────────────────────────────────
    // ILC зовёт помощники по фиксированным именам, и до сих пор каждый из
    // них был вечным циклом. Чтобы бросать по-настоящему, нужны сами типы;
    // иерархия — как в BCL, только базой везде Exception (SystemException у
    // нас нет).

    public class TypeLoadException : Exception
    {
        public TypeLoadException() { }
        public TypeLoadException(string message) : base(message) { }
        public TypeLoadException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class MissingMemberException : Exception
    {
        public MissingMemberException() { }
        public MissingMemberException(string message) : base(message) { }
        public MissingMemberException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class MissingFieldException : MissingMemberException
    {
        public MissingFieldException() { }
        public MissingFieldException(string message) : base(message) { }
        public MissingFieldException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class MissingMethodException : MissingMemberException
    {
        public MissingMethodException() { }
        public MissingMethodException(string message) : base(message) { }
        public MissingMethodException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class InvalidProgramException : Exception
    {
        public InvalidProgramException() { }
        public InvalidProgramException(string message) : base(message) { }
        public InvalidProgramException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public class BadImageFormatException : Exception
    {
        public BadImageFormatException() { }
        public BadImageFormatException(string message) : base(message) { }
        public BadImageFormatException(string message, Exception innerException)
            : base(message, innerException) { }
    }

    public sealed class InsufficientExecutionStackException : Exception
    {
        public InsufficientExecutionStackException() : base("Insufficient stack to continue executing the program safely. This can happen from having too many functions on the call stack or function on the stack using too much stack space.") { }
        public InsufficientExecutionStackException(string message) : base(message) { }
        public InsufficientExecutionStackException(string message, Exception innerException) : base(message, innerException) { }
    }
}

namespace System.Collections.Generic
{
    public class KeyNotFoundException : Exception
    {
        public KeyNotFoundException() { }
        public KeyNotFoundException(string message) : base(message) { }
        public KeyNotFoundException(string message, Exception innerException)
            : base(message, innerException) { }
    }
}
