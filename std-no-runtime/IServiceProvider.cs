// System.IServiceProvider, as CoreLib has it (dotnet/runtime, MIT):
//   src/libraries/System.Private.CoreLib/src/System/IServiceProvider.cs
// Step232: AvaloniaEdit's IDocument (vendor/TuiEditor) extends it.

namespace System
{
    public interface IServiceProvider
    {
        object? GetService(Type serviceType);
    }
}
