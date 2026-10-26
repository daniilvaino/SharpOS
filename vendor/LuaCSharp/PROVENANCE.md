# Lua-CSharp — vendored cut

**Upstream:** https://github.com/nuskey8/Lua-CSharp
**Snapshot:** commit `a97aac88c4de9abbeb412abbecdd34846e41eea7` (2026-10-25,
"Merge pull request #370 from nuskey8/codex/string-text-api")
**License:** MIT (see [LICENSE](LICENSE)) — © 2025 Yusuke Nakada
**Purpose:** a Lua 5.2 interpreter written in C#: the `LUA` program
(`apps_native/LuaApp`) — scripts, a prompt, and a stage of an object pipeline
(a message in as a Lua table, a table or text out).

## Included

`src/Lua/**/*.cs` and `src/Lua.Annotations/**/*.cs` — the interpreter, its
standard libraries and the attribute types it uses. Compiled straight into the
app image (`LuaApp.csproj`), with no project of their own.

`SharpOS/GlobalUsings.cs` is ours, not upstream's: the implicit usings the
upstream project gets from the SDK, plus two aliases for names std's
internal types share with Lua's once both are compiled into one assembly.

## Excluded

- The upstream projects (`*.csproj`), `tests/`, `sandbox/`, `docs/`, and the
  `Lua.Unity` and `Lua.SourceGenerator` packages.
- `src/Lua/InternalVisibleTo.cs` — present but not compiled (one assembly here).
- `src/Lua/Internal/ValueStringBuilder.cs` — present but not compiled: std has
  the same BCL type.

## Local edits

Each is marked `SharpOS cut:` at the place. The reasons are the target
runtime's, not the library's:

- **No default or static interface members** (CS8701 on this runtime):
  `ILuaStream`, `ILuaFileSystem`, `LuaUserData` — the default bodies moved to
  `LuaStreamDefaults` and to the implementers (`BufferedOutputStream`,
  `ConsoleStandardIO`, `LuaStream`, `MemoryStreams`, `StandardIOStream`,
  `FileHandle`, `MathematicsLibrary`).
- **No `Encoder`/`Decoder` in std's `Encoding`**: `LuaStream`, `Utf8Reader`
  and `LuaByteEncoding` encode and decode whole UTF-8 sequences instead of
  streaming through a decoder.
- **No `Regex`**: `StringHelper.ToRegex` (nothing called it).
- **No temporary-directory convention**: `FileSystem` has no
  `Path.GetTempFileName()`.
- **No `ExecutionContext`** (one context here): `ValueTaskEx`.
- **No processor-time counter per process**: `SystemOsEnvironment` answers
  `os.clock` from `Stopwatch` since the program started.
- **C# 14 first-class spans**: `Dump.cs` passes an explicit `.AsSpan()`, where
  upstream (C# 13) relied on the array overload.

Nothing else is changed; the sources are otherwise as published and diff
cleanly against the snapshot.
