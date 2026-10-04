// Roslyn incremental source generator for compile-time stub codegen.
//
// M2 (this file): execute body methods via syntax-walker + reflection
// against the Iced.Intel.Assembler instance compiled into the generator
// itself. No ALC, no temp Roslyn compilation — bodies are restricted to
// flat sequences of `a.method(args)` calls (per SPEC §3), so walking the
// syntax tree and dispatching to Iced reflectively is enough.
//
// Current walker supports ONLY zero-arg method calls (`a.ret()`). Each
// new instruction shape grows the walker incrementally: literal arg,
// register identifier, memory-operand expression, etc.
//
// Anything the walker can't handle → InvalidOperationException with
// the syntax-node text. Roslyn presents that as a build error.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Iced.Intel;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace BootAsm.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class BootAsmGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(static ctx =>
        {
            ctx.AddSource("BootAsm.Attributes.g.cs", SourceText.From(AttributeSource, Encoding.UTF8));
        });

        // Stubs: [CompileTimeAsm] partial methods.
        var stubs = context.SyntaxProvider.ForAttributeWithMetadataName(
            "BootAsm.CompileTimeAsmAttribute",
            predicate: static (node, _) => node is MethodDeclarationSyntax,
            transform: static (ctx, _) =>
            {
                var symbol = (IMethodSymbol)ctx.TargetSymbol;
                var decl = (MethodDeclarationSyntax)ctx.TargetNode;
                // Capture the declaration's parameter list verbatim so the
                // emitted impl matches the partial signature exactly (incl.
                // hole-param names like `void** handlerCalled`).
                var pars = decl.ParameterList.Parameters;
                var parTexts = new string[pars.Count];
                for (int i = 0; i < pars.Count; i++) parTexts[i] = pars[i].ToString();
                string parListText = string.Join(", ", parTexts);
                return new StubInfo(
                    ns: symbol.ContainingNamespace.IsGlobalNamespace
                        ? null
                        : symbol.ContainingNamespace.ToDisplayString(),
                    containingType: symbol.ContainingType.Name,
                    typeAccessibility: AccessibilityKeyword(symbol.ContainingType.DeclaredAccessibility),
                    methodName: symbol.Name,
                    methodAccessibility: AccessibilityKeyword(symbol.DeclaredAccessibility),
                    parameterListText: parListText);
            });

        // Bodies: [CompileTimeAsmBody(nameof(Target))] methods. Capture
        // syntax + the target-method name (string literal arg of attr).
        var bodies = context.SyntaxProvider.ForAttributeWithMetadataName(
            "BootAsm.CompileTimeAsmBodyAttribute",
            predicate: static (node, _) => node is MethodDeclarationSyntax,
            transform: static (ctx, _) =>
            {
                var method = (MethodDeclarationSyntax)ctx.TargetNode;
                var symbol = (IMethodSymbol)ctx.TargetSymbol;
                string targetName = "";
                var attrData = symbol.GetAttributes().FirstOrDefault(a =>
                    a.AttributeClass?.ToDisplayString() == "BootAsm.CompileTimeAsmBodyAttribute");
                if (attrData?.ConstructorArguments.Length > 0 &&
                    attrData.ConstructorArguments[0].Value is string s)
                    targetName = s;

                var statements = ExtractStatements(method);
                return new BodyInfo(
                    targetMethodName: targetName,
                    containingType: symbol.ContainingType.Name,
                    statementTexts: statements);
            });

        // Combine: pair each stub with its body by (containingType, methodName).
        var pairs = stubs.Combine(bodies.Collect()).Select(static (tuple, _) =>
        {
            var (stub, allBodies) = tuple;
            foreach (var b in allBodies)
            {
                if (b.ContainingType == stub.ContainingType &&
                    b.TargetMethodName == stub.MethodName)
                {
                    return new StubPlusBody(stub, b);
                }
            }
            return new StubPlusBody(stub, null);
        });

        context.RegisterSourceOutput(pairs, static (ctx, pair) =>
        {
            string source;
            if (pair.Body is BodyInfo body)
            {
                var execBody = ExecuteBody(body);
                source = BuildStubWithTemplate(pair.Stub, execBody);
            }
            else
            {
                source = BuildStubSkeleton(pair.Stub);
            }
            ctx.AddSource($"{pair.Stub.ContainingType}.{pair.Stub.MethodName}.g.cs",
                SourceText.From(source, Encoding.UTF8));
        });
    }

    // ---- Walker (M3) ----
    // Supports `a.NAME(arg, arg, ...)` where each arg is either:
    //   * a register identifier (rax/rcx/.../cr3/...) resolved via
    //     reflection on Iced.Intel.AssemblerRegisters
    //   * an integer literal (decimal or 0x-hex), late-bound per overload
    // Overload picked by parameter-count match + per-param compatibility:
    //   register arg → param type must be assignable from concrete reg type
    //   literal arg  → Convert.ChangeType to param type must succeed
    // Anything else → throw with the offending statement text.

    private const ulong SentinelBase = 0xD1F0_DEAD_0000_0000UL;
    private const ulong SentinelHighMask = 0xFFFF_FFFF_0000_0000UL;
    private const ulong SentinelHighValue = 0xD1F0_DEAD_0000_0000UL;

    // DataSlot8 sentinel — 8 raw bytes (no instruction prefix) emitted by
    // a.dq(...) inside h.DataSlotHole. Distinct high mark from MovImm64 so
    // the post-Assemble scan can disambiguate by reading the high 32 bits.
    private const ulong DataSlotSentinelBase = 0xD1F0_DA7A_0000_0000UL;
    private const ulong DataSlotHighValue    = 0xD1F0_DA7A_0000_0000UL;

    private readonly struct ExecutedBody
    {
        public ExecutedBody(byte[] bytes, List<HoleResult> holes)
        { Bytes = bytes; Holes = holes; }
        public byte[] Bytes { get; }
        public List<HoleResult> Holes { get; }
    }

    private enum HoleKind { MovImm64, JmpRel32, DataSlot8, PushImm32 }

    private readonly struct HoleResult
    {
        public HoleResult(string name, int byteOffset, HoleKind kind)
        { Name = name; ByteOffset = byteOffset; Kind = kind; }
        public string Name { get; }
        public int ByteOffset { get; }
        public HoleKind Kind { get; }
    }

    private static ExecutedBody ExecuteBody(BodyInfo body)
    {
        var asm = new Assembler(64);
        var asmType = typeof(Assembler);

        // Look up HoleCollector from the loaded generator assembly (emitted
        // into the same compilation as the bodies via PostInitializationOutput
        // — but on the generator side we use our OWN copy of the class. Since
        // the generator references Iced source same as OS, but does NOT
        // reference the consumer assembly, we need a separate generator-side
        // HoleCollector that lives in this same DLL. Defined below as a
        // private nested class.
        var holeCollector = new GenHoleCollector();
        var holeType = typeof(GenHoleCollector);

        // Local-variable scope for `var X = a.CreateLabel();` declarations.
        // Identifiers in subsequent statements that aren't a register or
        // factory field on AssemblerRegisters fall through to this dict.
        var locals = new Dictionary<string, object>();

        foreach (var stmtText in body.StatementTexts)
        {
            var trimmed = stmtText.Trim().TrimEnd(';').Trim();

            // `var IDENT = a.method(args)` — record method return value into locals.
            // Currently the only meaningful RHS is `a.CreateLabel()` (returns
            // Iced.Intel.Label), but we resolve via the same arg-binding path so
            // any reflectively-resolvable Assembler method returning a non-void
            // value works uniformly.
            if (trimmed.StartsWith("var "))
            {
                var afterVar = trimmed.Substring(4).TrimStart();
                int eq = afterVar.IndexOf('=');
                if (eq < 0)
                    throw new InvalidOperationException(
                        $"BootAsm walker: `var` declaration without `=`: \"{stmtText}\"");
                string varName = afterVar.Substring(0, eq).Trim();
                string rhs = afterVar.Substring(eq + 1).Trim();
                if (varName.Length == 0)
                    throw new InvalidOperationException(
                        $"BootAsm walker: empty var name in \"{stmtText}\"");
                var value = InvokeCallExpr(rhs, asm, asmType, holeCollector, holeType, locals);
                if (value == null)
                    throw new InvalidOperationException(
                        $"BootAsm walker: `var {varName}` RHS returned null in \"{stmtText}\"");
                locals[varName] = value;
                continue;
            }

            Type dispatchType;
            object dispatchInstance;
            int prefixLen;
            if (trimmed.StartsWith("a."))
            { dispatchType = asmType; dispatchInstance = asm; prefixLen = 2; }
            else if (trimmed.StartsWith("h."))
            { dispatchType = holeType; dispatchInstance = holeCollector; prefixLen = 2; }
            else
                throw new InvalidOperationException(
                    $"BootAsm walker: statement must start with `a.` or `h.` — got \"{stmtText}\"");

            int lparen = trimmed.IndexOf('(');
            if (lparen < 0 || !trimmed.EndsWith(")"))
                throw new InvalidOperationException(
                    $"BootAsm walker: statement must look like `<a|h>.method(args)` — got \"{stmtText}\"");

            string methodName = trimmed.Substring(prefixLen, lparen - prefixLen);
            // Strip leading `@` — used in C# source to call methods whose
            // name collides with reserved keywords (Iced has `@in`/`@out`
            // for the `in`/`out` instructions). Reflection looks up the
            // raw name `in`/`out`.
            if (methodName.StartsWith("@")) methodName = methodName.Substring(1);
            string argsBlob = trimmed.Substring(lparen + 1, trimmed.Length - lparen - 2);
            string[] argTexts = SplitTopLevel(argsBlob);

            var args = new ParsedArg[argTexts.Length];
            // For ref-args sourced from a local var, remember the var name so we
            // can write the updated value back after the method invocation
            // (Iced's Label(ref Label) mutates the struct).
            var refLocalName = new string?[argTexts.Length];
            for (int i = 0; i < argTexts.Length; i++)
            {
                var t = argTexts[i].Trim();
                if (t.Length == 0)
                    throw new InvalidOperationException(
                        $"BootAsm walker: empty arg in \"{stmtText}\"");

                // `ref IDENT` — strip the keyword and remember the source local
                // for write-back. Only locals are supported as ref sources.
                if (t.StartsWith("ref "))
                {
                    var refTarget = t.Substring(4).TrimStart();
                    if (!locals.TryGetValue(refTarget, out var refVal))
                        throw new InvalidOperationException(
                            $"BootAsm walker: `ref {refTarget}` — not a known local in \"{stmtText}\"");
                    refLocalName[i] = refTarget;
                    args[i] = ParsedArg.Register(refVal);
                    continue;
                }

                // Special identifiers for body-method parameters.
                if (t == "a") { args[i] = ParsedArg.Register(asm); continue; }
                if (t == "h") { args[i] = ParsedArg.Register(holeCollector); continue; }

                // String literal.
                if (t.Length >= 2 && t[0] == '"' && t[t.Length - 1] == '"')
                {
                    args[i] = ParsedArg.Register(t.Substring(1, t.Length - 2));
                    continue;
                }

                if (LooksLikeNumber(t)) { args[i] = ParsedArg.Literal(t); continue; }

                var value = EvaluateExpr(t, locals);
                if (value == null)
                    throw new InvalidOperationException(
                        $"BootAsm walker: failed to evaluate \"{t}\" in \"{stmtText}\".");
                args[i] = ParsedArg.Register(value);
            }

            MethodInfo? chosen = null;
            object?[]? boundArgs = null;
            foreach (var mi in dispatchType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (mi.Name != methodName) continue;
                var pars = mi.GetParameters();
                if (pars.Length != args.Length) continue;

                var bound = new object?[args.Length];
                bool ok = true;
                for (int i = 0; i < args.Length; i++)
                {
                    var pt = pars[i].ParameterType;
                    // Unwrap by-ref param types (e.g. `ref Label`) — the boxed
                    // value the walker holds is the underlying type, reflection
                    // passes ref to the boxed slot at invoke time.
                    var checkType = pt.IsByRef ? pt.GetElementType()! : pt;
                    if (args[i].IsRegister)
                    {
                        var v = args[i].Value!;
                        if (!checkType.IsInstanceOfType(v)) { ok = false; break; }
                        bound[i] = v;
                    }
                    else
                    {
                        if (!TryParseLiteralAs(args[i].LiteralText!, checkType, out var parsed))
                        { ok = false; break; }
                        bound[i] = parsed;
                    }
                }
                if (ok) { chosen = mi; boundArgs = bound; break; }
            }

            if (chosen == null)
                throw new InvalidOperationException(
                    $"BootAsm walker: no matching {dispatchType.Name}.{methodName} overload for \"{stmtText}\"");

            chosen.Invoke(dispatchInstance, boundArgs);

            // Write back any ref-args into locals (e.g. `a.Label(ref slow)`
            // updates the boxed Label struct's id/offset).
            for (int i = 0; i < refLocalName.Length; i++)
            {
                var n = refLocalName[i];
                if (n != null && boundArgs![i] != null)
                    locals[n] = boundArgs[i]!;
            }
        }

        var writer = new BufWriter();
        asm.Assemble(writer, 0);
        var bytes = writer.Bytes.ToArray();

        // Find sentinels in bytes for both hole kinds.
        // Mov_r64_imm64 pattern: REX.W (0x48..0x4F) + B8+rd (0xB8..0xBF) + 8-byte
        //   imm whose high 4 bytes encode 0xD1F0DEAD.
        // Jmp_rel32 pattern: 0xE9 + 4-byte disp where bytes[i+3]=0xAD and
        //   bytes[i+4]=0xDE; the 16-bit ordinal lives in bytes[i+1..i+2].
        // Mov is scanned first (10-byte stride) — a Jmp's 0xE9 byte cannot
        // appear inside a Mov sentinel because the Mov pattern is fixed.
        var movByOrdinal = new Dictionary<int, int>();
        var jmpByOrdinal = new Dictionary<int, int>();
        var dataByOrdinal = new Dictionary<int, int>();
        // Cover MovImm64 sentinels (must be preceded by REX.W + B8+rd).
        for (int i = 0; i <= bytes.Length - 10; i++)
        {
            byte rex = bytes[i];
            byte opc = bytes[i + 1];
            if ((rex & 0xF8) != 0x48) continue;
            if ((opc & 0xF8) != 0xB8) continue;
            int immOff = i + 2;
            ulong imm =
                  (ulong)bytes[immOff]
                | ((ulong)bytes[immOff + 1] << 8)
                | ((ulong)bytes[immOff + 2] << 16)
                | ((ulong)bytes[immOff + 3] << 24)
                | ((ulong)bytes[immOff + 4] << 32)
                | ((ulong)bytes[immOff + 5] << 40)
                | ((ulong)bytes[immOff + 6] << 48)
                | ((ulong)bytes[immOff + 7] << 56);
            if ((imm & SentinelHighMask) != SentinelHighValue) continue;
            int ordinal = (int)(imm & 0xFFFFFFFFUL);
            movByOrdinal[ordinal] = immOff;
            i += 9;
        }
        var pushByOrdinal = new Dictionary<int, int>();
        for (int i = 0; i <= bytes.Length - 5; i++)
        {
            if (bytes[i + 3] != 0xAD) continue;
            if (bytes[i + 4] != 0xDE) continue;
            int ordinal = bytes[i + 1] | (bytes[i + 2] << 8);
            if (bytes[i] == 0xE9)
            {
                jmpByOrdinal[ordinal] = i + 1; // offset of the 4-byte disp slot
                i += 4;
            }
            else if (bytes[i] == 0x68)
            {
                pushByOrdinal[ordinal] = i + 1; // offset of the 4-byte imm32 slot
                i += 4;
            }
        }
        // DataSlot8: raw 8-byte standalone sentinel (no REX/opcode prefix).
        // Scan every 8-byte window — the distinct high mark prevents false
        // matches against MovImm64's imm or any normal code/data.
        for (int i = 0; i <= bytes.Length - 8; i++)
        {
            ulong qw =
                  (ulong)bytes[i]
                | ((ulong)bytes[i + 1] << 8)
                | ((ulong)bytes[i + 2] << 16)
                | ((ulong)bytes[i + 3] << 24)
                | ((ulong)bytes[i + 4] << 32)
                | ((ulong)bytes[i + 5] << 40)
                | ((ulong)bytes[i + 6] << 48)
                | ((ulong)bytes[i + 7] << 56);
            if ((qw & SentinelHighMask) != DataSlotHighValue) continue;
            int ordinal = (int)(qw & 0xFFFFFFFFUL);
            dataByOrdinal[ordinal] = i;
            i += 7;
        }

        int totalSentinels = movByOrdinal.Count + jmpByOrdinal.Count + dataByOrdinal.Count + pushByOrdinal.Count;
        if (totalSentinels != holeCollector.Holes.Count)
            throw new InvalidOperationException(
                $"BootAsm walker: anchor 4 — found {totalSentinels} sentinels in encoded bytes " +
                $"(mov:{movByOrdinal.Count} jmp:{jmpByOrdinal.Count} data:{dataByOrdinal.Count} push:{pushByOrdinal.Count}), expected {holeCollector.Holes.Count}.");

        var holeResults = new List<HoleResult>();
        foreach (var (kind, ordinal, name) in holeCollector.Holes)
        {
            int off;
            if (kind == HoleKind.MovImm64)
            {
                if (!movByOrdinal.TryGetValue(ordinal, out off))
                    throw new InvalidOperationException(
                        $"BootAsm walker: MovHole \"{name}\" (ord {ordinal}) — sentinel not found.");
                for (int b = 0; b < 8; b++) bytes[off + b] = 0;
            }
            else if (kind == HoleKind.DataSlot8)
            {
                if (!dataByOrdinal.TryGetValue(ordinal, out off))
                    throw new InvalidOperationException(
                        $"BootAsm walker: DataSlotHole \"{name}\" (ord {ordinal}) — sentinel not found.");
                for (int b = 0; b < 8; b++) bytes[off + b] = 0;
            }
            else if (kind == HoleKind.PushImm32)
            {
                if (!pushByOrdinal.TryGetValue(ordinal, out off))
                    throw new InvalidOperationException(
                        $"BootAsm walker: PushImm32Hole \"{name}\" (ord {ordinal}) — sentinel not found.");
                for (int b = 0; b < 4; b++) bytes[off + b] = 0;
            }
            else // JmpRel32
            {
                if (!jmpByOrdinal.TryGetValue(ordinal, out off))
                    throw new InvalidOperationException(
                        $"BootAsm walker: JmpRelHole \"{name}\" (ord {ordinal}) — sentinel not found.");
                for (int b = 0; b < 4; b++) bytes[off + b] = 0;
            }
            holeResults.Add(new HoleResult(name, off, kind));
        }

        return new ExecutedBody(bytes, holeResults);
    }

    // Generator-side mirror of the consumer-facing HoleCollector. Lives in
    // the generator DLL so reflection lookup is local. Records (kind,
    // ordinal, name) per hole; the actual byte offset is resolved post-
    // Assemble via sentinel pattern scan in ExecuteBody.
    private sealed class GenHoleCollector
    {
        private readonly List<(HoleKind kind, int ordinal, string name)> _holes = new();
        public IReadOnlyList<(HoleKind kind, int ordinal, string name)> Holes => _holes;

        public void MovHole(Assembler a, AssemblerRegister64 reg, string name)
        {
            int ordinal = _holes.Count;
            ulong sentinel = SentinelBase | (ulong)ordinal;
            _holes.Add((HoleKind.MovImm64, ordinal, name));
            a.mov(reg, sentinel);
        }

        public void JmpHole(Assembler a, AssemblerRegister64 scratchReg, string name)
        {
            int ordinal = _holes.Count;
            ulong sentinel = SentinelBase | (ulong)ordinal;
            _holes.Add((HoleKind.MovImm64, ordinal, name));
            a.mov(scratchReg, sentinel);
            a.jmp(scratchReg);
        }

        // DataSlot hole — places `label` at the current encoder offset and
        // emits an 8-byte qword sentinel that the runtime patches with an
        // absolute address. Used for RIP-relative `mov rax, [rip + label]`
        // pattern where the label points at a data slot at end of stub.
        // The sentinel has distinct high mark (0xD1F0_DA7A) so the post-
        // Assemble scan distinguishes it from MovImm64 (0xD1F0_DEAD).
        public void DataSlotHole(Assembler a, ref Label label, string name)
        {
            int ordinal = _holes.Count;
            if (ordinal > 0xFFFF)
                throw new InvalidOperationException("BootAsm: DataSlotHole ordinal exceeds 16-bit limit.");
            _holes.Add((HoleKind.DataSlot8, ordinal, name));
            a.Label(ref label);
            ulong sentinel = DataSlotSentinelBase | (ulong)ordinal;
            a.dq(sentinel);
        }

        // Push-imm32 hole — emits 5 raw bytes `[68, ord_lo, ord_hi, AD, DE]`
        // (push imm32 with placeholder). Runtime patches the 4-byte imm32
        // slot with a uint value (e.g. CPU exception vector #).
        public void PushImm32Hole(Assembler a, string name)
        {
            int ordinal = _holes.Count;
            if (ordinal > 0xFFFF)
                throw new InvalidOperationException("BootAsm: PushImm32Hole ordinal exceeds 16-bit.");
            _holes.Add((HoleKind.PushImm32, ordinal, name));
            byte[] raw = new byte[]
            {
                0x68,
                (byte)(ordinal & 0xFF),
                (byte)((ordinal >> 8) & 0xFF),
                0xAD, 0xDE,
            };
            a.db(raw);
        }

        // Relative-jump hole — emits 5 raw bytes `[E9, ord_lo, ord_hi, AD, DE]`
        // via Iced's db() (declare-byte) so the bytes survive Assemble
        // verbatim. Post-Assemble scan recognizes `E9 .. .. AD DE` pattern
        // and reads the 16-bit ordinal from bytes 1-2.
        public void JmpRelHole(Assembler a, string name)
        {
            int ordinal = _holes.Count;
            if (ordinal > 0xFFFF)
                throw new InvalidOperationException("BootAsm: RelHole ordinal exceeds 16-bit limit (65535).");
            _holes.Add((HoleKind.JmpRel32, ordinal, name));
            byte[] raw = new byte[]
            {
                0xE9,
                (byte)(ordinal & 0xFF),
                (byte)((ordinal >> 8) & 0xFF),
                0xAD, 0xDE,
            };
            a.db(raw);
        }
    }

    // ---- Expression evaluator (M5: memory operands) ----
    //
    // Grammar supported:
    //   atom    := NUMBER | IDENT
    //   binop   := atom (('+' | '-') atom)*
    //   indexer := IDENT '[' binop ']'
    //   expr    := indexer | binop | atom
    //
    // Examples this handles:
    //   rax                                  -> AssemblerRegister64
    //   0x88                                 -> int (forced default — atom path)
    //   rcx + 0x88                           -> AssemblerMemoryOperand (via op_Addition)
    //   rdx - 8                              -> AssemblerMemoryOperand
    //   __qword_ptr[rsp]                     -> AssemblerMemoryOperand (via factory indexer)
    //   __qword_ptr[rcx + 0x88]              -> AssemblerMemoryOperand
    //
    // Reflection dispatches:
    //   IDENT          -> AssemblerRegisters.<field> (or AssemblerRegisters static prop)
    //   op_Addition    -> <lhsType>.op_Addition(lhs, rhs)
    //   op_Subtraction -> ditto
    //   indexer        -> typeof(factory).get_Item(arg)
    private static object? EvaluateExpr(string text, IReadOnlyDictionary<string, object>? locals = null)
    {
        text = text.Trim();
        if (text.Length == 0) return null;

        // Indexer: IDENT[INNER]. The IDENT must reach up to the first '[', and
        // the closing ']' must be the LAST character.
        int firstBracket = text.IndexOf('[');
        if (firstBracket > 0 && text[text.Length - 1] == ']')
        {
            string target = text.Substring(0, firstBracket).Trim();
            string inner = text.Substring(firstBracket + 1, text.Length - firstBracket - 2).Trim();
            var tgt = EvaluateAtom(target, locals);
            var idx = EvaluateExpr(inner, locals);
            if (tgt == null || idx == null) return null;
            var indexer = tgt.GetType().GetMethod("get_Item",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { idx.GetType() }, null);
            if (indexer == null)
                throw new InvalidOperationException(
                    $"BootAsm walker: no get_Item({idx.GetType().Name}) on {tgt.GetType().Name} for \"{text}\"");
            return indexer.Invoke(tgt, new[] { idx });
        }

        // Top-level binary op: scan for '+' or '-' outside brackets.
        int opPos = FindTopLevelBinaryOp(text);
        if (opPos > 0)
        {
            string lhsText = text.Substring(0, opPos).Trim();
            char op = text[opPos];
            string rhsText = text.Substring(opPos + 1).Trim();
            var lhs = EvaluateExpr(lhsText, locals);
            var rhs = EvaluateExpr(rhsText, locals);
            if (lhs == null || rhs == null) return null;
            string mname = op == '+' ? "op_Addition" : "op_Subtraction";
            // Operators live as static methods on the LHS type; try LHS first then RHS.
            var opMethod = lhs.GetType().GetMethod(mname,
                BindingFlags.Public | BindingFlags.Static, null, new[] { lhs.GetType(), rhs.GetType() }, null);
            if (opMethod == null)
                opMethod = rhs.GetType().GetMethod(mname,
                    BindingFlags.Public | BindingFlags.Static, null, new[] { lhs.GetType(), rhs.GetType() }, null);
            if (opMethod == null)
                throw new InvalidOperationException(
                    $"BootAsm walker: no {mname}({lhs.GetType().Name}, {rhs.GetType().Name}) for \"{text}\"");
            return opMethod.Invoke(null, new[] { lhs, rhs });
        }

        return EvaluateAtom(text, locals);
    }

    // Invoke `a.method(args)` or `h.method(args)` as a standalone expression
    // (no statement-level dispatch) and return the result. Used by `var X = …`
    // bindings. Reuses the same overload-resolution logic.
    private static object? InvokeCallExpr(
        string text, Assembler asm, Type asmType,
        GenHoleCollector holeCollector, Type holeType,
        IReadOnlyDictionary<string, object> locals)
    {
        text = text.Trim().TrimEnd(';').Trim();
        Type dispatchType;
        object dispatchInstance;
        int prefixLen;
        if (text.StartsWith("a.")) { dispatchType = asmType; dispatchInstance = asm; prefixLen = 2; }
        else if (text.StartsWith("h.")) { dispatchType = holeType; dispatchInstance = holeCollector; prefixLen = 2; }
        else
            throw new InvalidOperationException(
                $"BootAsm walker: var RHS must start with `a.` or `h.` — got \"{text}\"");

        int lparen = text.IndexOf('(');
        if (lparen < 0 || !text.EndsWith(")"))
            throw new InvalidOperationException(
                $"BootAsm walker: var RHS must look like `<a|h>.method(...)` — got \"{text}\"");

        string methodName = text.Substring(prefixLen, lparen - prefixLen);
        if (methodName.StartsWith("@")) methodName = methodName.Substring(1);
        string argsBlob = text.Substring(lparen + 1, text.Length - lparen - 2);
        string[] argTexts = SplitTopLevel(argsBlob);

        var bound = new object?[argTexts.Length];
        var argTypes = new Type[argTexts.Length];
        for (int i = 0; i < argTexts.Length; i++)
        {
            var t = argTexts[i].Trim();
            object? v;
            if (t == "a") v = asm;
            else if (t == "h") v = holeCollector;
            else if (LooksLikeNumber(t))
            {
                if (!TryParseLiteralAs(t, typeof(int), out v))
                    throw new InvalidOperationException($"BootAsm walker: bad literal in var RHS: \"{t}\"");
            }
            else v = EvaluateExpr(t, locals);
            if (v == null)
                throw new InvalidOperationException(
                    $"BootAsm walker: failed to evaluate var-RHS arg \"{t}\"");
            bound[i] = v;
            argTypes[i] = v.GetType();
        }

        foreach (var mi in dispatchType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (mi.Name != methodName) continue;
            var pars = mi.GetParameters();
            if (pars.Length < bound.Length) continue;
            // Allow trailing optional params (e.g. CreateLabel(string? name = null)).
            for (int i = bound.Length; i < pars.Length; i++)
                if (!pars[i].HasDefaultValue) { pars = null!; break; }
            if (pars == null) continue;
            bool ok = true;
            for (int i = 0; i < bound.Length; i++)
                if (!pars[i].ParameterType.IsInstanceOfType(bound[i]!)) { ok = false; break; }
            if (!ok) continue;
            var fullArgs = new object?[pars.Length];
            for (int i = 0; i < bound.Length; i++) fullArgs[i] = bound[i];
            for (int i = bound.Length; i < pars.Length; i++) fullArgs[i] = pars[i].DefaultValue;
            return mi.Invoke(dispatchInstance, fullArgs);
        }
        throw new InvalidOperationException(
            $"BootAsm walker: no matching {dispatchType.Name}.{methodName} for var RHS \"{text}\"");
    }

    private static object? EvaluateAtom(string text, IReadOnlyDictionary<string, object>? locals = null)
    {
        text = text.Trim();
        if (LooksLikeNumber(text))
        {
            // Default to int. Memory-operand arithmetic on registers accepts
            // int operands directly; if a particular operator wants long, we
            // can extend later.
            if (TryParseLiteralAs(text, typeof(int), out var v))
                return v;
            if (TryParseLiteralAs(text, typeof(long), out v))
                return v;
            if (TryParseLiteralAs(text, typeof(ulong), out v))
                return v;
            throw new InvalidOperationException($"BootAsm walker: cannot parse number atom \"{text}\"");
        }

        // Identifier — register or factory field on AssemblerRegisters,
        // then fall through to local-var scope (Labels declared with `var`).
        var regsType = typeof(AssemblerRegisters);
        var field = regsType.GetField(text, BindingFlags.Public | BindingFlags.Static);
        if (field != null) return field.GetValue(null);
        var prop = regsType.GetProperty(text, BindingFlags.Public | BindingFlags.Static);
        if (prop != null) return prop.GetValue(null);
        if (locals != null && locals.TryGetValue(text, out var local)) return local;
        throw new InvalidOperationException(
            $"BootAsm walker: unknown identifier \"{text}\" (no public static field/property on AssemblerRegisters, no local).");
    }

    private static int FindTopLevelBinaryOp(string s)
    {
        int depth = 0;
        // Don't treat a leading '-' or '+' as a binary op (it's part of the number).
        for (int i = 1; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '(' || c == '[') depth++;
            else if (c == ')' || c == ']') depth--;
            else if (depth == 0 && (c == '+' || c == '-'))
            {
                // Watch out for hex literals like `0x...` — `+/-` inside is
                // impossible there, so the check above suffices. Also skip
                // `-` if previous non-space char is also an operator (handles
                // `rcx + -8` style).
                int p = i - 1;
                while (p >= 0 && s[p] == ' ') p--;
                if (p < 0) continue;
                char prev = s[p];
                if (prev == '+' || prev == '-' || prev == '*' || prev == '/') continue;
                return i;
            }
        }
        return -1;
    }

    private readonly struct ParsedArg
    {
        private ParsedArg(bool isReg, object? value, string? literalText)
        { IsRegister = isReg; Value = value; LiteralText = literalText; }
        public bool IsRegister { get; }
        public object? Value { get; }       // resolved register instance
        public string? LiteralText { get; } // raw literal text, late-bound per overload

        public static ParsedArg Register(object v) => new(true, v, null);
        public static ParsedArg Literal(string s) => new(false, null, s);
    }

    private static bool LooksLikeNumber(string t)
    {
        int start = 0;
        if (t.Length > 0 && t[0] == '-') start = 1;
        if (start == t.Length) return false;
        if (t.Length - start >= 2 && t[start] == '0' && (t[start + 1] == 'x' || t[start + 1] == 'X'))
            return true;
        return t[start] >= '0' && t[start] <= '9';
    }

    private static bool TryParseLiteralAs(string text, Type targetType, out object? value)
    {
        value = null;
        bool isHex = text.StartsWith("0x") || text.StartsWith("0X") ||
                     text.StartsWith("-0x") || text.StartsWith("-0X");
        var style = isHex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.Integer;
        string parseText = isHex
            ? (text.StartsWith("-") ? "-" + text.Substring(3) : text.Substring(2))
            : text;
        // Hex numbers with leading minus aren't directly supported by NumberStyles.HexNumber —
        // parse as ulong then negate if needed.
        try
        {
            if (targetType == typeof(int))
            {
                if (isHex)
                {
                    if (text.StartsWith("-"))
                    {
                        if (uint.TryParse(parseText.Substring(1), System.Globalization.NumberStyles.HexNumber, null, out var u))
                        { value = -(int)u; return true; }
                    }
                    else
                    {
                        if (uint.TryParse(parseText, System.Globalization.NumberStyles.HexNumber, null, out var u))
                        { value = (int)u; return true; }
                    }
                }
                else if (int.TryParse(parseText, style, null, out var v)) { value = v; return true; }
            }
            else if (targetType == typeof(uint))
            {
                if (uint.TryParse(isHex ? parseText.TrimStart('-') : parseText, style, null, out var v))
                { value = v; return true; }
            }
            else if (targetType == typeof(long))
            {
                if (isHex)
                {
                    if (ulong.TryParse(parseText.TrimStart('-'), System.Globalization.NumberStyles.HexNumber, null, out var u))
                    { value = text.StartsWith("-") ? -(long)u : (long)u; return true; }
                }
                else if (long.TryParse(parseText, style, null, out var v)) { value = v; return true; }
            }
            else if (targetType == typeof(ulong))
            {
                if (ulong.TryParse(isHex ? parseText.TrimStart('-') : parseText, style, null, out var v))
                { value = v; return true; }
            }
            else if (targetType == typeof(sbyte))
            {
                if (TryParseLiteralAs(text, typeof(int), out var iv) && iv is int i && i >= sbyte.MinValue && i <= sbyte.MaxValue)
                { value = (sbyte)i; return true; }
            }
            else if (targetType == typeof(byte))
            {
                if (TryParseLiteralAs(text, typeof(int), out var iv) && iv is int i && i >= 0 && i <= byte.MaxValue)
                { value = (byte)i; return true; }
            }
        }
        catch { }
        return false;
    }

    // Split top-level by comma — ignore commas inside (), [], {}.
    private static string[] SplitTopLevel(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return Array.Empty<string>();
        var parts = new List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '(' || c == '[' || c == '{') depth++;
            else if (c == ')' || c == ']' || c == '}') depth--;
            else if (c == ',' && depth == 0)
            {
                parts.Add(s.Substring(start, i - start));
                start = i + 1;
            }
        }
        parts.Add(s.Substring(start));
        return parts.ToArray();
    }

    private sealed class BufWriter : CodeWriter
    {
        private readonly List<byte> _bytes = new();
        public List<byte> Bytes => _bytes;
        public override void WriteByte(byte value) => _bytes.Add(value);
    }

    // Extract statement *text* from a body method — either expression-bodied
    // (`=> a.ret()`) or block-bodied (`{ a.ret(); a.push(rbp); ... }`). We
    // capture as strings because the walker dispatches lexically (no semantic
    // resolution needed for our restricted body grammar).
    private static EquatableArray<string> ExtractStatements(MethodDeclarationSyntax method)
    {
        var list = new List<string>();
        if (method.ExpressionBody is { } eb)
        {
            list.Add(eb.Expression.ToString());
        }
        else if (method.Body is { } block)
        {
            foreach (var stmt in block.Statements)
            {
                if (stmt is ExpressionStatementSyntax es)
                    list.Add(es.Expression.ToString());
                else
                    list.Add(stmt.ToString());
            }
        }
        return new EquatableArray<string>(list.ToArray());
    }

    // ---- Source emission ----

    private static string BuildStubSkeleton(StubInfo stub)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, stub);
        sb.AppendLine("    {");
        sb.AppendLine("        // No [CompileTimeAsmBody] paired yet — M1 skeleton returns 0.");
        sb.AppendLine("        return 0;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string BuildStubWithTemplate(StubInfo stub, ExecutedBody execBody)
    {
        var sb = new StringBuilder();
        AppendHeader(sb, stub);
        sb.AppendLine("    {");
        sb.Append("        var tpl = ").Append(stub.MethodName).AppendLine("_Template;");
        sb.AppendLine("        tpl.CopyTo(new System.Span<byte>(dst, tpl.Length));");
        // M6/M6.1: patch lines for each hole. Offset is compile-time constant
        // burned into the generated source; the parameter name matches the
        // body's MovHole/JmpHole/JmpRelHole call.
        //   MovImm64: `*(ulong*)(dst + N) = (ulong)(nuint)<name>;`
        //   JmpRel32: `*(int*)(dst + N) = (int)((long)<name> - ((long)dst + N + 4));`
        //             (rel32 = target_addr - jmp_end_addr; jmp_end = dst+N+4
        //              because the 4-byte disp starts at dst+N and ends 4
        //              bytes later.)
        foreach (var hole in execBody.Holes)
        {
            if (hole.Kind == HoleKind.MovImm64 || hole.Kind == HoleKind.DataSlot8)
            {
                sb.Append("        *(ulong*)(dst + 0x")
                  .Append(hole.ByteOffset.ToString("X"))
                  .Append(") = (ulong)(nuint)")
                  .Append(hole.Name)
                  .AppendLine(";");
            }
            else if (hole.Kind == HoleKind.PushImm32)
            {
                sb.Append("        *(uint*)(dst + 0x")
                  .Append(hole.ByteOffset.ToString("X"))
                  .Append(") = (uint)")
                  .Append(hole.Name)
                  .AppendLine(";");
            }
            else // JmpRel32
            {
                sb.Append("        *(int*)(dst + 0x")
                  .Append(hole.ByteOffset.ToString("X"))
                  .Append(") = (int)((long)")
                  .Append(hole.Name)
                  .Append(" - ((long)dst + 0x")
                  .Append(hole.ByteOffset.ToString("X"))
                  .AppendLine(" + 4));");
            }
        }
        sb.AppendLine("        return tpl.Length;");
        sb.AppendLine("    }");
        sb.Append("    private static System.ReadOnlySpan<byte> ").Append(stub.MethodName).AppendLine("_Template => new byte[] {");
        sb.Append("        ");
        for (int i = 0; i < execBody.Bytes.Length; i++)
        {
            sb.Append("0x").Append(execBody.Bytes[i].ToString("X2"));
            if (i < execBody.Bytes.Length - 1) sb.Append(", ");
        }
        sb.AppendLine();
        sb.AppendLine("    };");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void AppendHeader(StringBuilder sb, StubInfo stub)
    {
        sb.AppendLine("// <auto-generated/> DO NOT EDIT — BootAsm.Generator");
        sb.AppendLine("#nullable enable");
        if (stub.Namespace is not null)
            sb.Append("namespace ").Append(stub.Namespace).AppendLine(";");
        sb.Append(stub.TypeAccessibility).Append(" static unsafe partial class ").AppendLine(stub.ContainingType);
        sb.AppendLine("{");
        sb.Append("    ").Append(stub.MethodAccessibility).Append(" static partial int ")
          .Append(stub.MethodName).Append("(").Append(stub.ParameterListText).AppendLine(")");
    }

    // ---- Data carriers ----

    private readonly struct StubInfo : IEquatable<StubInfo>
    {
        public StubInfo(string? ns, string containingType, string typeAccessibility,
                        string methodName, string methodAccessibility, string parameterListText)
        {
            Namespace = ns;
            ContainingType = containingType;
            TypeAccessibility = typeAccessibility;
            MethodName = methodName;
            MethodAccessibility = methodAccessibility;
            ParameterListText = parameterListText;
        }
        public string? Namespace { get; }
        public string ContainingType { get; }
        public string TypeAccessibility { get; }
        public string MethodName { get; }
        public string MethodAccessibility { get; }
        public string ParameterListText { get; }

        public bool Equals(StubInfo other) =>
            Namespace == other.Namespace &&
            ContainingType == other.ContainingType &&
            TypeAccessibility == other.TypeAccessibility &&
            MethodName == other.MethodName &&
            MethodAccessibility == other.MethodAccessibility &&
            ParameterListText == other.ParameterListText;
        public override bool Equals(object? obj) => obj is StubInfo s && Equals(s);
        public override int GetHashCode() => HashCombine(Namespace, ContainingType, MethodName, ParameterListText);
        private static int HashCombine(params object?[] xs)
        { int h = 17; foreach (var x in xs) h = unchecked(h * 31 + (x?.GetHashCode() ?? 0)); return h; }
    }

    private readonly struct BodyInfo
    {
        public BodyInfo(string targetMethodName, string containingType, EquatableArray<string> statementTexts)
        {
            TargetMethodName = targetMethodName;
            ContainingType = containingType;
            StatementTexts = statementTexts;
        }
        public string TargetMethodName { get; }
        public string ContainingType { get; }
        public EquatableArray<string> StatementTexts { get; }
    }

    private readonly struct StubPlusBody
    {
        public StubPlusBody(StubInfo stub, BodyInfo? body) { Stub = stub; Body = body; }
        public StubInfo Stub { get; }
        public BodyInfo? Body { get; }
    }

    // EquatableArray<T> wraps a T[] with value-equality for IncrementalGenerator
    // cache stability. Without this, every rebuild re-emits even on identical inputs.
    private readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>
    {
        private readonly T[] _items;
        public EquatableArray(T[] items) => _items = items;
        public int Count => _items?.Length ?? 0;
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items ?? Array.Empty<T>())).GetEnumerator();
        public bool Equals(EquatableArray<T> other)
        {
            if (_items is null && other._items is null) return true;
            if (_items is null || other._items is null) return false;
            if (_items.Length != other._items.Length) return false;
            for (int i = 0; i < _items.Length; i++)
                if (!EqualityComparer<T>.Default.Equals(_items[i], other._items[i])) return false;
            return true;
        }
        public override bool Equals(object? obj) => obj is EquatableArray<T> a && Equals(a);
        public override int GetHashCode()
        {
            if (_items is null) return 0;
            int h = 17;
            foreach (var i in _items) h = unchecked(h * 31 + (i?.GetHashCode() ?? 0));
            return h;
        }
    }

    private static string AccessibilityKeyword(Accessibility a) => a switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Private => "private",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        _ => "internal",
    };

    private const string AttributeSource = """
// <auto-generated/> DO NOT EDIT — BootAsm.Generator
#nullable enable
namespace BootAsm;

[System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
internal sealed class CompileTimeAsmAttribute : System.Attribute { }

[System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
internal sealed class CompileTimeAsmBodyAttribute : System.Attribute
{
    public CompileTimeAsmBodyAttribute(string targetMethod) { TargetMethod = targetMethod; }
    public string TargetMethod { get; }
}

// HoleCollector — declared so body methods of the form
//   private static void Foo_Body(Iced.Intel.Assembler a, BootAsm.HoleCollector h) { … }
// compile in the consumer (NoStdLib kernel, no ValueTuple). The methods
// are intentionally empty — bodies are NEVER executed at runtime; the
// walker inside BootAsm.Generator intercepts h.MovHole / h.JmpHole /
// h.JmpRelHole at the syntax level and dispatches to its own private
// GenHoleCollector (which actually records the holes and emits the
// placeholder bytes).
internal sealed class HoleCollector
{
    // Forced imm64 hole — emits `mov reg, sentinel_imm64`. Patched at
    // runtime with an absolute address (managed callback / static field).
    public void MovHole(Iced.Intel.Assembler a, Iced.Intel.AssemblerRegister64 reg, string name) { }
    // Terminal absolute-address jump — emits `mov scratchReg, sentinel;
    // jmp scratchReg`. Patched at runtime with an absolute address.
    public void JmpHole(Iced.Intel.Assembler a, Iced.Intel.AssemblerRegister64 scratchReg, string name) { }
    // Relative jump hole — emits `JMP rel32` with placeholder displacement.
    // Patched at runtime with displacement = (target - (jmp_end_addr)).
    public void JmpRelHole(Iced.Intel.Assembler a, string name) { }
    // Push-imm32 hole — emits `push imm32` with placeholder. Runtime
    // patches the 4-byte imm slot with a uint value.
    public void PushImm32Hole(Iced.Intel.Assembler a, string name) { }
    // Data-slot hole — places `label` here and emits an 8-byte qword
    // that the runtime patches with an absolute address. Used together
    // with `mov rax, __qword_ptr[label]` for RIP-relative loads of
    // pointer values (resolver/fail handler/dispatcher tables).
    public void DataSlotHole(Iced.Intel.Assembler a, ref Iced.Intel.Label label, string name) { }
}
""";
}
