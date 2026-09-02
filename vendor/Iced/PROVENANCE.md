# Iced — vendored cut

**Upstream:** https://github.com/icedland/iced (C# tree, `src/csharp/Intel/Iced/`)
**License:** MIT (see [LICENSE.txt](LICENSE.txt)) — © iced project
**Purpose:** emitting x86-64 machine code from C#, which is how this project
writes assembly at all: the language rule says no `.asm` in the tree, so an
encoder library is the mechanism, not a convenience.

Used at two different times, from the same source:

- **Build time** — `bootasm/BootAsm.Generator/` (a Roslyn source generator)
  runs Iced while the OS compiles and bakes the result into `ReadOnlySpan<byte>`
  templates. Everything the early boot needs is emitted this way, because at
  that point nothing can be allocated yet.
- **Run time** — the same encoder is linked into the kernel image, so code can
  be assembled after `KernelHeap`/`GcHeap` are up (`new Assembler(64);
  a.mov(rax, rcx); a.Assemble(writer, rip);`).

## Included

The whole C# tree as published. Nothing was deleted — the cuts are made by
what is *defined*, not by what is present, so the sources stay diffable against
upstream.

## Excluded — by compilation define

`OS.csproj` and `BootAsm.Generator.csproj` build Iced with the same set:

```
ENCODER, BLOCK_ENCODER   emitter only — the decoder and formatters compile to
                         nothing, and we have no use for reading instructions
CODE_ASSEMBLER           the fluent asm.mov(...) surface
HAS_SPAN                 our std has Span<T> / ReadOnlySpan<T>
IcedNoIVT                strips InternalsVisibleTo to Iced's unit tests
NO_EVEX                  drops the EVEX (AVX-512) encoder path
```

`NO_EVEX` is the one that is about this environment rather than about size. The
EVEX path carries

```csharp
static readonly TryConvertToDisp8N tryConvertToDisp8N = ...Impl.Method;
```

— a managed delegate held in a lazily initialised static field. Both halves are
things the AOT tier could not run when Iced was brought in: managed delegates
landed later (step131), and a static reference field with an initialiser still
trips the class-constructor check (limits §1).

## Excluded — by file

- `Intel/StreamCodeWriter.cs` — needs `System.IO.Stream`, which our std does not
  ship. Excluded in `OS.csproj` rather than deleted.
- `Iced.csproj` — referenced as `None`. The sources are compiled directly into
  the kernel image (same arrangement as `vendor/PeNet/`), so the NuGet-style
  multi-target project would only confuse `dotnet restore`.

## Local edits

None. The sources are upstream as published; everything specific to this
project is expressed through the defines and the two exclusions above.

`BlockEncoder` sorts with a lambda (`Array.Sort(blocks, (a, b) => …)`), which
once needed working around and no longer does — managed delegates landed in
step131. If a note elsewhere still mentions an inline-sort patch here, that note
is stale rather than this file.
