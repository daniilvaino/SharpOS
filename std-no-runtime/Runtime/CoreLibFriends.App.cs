// Who sees CoreLib's internals besides CoreLib.
//
// The SDK library (apps_native/sdk/SharpOS.AppSdk.csproj) is built over
// CoreLib and uses its internals — the GC, the service table — as stock
// runtime libraries use theirs.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("SharpOS.AppSdk")]

// The BCL ports built as libraries over CoreLib (std-no-runtime/Libraries):
// stock builds each with its own copy of the shared helpers (ValueStringBuilder,
// SR, Obsoletions…); here they share CoreLib's.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("System.Text.RegularExpressions")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("System.Text.Json")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("System.Private.Xml")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("System.Text.Encoding.CodePages")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("SharpCompress")]

// The white-box batteries: AotTests drives the collector, the exchange heap
// and the exception machinery directly; DataTest the pipe end's closing.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("AotTests")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DataTest")]

// The system programs that work with pipes below the typed API — raw blocks,
// the transport, a view's shapes and addresses: the shell and the utilities,
// the bridges of KQL, CONVERT and the script engines between messages and
// their own values, the pipe tools. Until the pipes have a public low-level
// API (donext), they see the internals.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Shell")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("UtilsApp")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("KqlApp")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ConvertApp")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("JsApp")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("LuaApp")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("PipePerf")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("PipeSpy")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Synth")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("WriteApp")]
