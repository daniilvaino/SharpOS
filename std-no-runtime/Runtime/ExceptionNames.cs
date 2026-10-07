using System;

namespace SharpOS.Std.Runtime
{
    /// <summary>
    /// The name of an exception's type, without reflection: there is no
    /// object.GetType() here, and no metadata to read a name from.
    /// </summary>
    /// <remarks>
    /// Type tests over the exception types std defines, the most derived
    /// first; the exact type is told by its MethodTable. An exception of a
    /// type of the app's own is named by the nearest std type it derives from.
    /// Literals only: the unhandled-exception report asks while the exception
    /// is still unwinding, where nothing may be allocated (step194 §3).
    /// </remarks>
    public static unsafe class ExceptionNames
    {
        public static string NameOf(Exception e)
        {
            if (e == null) return "(no exception)";
            void* mt = **(void***)System.Runtime.CompilerServices.Unsafe.AsPointer(ref e);
            if (e is SharpOS.Std.Pipes.PipeException) return mt == System.EETypePtr.EETypePtrOf<SharpOS.Std.Pipes.PipeException>().ToPointer() ? "SharpOS.Std.Pipes.PipeException" : "a type derived from SharpOS.Std.Pipes.PipeException";
            if (e is SharpOS.Std.Pipes.RegionReferenceException) return mt == System.EETypePtr.EETypePtrOf<SharpOS.Std.Pipes.RegionReferenceException>().ToPointer() ? "SharpOS.Std.Pipes.RegionReferenceException" : "a type derived from SharpOS.Std.Pipes.RegionReferenceException";
            if (e is System.Text.DecoderFallbackException) return mt == System.EETypePtr.EETypePtrOf<System.Text.DecoderFallbackException>().ToPointer() ? "System.Text.DecoderFallbackException" : "a type derived from System.Text.DecoderFallbackException";
            if (e is System.Text.EncoderFallbackException) return mt == System.EETypePtr.EETypePtrOf<System.Text.EncoderFallbackException>().ToPointer() ? "System.Text.EncoderFallbackException" : "a type derived from System.Text.EncoderFallbackException";
            if (e is ArgumentNullException) return mt == System.EETypePtr.EETypePtrOf<ArgumentNullException>().ToPointer() ? "System.ArgumentNullException" : "a type derived from System.ArgumentNullException";
            if (e is ArgumentOutOfRangeException) return mt == System.EETypePtr.EETypePtrOf<ArgumentOutOfRangeException>().ToPointer() ? "System.ArgumentOutOfRangeException" : "a type derived from System.ArgumentOutOfRangeException";
            if (e is DivideByZeroException) return mt == System.EETypePtr.EETypePtrOf<DivideByZeroException>().ToPointer() ? "System.DivideByZeroException" : "a type derived from System.DivideByZeroException";
            if (e is OverflowException) return mt == System.EETypePtr.EETypePtrOf<OverflowException>().ToPointer() ? "System.OverflowException" : "a type derived from System.OverflowException";
            if (e is MissingFieldException) return mt == System.EETypePtr.EETypePtrOf<MissingFieldException>().ToPointer() ? "System.MissingFieldException" : "a type derived from System.MissingFieldException";
            if (e is MissingMethodException) return mt == System.EETypePtr.EETypePtrOf<MissingMethodException>().ToPointer() ? "System.MissingMethodException" : "a type derived from System.MissingMethodException";
            if (e is System.IO.EndOfStreamException) return mt == System.EETypePtr.EETypePtrOf<System.IO.EndOfStreamException>().ToPointer() ? "System.IO.EndOfStreamException" : "a type derived from System.IO.EndOfStreamException";
            if (e is System.IO.FileNotFoundException) return mt == System.EETypePtr.EETypePtrOf<System.IO.FileNotFoundException>().ToPointer() ? "System.IO.FileNotFoundException" : "a type derived from System.IO.FileNotFoundException";
            if (e is ObjectDisposedException) return mt == System.EETypePtr.EETypePtrOf<ObjectDisposedException>().ToPointer() ? "System.ObjectDisposedException" : "a type derived from System.ObjectDisposedException";
            if (e is PlatformNotSupportedException) return mt == System.EETypePtr.EETypePtrOf<PlatformNotSupportedException>().ToPointer() ? "System.PlatformNotSupportedException" : "a type derived from System.PlatformNotSupportedException";
            if (e is ArgumentException) return mt == System.EETypePtr.EETypePtrOf<ArgumentException>().ToPointer() ? "System.ArgumentException" : "a type derived from System.ArgumentException";
            if (e is ArithmeticException) return mt == System.EETypePtr.EETypePtrOf<ArithmeticException>().ToPointer() ? "System.ArithmeticException" : "a type derived from System.ArithmeticException";
            if (e is MissingMemberException) return mt == System.EETypePtr.EETypePtrOf<MissingMemberException>().ToPointer() ? "System.MissingMemberException" : "a type derived from System.MissingMemberException";
            if (e is System.IO.IOException) return mt == System.EETypePtr.EETypePtrOf<System.IO.IOException>().ToPointer() ? "System.IO.IOException" : "a type derived from System.IO.IOException";
            if (e is InvalidOperationException) return mt == System.EETypePtr.EETypePtrOf<InvalidOperationException>().ToPointer() ? "System.InvalidOperationException" : "a type derived from System.InvalidOperationException";
            if (e is NotSupportedException) return mt == System.EETypePtr.EETypePtrOf<NotSupportedException>().ToPointer() ? "System.NotSupportedException" : "a type derived from System.NotSupportedException";
            if (e is AccessViolationException) return mt == System.EETypePtr.EETypePtrOf<AccessViolationException>().ToPointer() ? "System.AccessViolationException" : "a type derived from System.AccessViolationException";
            if (e is ArrayTypeMismatchException) return mt == System.EETypePtr.EETypePtrOf<ArrayTypeMismatchException>().ToPointer() ? "System.ArrayTypeMismatchException" : "a type derived from System.ArrayTypeMismatchException";
            if (e is BadImageFormatException) return mt == System.EETypePtr.EETypePtrOf<BadImageFormatException>().ToPointer() ? "System.BadImageFormatException" : "a type derived from System.BadImageFormatException";
            if (e is FormatException) return mt == System.EETypePtr.EETypePtrOf<FormatException>().ToPointer() ? "System.FormatException" : "a type derived from System.FormatException";
            if (e is IndexOutOfRangeException) return mt == System.EETypePtr.EETypePtrOf<IndexOutOfRangeException>().ToPointer() ? "System.IndexOutOfRangeException" : "a type derived from System.IndexOutOfRangeException";
            if (e is InvalidCastException) return mt == System.EETypePtr.EETypePtrOf<InvalidCastException>().ToPointer() ? "System.InvalidCastException" : "a type derived from System.InvalidCastException";
            if (e is InvalidProgramException) return mt == System.EETypePtr.EETypePtrOf<InvalidProgramException>().ToPointer() ? "System.InvalidProgramException" : "a type derived from System.InvalidProgramException";
            if (e is System.Collections.Generic.KeyNotFoundException) return mt == System.EETypePtr.EETypePtrOf<System.Collections.Generic.KeyNotFoundException>().ToPointer() ? "System.Collections.Generic.KeyNotFoundException" : "a type derived from System.Collections.Generic.KeyNotFoundException";
            if (e is NotImplementedException) return mt == System.EETypePtr.EETypePtrOf<NotImplementedException>().ToPointer() ? "System.NotImplementedException" : "a type derived from System.NotImplementedException";
            if (e is NullReferenceException) return mt == System.EETypePtr.EETypePtrOf<NullReferenceException>().ToPointer() ? "System.NullReferenceException" : "a type derived from System.NullReferenceException";
            if (e is OutOfMemoryException) return mt == System.EETypePtr.EETypePtrOf<OutOfMemoryException>().ToPointer() ? "System.OutOfMemoryException" : "a type derived from System.OutOfMemoryException";
            if (e is TypeLoadException) return mt == System.EETypePtr.EETypePtrOf<TypeLoadException>().ToPointer() ? "System.TypeLoadException" : "a type derived from System.TypeLoadException";
            if (e is System.Threading.OperationCanceledException) return mt == System.EETypePtr.EETypePtrOf<System.Threading.OperationCanceledException>().ToPointer() ? "System.OperationCanceledException" : "a type derived from System.OperationCanceledException";
            if (e is System.Threading.SynchronizationLockException) return mt == System.EETypePtr.EETypePtrOf<System.Threading.SynchronizationLockException>().ToPointer() ? "System.Threading.SynchronizationLockException" : "a type derived from System.Threading.SynchronizationLockException";
            if (e is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) return mt == System.EETypePtr.EETypePtrOf<Microsoft.CSharp.RuntimeBinder.RuntimeBinderException>().ToPointer() ? "Microsoft.CSharp.RuntimeBinder.RuntimeBinderException" : "a type derived from Microsoft.CSharp.RuntimeBinder.RuntimeBinderException";
            if (e is System.Formats.Cbor.CborContentException) return mt == System.EETypePtr.EETypePtrOf<System.Formats.Cbor.CborContentException>().ToPointer() ? "System.Formats.Cbor.CborContentException" : "a type derived from System.Formats.Cbor.CborContentException";
            return mt == System.EETypePtr.EETypePtrOf<Exception>().ToPointer() ? "System.Exception" : "a type derived from System.Exception";
        }
    }
}
