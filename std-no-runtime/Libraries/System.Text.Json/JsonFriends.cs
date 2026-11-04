// CONVERT reports where a JSON input is wrong with the reader's own line and
// byte position (JsonReaderState._lineNumber, _bytePositionInLine), which
// System.Text.Json keeps internal. It was compiled into the app until the
// library became its own assembly.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("ConvertApp")]
