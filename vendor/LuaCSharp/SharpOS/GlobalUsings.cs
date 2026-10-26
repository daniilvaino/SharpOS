// What <ImplicitUsings>enable</ImplicitUsings> gives the upstream project,
// less System.Net.Http.
global using System;
global using System.Collections.Generic;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Threading.Tasks;

// std compiles into this assembly, so its internal types meet Lua's by name.
// Lua.Internal.HexConverter vs std's internal System.HexConverter: Lua means
// its own (std code sits in namespace System and finds System's first).
global using HexConverter = Lua.Internal.HexConverter;
// sdk AppGC.cs means SharpOS.Std.NoRuntime.GC (its s_collectHook), and the
// global `using System` above makes that ambiguous with System.GC. Lua calls
// only GC.Collect(), which System.GC forwards to this one anyway.
global using GC = SharpOS.Std.NoRuntime.GC;
