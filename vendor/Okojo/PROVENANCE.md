# Okojo — vendored cut

**Upstream:** https://github.com/akeit0/okojo
**Snapshot:** commit `478ec2d34df6f880e6e8fb2439419cb82c677c19` (2026-09-21,
"chore(release): bump JavaScript packages to 0.2.0-preview.2")
**License:** MIT (see [LICENSE](LICENSE)) — © 2026 Akito Inoue.
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) is upstream's and kept as is:
the design draws on V8 (BSD-3-Clause, no code), and the atom string index
adapts parts of .NET runtime v10.0.12 (MIT).
**Purpose:** an ECMAScript engine written in C# (bytecode compiler, VM, the
standard library, `Intl`, `RegExp`): the `JS` program (`apps_native/JsApp`) —
scripts, a prompt, and a stage of an object pipeline (a message in as a JS
object, an object or text out).

## Included

Six of upstream's projects, sources only, compiled straight into the app
image (`JsApp.csproj`):

- `src/Okojo.JavaScript` — parser, compiler, VM, built-ins
- `src/Okojo.JavaScript.Embedding` — `JsRuntime` and its builder
- `src/Okojo.Numerics`, `src/Okojo.Text.Unicode`,
  `src/Okojo.Text.RegularExpressions` — numbers, Unicode tables, the
  ECMAScript regular-expression engine
- `src/Okojo.Globalization` — `Intl` data and formatting; its three data files
  (`Data/*.txt`) are embedded resources under upstream's names and read back
  through std's `Assembly.GetManifestResourceStream`

`SharpOS/` is ours, not upstream's:

- `GlobalUsings.cs` — the implicit usings the upstream projects get from the
  SDK, and aliases for `DateTimeFormat`/`CalendarData`, whose names std's
  internal BCL types share once both are compiled into one assembly.
- `OkojoShims.cs` — std has no `System.Text.RegularExpressions` yet. Okojo
  uses .NET `Regex` only for eight fixed internal patterns (`[GeneratedRegex]`);
  this layer runs them through Okojo's own ECMAScript engine and supplies the
  bodies the source generator would have written. It goes when std has `Regex`.

## Excluded

- The host layers: `Okojo.Node` (`require`, `fs`, `process`, timers),
  `Okojo.Browser`/`Okojo.WebPlatform`, `Okojo.Hosting`, `Okojo.DotNet.Modules`.
- `Okojo.Reflection` (CLR access by reflection — no metadata here) and
  `Okojo.SourceGenerator`.
- `Okojo.WebAssembly` and `Okojo.WebAssembly.Wasmtime` (a native library).
- `Okojo.Annotations` and `Okojo.Diagnostics` (not referenced by the six above).
- The debugger, REPL, CLI, documentation generator, VS Code extension, tests,
  examples and sandbox; the `*.csproj` files; `InternalsVisibleTo.cs` (not
  compiled: one assembly here).

## Local edits

All behind the `SHARPOS` define (`#if SHARPOS` / `#if !SHARPOS`) or marked
`SharpOS cut:` at the place:

- **No reflection metadata**: host interop finds members through the generated
  bindings only — `HostTypeDescriptor`, `HostNamedMemberDescriptor`,
  `IClrAccessProvider`, `JsRealm.HostInterop`/`.Clr`, `JsAgent`, and the
  runtime builder and options, which only carry assemblies for
  `Okojo.Reflection`.
- **No default or static interface members** (CS8701 on this runtime):
  `IModuleSourceLoader`, `IWorkerScriptSourceLoader` and their implementers
  (`FileModuleSourceLoader`, `SourceMapModuleSourceLoader`), `JsHostObject`,
  `JsObject`.
- **No generics over ref structs** (`allows ref struct`, which ILC 8 does not
  support): `JsGlobalInstaller.Function(… Func<CallInfo, …>)`,
  `JsArrayBufferObject`, `SeededOrdinalComparer` (no
  `IAlternateEqualityComparer`), `JsIdentifierTable` (no
  `Dictionary.AlternateLookup`).
- **No `ITimer`/`TimerCallback`**: `ITimerFactory` (nothing in the engine uses it).
- **No `System.Collections.Frozen`**: `CalendarData`, `UnitData` — the added
  file `Okojo.Globalization/SharpOS.FrozenStandIns.cs` gives the same lookups
  over `HashSet`/`Dictionary`, so the two files stay as written.
- **No non-Gregorian calendar hierarchy in std**: `LunisolarCalendar`.

Everything else Okojo needed was added to std rather than cut from Okojo
(step212: dates, `Uri`, `Task` continuations, `RandomNumberGenerator`, text
elements, normalization and more — `done/step212.md`).
