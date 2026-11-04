// What every library over CoreLib says about itself (Library.props).
//
// Not CLS-compliant, as CoreLib is not (std-no-runtime/Runtime/RuntimeAttributes.cs):
// over a CoreLib without the attribute even `int` is not a CLS type, and the
// ports keep upstream's [CLSCompliant] markings on their own types.
[assembly: System.CLSCompliant(false)]
