# TuiEditor — vendored cut

**Upstream:** https://github.com/tui-cs/Editor, `src/Terminal.Gui.Editor`
(snapshot 2026-09-11, commit `b33bf48e01c80654e6b695e782b0733fbeb29204`)
**License:** MIT (see [LICENSE](LICENSE)) — © gui.cs; the highlighting and
document code is AvaloniaEdit's, from SharpDevelop's AvalonEdit (MIT, © 2014
AlphaSierraPapa for the SharpDevelop Team), headers kept in each file; that
notice on its own is [LICENSE-AvaloniaEdit.txt](LICENSE-AvaloniaEdit.txt), and
both go into the image's `\sharpos\licence\`.
**Purpose:** syntax highlighting in `EDIT` (step232): the regex engine, the
`.xshd` loader and the built-in definitions (C#, JSON, XML, JavaScript,
PowerShell, Python, Markdown and others).

The whole `src/Terminal.Gui.Editor` is here, so the snapshot diffs against
upstream; only the highlighting is compiled. The Editor view itself is written
for Terminal.Gui v2, and our apps run on v1 — `EDIT` stays a v1 `TextView`
that asks this engine for colours (`apps_native/EditApp/HighlightedTextView.cs`).

## Compiled (`TuiEditor.props`)

```
Highlighting/*.cs           the engine, rules, spans, colours, the manager
Highlighting/Xshd/*.cs      the .xshd object model and its loader
Highlighting/Resources/     Resources.cs and the .xshd files (embedded)
Document/                   IDocument, ITextSource, ITextAnchor, TextLocation,
                            DocumentChangeEventArgs, OffsetChangeMap
Extensions/ImmutableStackExtensions.cs
Utils/                      IFreezable, BusyManager, NullSafeCollection,
                            ThrowUtil, Constants, IServiceContainer
SharpOS/                    ours: below
```

## Excluded from the build

- `Highlighting/DocumentHighlighter.cs` — keeps state through `TextDocument`'s
  line tree (`ILineTracker`); that document is the v2 editor's. `EDIT` keeps
  the per-line state itself over `TextView`'s lines.
- `Highlighting/XshdRoleMap.cs` — maps colour names to v2 `VisualRole`s for v2
  themes. On v1 the `.xshd` colours are mapped to the sixteen console colours
  instead.
- `Highlighting/Xshd/SaveXshdVisitor.cs` — writes `.xshd`; needs `XmlWriter`,
  which our `System.Xml` does not have (`std-no-runtime/Xml/README.md`).
- `NamespaceDoc.cs` files — documentation stubs.
- Everything else (the v2 Editor view, folding, completion, search, rendering,
  `TextDocument`) — v2 only.

## Ours (`SharpOS/`)

- `Drawing.cs` — `Terminal.Gui.Drawing.Color` (RGB, parsed from `#RGB`,
  `#RRGGBB`, `#AARRGGBB` and CSS names) and `VisualRole`: the two v2 types the
  highlighting names.
- `ImplicitUsings.cs` — the SDK's implicit usings upstream compiles with.

## Edits in upstream files

Each is marked `SharpOS:` or `SharpOS cut:` in place.

| where | what | why |
| --- | --- | --- |
| eight files of `Highlighting/` and `Xshd/` | `using Color = Terminal.Gui.Drawing.Color;` after the namespace | v1's `Terminal.Gui.Color` (an enum) is found first in `Terminal.Gui.Editor.*` |
| `Xshd/XmlHighlightingDefinition.cs` | `c.Role = XshdRoleMap.ResolveRole (...)` cut | `XshdRoleMap` is excluded, above |
| `Document/TextLocation.cs` | `[TypeConverter]` and `TextLocationConverter` cut | no `System.ComponentModel` type converters in std |
| `Highlighting/Resources/Resources.cs` | `GetTypeInfo ().Assembly` → `Assembly` | no `TypeInfo` in std |

## The nullable setting

`TuiEditor.props` sets `<Nullable>annotations</Nullable>`: the files carry `?`
and upstream compiles with nullable on, but the setting is the whole app's, and
`enable` would turn on warnings across the app's own code.
