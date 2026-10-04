// Rules for references into regions (pipe spec Р15, В1).
//
// A region reference is Region<T>.Root and every reference-typed value read
// from it by field or index, directly or through a local that holds one. Such
// a reference is valid while its region is: the block goes back to the
// exchange heap on Dispose, to another pipe on Move, and the next tenant
// would answer through it. The runtime write barrier keeps references out
// of the region; this keeps region references out of everything else.
//
//   SOSR001  error    T of Region<T>, PipeWriter<T>, PipeReader<T> or
//                     Pipe.Create<T> is not a [Message] type
//   SOSR002  error    a region reference stored in a field, a static or an
//                     element of an array outside the region, captured by a
//                     lambda or local function, or returned
//   SOSR003  error    a region reference used after Dispose, Move, or the end
//                     of the using that owns its region (on some path)
//   SOSR004  error    a region reference passed to a parameter that keeps its
//                     argument: marked [Retains], or — for a method of this
//                     compilation — one whose body stores it
//   SOSR005  warning  a local Region<T> that on some path is neither disposed,
//                     moved, passed on, nor returned

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace SharpOS.Generators;

[DiagnosticAnalyzer(Microsoft.CodeAnalysis.LanguageNames.CSharp)]
public sealed class RegionAnalyzer : DiagnosticAnalyzer
{
    private const string Category = "SharpOS.Pipes";
    private const string RegionName = "SharpOS.Std.Pipes.Region<T>";
    private const string RetainsName = "SharpOS.Std.Pipes.RetainsAttribute";

    public static readonly DiagnosticDescriptor NotMessage = new(
        "SOSR001", "Pipe type argument is not a message",
        "'{0}' is not a [Message] type: a pipe end and a region carry only catalog types", Category, DiagnosticSeverity.Error, true);
    public static readonly DiagnosticDescriptor Escapes = new(
        "SOSR002", "Region reference escapes",
        "A region reference is {0}: it would outlive its region", Category, DiagnosticSeverity.Error, true);
    public static readonly DiagnosticDescriptor UsedAfterRelease = new(
        "SOSR003", "Region reference used after its region is gone",
        "'{0}' refers into region '{1}', which on some path is already {2}", Category, DiagnosticSeverity.Error, true);
    public static readonly DiagnosticDescriptor Retained = new(
        "SOSR004", "Region reference passed to a retaining parameter",
        "A region reference is passed to parameter '{0}' of '{1}', which keeps its argument", Category, DiagnosticSeverity.Error, true);
    public static readonly DiagnosticDescriptor NotReleased = new(
        "SOSR005", "Region not released",
        "Region '{0}' is on some path neither disposed, moved, passed on nor returned", Category, DiagnosticSeverity.Warning, true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(NotMessage, Escapes, UsedAfterRelease, Retained, NotReleased);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var retains = new RetainCache(start.Compilation);
            start.RegisterOperationAction(CheckTypeArguments,
                OperationKind.ObjectCreation, OperationKind.Invocation, OperationKind.VariableDeclarator);
            start.RegisterOperationBlockAction(block => AnalyzeBlock(block, retains));
        });
    }

    // ---- SOSR001 ----

    private static void CheckTypeArguments(OperationAnalysisContext ctx)
    {
        ITypeSymbol? type = ctx.Operation switch
        {
            IObjectCreationOperation c => c.Type,
            IVariableDeclaratorOperation d => d.Symbol.Type,
            _ => null,
        };
        if (ctx.Operation is IInvocationOperation inv)
        {
            IMethodSymbol m = inv.TargetMethod;
            if (m.ContainingType?.ToDisplayString() == "SharpOS.Std.Pipes.Pipe" && m.Name == "Create" && m.TypeArguments.Length == 1)
                Report(ctx, m.TypeArguments[0], inv.Syntax.GetLocation());
            // Only the static entry points name the type: an instance call on a
            // pipe end was already reported where the end was declared.
            type = m.IsStatic ? m.ContainingType : null;
        }
        if (type is INamedTypeSymbol named && IsPipeGeneric(named) && named.TypeArguments.Length == 1)
            Report(ctx, named.TypeArguments[0], ctx.Operation.Syntax.GetLocation());
    }

    private static void Report(OperationAnalysisContext ctx, ITypeSymbol argument, Location where)
    {
        if (argument.TypeKind == TypeKind.TypeParameter || MessageGenerator.HasMessage(argument))
            return;
        ctx.ReportDiagnostic(Diagnostic.Create(NotMessage, where, argument.ToDisplayString()));
    }

    private static bool IsPipeGeneric(INamedTypeSymbol t)
    {
        string name = t.OriginalDefinition.ToDisplayString();
        return name == RegionName || name == "SharpOS.Std.Pipes.PipeWriter<T>" || name == "SharpOS.Std.Pipes.PipeReader<T>";
    }

    internal static bool IsRegionType(ITypeSymbol? t)
        => t is INamedTypeSymbol n && n.OriginalDefinition.ToDisplayString() == RegionName;

    // ---- the method body ----

    private static void AnalyzeBlock(OperationBlockAnalysisContext ctx, RetainCache retains)
    {
        var reported = new HashSet<(string, int)>();
        void Say(DiagnosticDescriptor d, Location l, params object[] args)
        {
            if (reported.Add((d.Id, l.SourceSpan.Start)))
                ctx.ReportDiagnostic(Diagnostic.Create(d, l, args));
        }

        foreach (IOperation block in ctx.OperationBlocks)
        {
            var tracker = new Tracker();
            tracker.Learn(block);

            foreach (IOperation op in block.DescendantsAndSelf())
            {
                CheckEscape(op, tracker, Say);
                CheckRetained(op, tracker, retains, Say);
            }
            CheckUsingScopes(block, tracker, Say);

            if (block is IBlockOperation or IMethodBodyOperation or IConstructorBodyOperation)
            {
                ControlFlowGraph? cfg = null;
                try { cfg = ctx.GetControlFlowGraph(block); } catch { }
                if (cfg != null)
                    Flow(cfg, tracker, Say);
            }
        }
    }

    // Which locals hold region references, and of which region.
    private sealed class Tracker
    {
        public readonly Dictionary<ISymbol, ISymbol?> Locals = new(SymbolEqualityComparer.Default);

        public void Learn(IOperation root)
        {
            for (int pass = 0; pass < 3; pass++)
                foreach (IOperation op in root.DescendantsAndSelf())
                {
                    switch (op)
                    {
                        case IVariableDeclaratorOperation d when d.Initializer != null:
                            if (TrySource(d.Initializer.Value, out ISymbol? r)) Bind(d.Symbol, r);
                            break;
                        case ISimpleAssignmentOperation a when a.Target is ILocalReferenceOperation l:
                            if (TrySource(a.Value, out ISymbol? r2)) Bind(l.Local, r2);
                            break;
                        case IDeclarationPatternOperation p when p.Parent is IIsPatternOperation isp && p.DeclaredSymbol != null:
                            if (TrySource(isp.Value, out ISymbol? r3)) Bind(p.DeclaredSymbol, r3);
                            break;
                        case IForEachLoopOperation f when f.LoopControlVariable is IVariableDeclaratorOperation v:
                            if (TrySource(f.Collection, out ISymbol? r4)) Bind(v.Symbol, r4);
                            break;
                    }
                }
        }

        private void Bind(ISymbol local, ISymbol? region)
        {
            if (local is ILocalSymbol l && !l.Type.IsReferenceType) return;
            Locals[local] = region;
        }

        /// <summary>Whether the value is a region reference; <paramref name="region"/> is its region's symbol when known.</summary>
        public bool TrySource(IOperation? op, out ISymbol? region)
        {
            region = null;
            op = Strip(op);
            switch (op)
            {
                case IPropertyReferenceOperation p when p.Property.Name == "Root" && IsRegionType(p.Property.ContainingType):
                    region = SymbolOf(p.Instance);
                    return true;
                case IFieldReferenceOperation f when f.Type != null && f.Type.IsReferenceType && f.Instance != null:
                    return TrySource(f.Instance, out region);
                case IArrayElementReferenceOperation e when e.Type != null && e.Type.IsReferenceType:
                    return TrySource(e.ArrayReference, out region);
                case ILocalReferenceOperation l when Locals.TryGetValue(l.Local, out ISymbol? r):
                    region = r;
                    return true;
                case IParameterReferenceOperation pr when Locals.TryGetValue(pr.Parameter, out ISymbol? r5):
                    region = r5;
                    return true;
            }
            return false;
        }
    }

    private static IOperation? Strip(IOperation? op)
    {
        while (op is IConversionOperation c) op = c.Operand;
        while (op is IParenthesizedOperation p) op = p.Operand;
        return op;
    }

    private static ISymbol? SymbolOf(IOperation? op) => Strip(op) switch
    {
        ILocalReferenceOperation l => l.Local,
        IParameterReferenceOperation p => p.Parameter,
        IFieldReferenceOperation f => f.Field,
        _ => null,
    };

    private delegate void Reporter(DiagnosticDescriptor d, Location l, params object[] args);

    // ---- SOSR002 ----

    private static void CheckEscape(IOperation op, Tracker t, Reporter say)
    {
        switch (op)
        {
            case ISimpleAssignmentOperation a when t.TrySource(a.Value, out _):
            {
                IOperation target = Strip(a.Target)!;
                string? where = target switch
                {
                    IFieldReferenceOperation f when f.Field.IsStatic => "stored in a static field",
                    IFieldReferenceOperation f when !t.TrySource(f.Instance, out _) => "stored in a field outside the region",
                    IArrayElementReferenceOperation e when !t.TrySource(e.ArrayReference, out _) => "stored in an array outside the region",
                    IPropertyReferenceOperation p when p.Property.IsStatic => "stored in a static property",
                    IPropertyReferenceOperation p when !t.TrySource(p.Instance, out _) => "stored through a property outside the region",
                    IParameterReferenceOperation p when p.Parameter.RefKind is RefKind.Out or RefKind.Ref => "returned through a ref or out parameter",
                    _ => null,
                };
                if (where != null) say(Escapes, a.Syntax.GetLocation(), where);
                break;
            }
            case IReturnOperation r when r.ReturnedValue != null && t.TrySource(r.ReturnedValue, out _):
                say(Escapes, r.Syntax.GetLocation(), "returned from the method");
                break;
            case ILocalReferenceOperation l when t.Locals.ContainsKey(l.Local) && CapturedBy(l, l.Local):
                say(Escapes, l.Syntax.GetLocation(), "captured by a lambda or a local function");
                break;
            case IPropertyReferenceOperation p when p.Property.Name == "Root" && IsRegionType(p.Property.ContainingType)
                                                   && SymbolOf(p.Instance) is ISymbol region && CapturedBy(p, region):
                say(Escapes, p.Syntax.GetLocation(), "taken inside a lambda or a local function from a region outside it");
                break;
        }
    }

    // A use inside a lambda or local function of a symbol declared outside it.
    private static bool CapturedBy(IOperation use, ISymbol symbol)
    {
        SyntaxNode? declared = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        for (IOperation? o = use.Parent; o != null; o = o.Parent)
        {
            if (o is IAnonymousFunctionOperation or ILocalFunctionOperation)
                return declared == null || !o.Syntax.Span.Contains(declared.Span);
        }
        return false;
    }

    // ---- SOSR004 ----

    private static void CheckRetained(IOperation op, Tracker t, RetainCache retains, Reporter say)
    {
        if (op is not IArgumentOperation arg || arg.Parameter == null || !t.TrySource(arg.Value, out _))
            return;
        if (retains.Retains(arg.Parameter))
            say(Retained, arg.Syntax.GetLocation(), arg.Parameter.Name, arg.Parameter.ContainingSymbol.ToDisplayString());
    }

    /// <summary>Whether a parameter keeps its argument: [Retains], or a body of this compilation that stores it.</summary>
    private sealed class RetainCache
    {
        private readonly Compilation _compilation;
        private readonly Dictionary<IParameterSymbol, bool> _known = new(SymbolEqualityComparer.Default);
        private readonly object _gate = new();

        public RetainCache(Compilation compilation) => _compilation = compilation;

        public bool Retains(IParameterSymbol parameter) => Retains(parameter, 0);

        private bool Retains(IParameterSymbol parameter, int depth)
        {
            if (parameter.Type.IsValueType && parameter.Type.TypeKind != TypeKind.TypeParameter) return false;
            if (parameter.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == RetainsName)) return true;
            if (depth > 3) return false;
            IParameterSymbol original = parameter.OriginalDefinition;
            lock (_gate)
                if (_known.TryGetValue(original, out bool cached)) return cached;

            bool result = Infer(original, depth);
            lock (_gate)
                _known[original] = result;
            return result;
        }

        private bool Infer(IParameterSymbol parameter, int depth)
        {
            if (parameter.ContainingSymbol is not IMethodSymbol method) return false;
            foreach (SyntaxReference reference in method.DeclaringSyntaxReferences)
            {
                SyntaxNode node = reference.GetSyntax();
                if (!_compilation.ContainsSyntaxTree(node.SyntaxTree)) continue;
#pragma warning disable RS1030
                SemanticModel model = _compilation.GetSemanticModel(node.SyntaxTree);
#pragma warning restore RS1030
                IOperation? body = node switch
                {
                    BaseMethodDeclarationSyntax m when m.Body != null => model.GetOperation(m.Body),
                    BaseMethodDeclarationSyntax m when m.ExpressionBody != null => model.GetOperation(m.ExpressionBody.Expression),
                    AccessorDeclarationSyntax a when a.Body != null => model.GetOperation(a.Body),
                    AccessorDeclarationSyntax a when a.ExpressionBody != null => model.GetOperation(a.ExpressionBody.Expression),
                    _ => null,
                };
                if (body == null) continue;

                var holds = new HashSet<ISymbol>(SymbolEqualityComparer.Default) { parameter };
                for (int pass = 0; pass < 2; pass++)
                    foreach (IOperation op in body.DescendantsAndSelf())
                        if (op is IVariableDeclaratorOperation d && d.Initializer != null && Holds(d.Initializer.Value, holds))
                            holds.Add(d.Symbol);
                        else if (op is ISimpleAssignmentOperation la && Strip(la.Target) is ILocalReferenceOperation lr && Holds(la.Value, holds))
                            holds.Add(lr.Local);

                foreach (IOperation op in body.DescendantsAndSelf())
                {
                    switch (op)
                    {
                        case ISimpleAssignmentOperation a when Holds(a.Value, holds):
                            if (Strip(a.Target) is IFieldReferenceOperation or IArrayElementReferenceOperation or IPropertyReferenceOperation)
                                return true;
                            break;
                        case IParameterReferenceOperation or ILocalReferenceOperation when Holds(op, holds) && InsideNested(op):
                            return true;
                        case IArgumentOperation arg when arg.Parameter != null && Holds(arg.Value, holds)
                                                         && !SymbolEqualityComparer.Default.Equals(arg.Parameter.OriginalDefinition, parameter):
                            if (Retains(arg.Parameter, depth + 1)) return true;
                            break;
                    }
                }
            }
            return false;
        }

        private static bool Holds(IOperation? op, HashSet<ISymbol> holds) => Strip(op) switch
        {
            IParameterReferenceOperation p => holds.Contains(p.Parameter),
            ILocalReferenceOperation l => holds.Contains(l.Local),
            _ => false,
        };

        private static bool InsideNested(IOperation op)
        {
            for (IOperation? o = op.Parent; o != null; o = o.Parent)
                if (o is IAnonymousFunctionOperation or ILocalFunctionOperation) return true;
            return false;
        }
    }

    // ---- SOSR003 (using scopes) ----

    // The end of a using that owns a region is where every reference into it dies.
    private static void CheckUsingScopes(IOperation root, Tracker t, Reporter say)
    {
        var ends = new List<(ISymbol Region, int End)>();
        foreach (IOperation op in root.DescendantsAndSelf())
        {
            if (op is IUsingOperation u)
            {
                ISymbol? region = u.Resources switch
                {
                    IVariableDeclarationGroupOperation g => g.Declarations.SelectMany(d => d.Declarators)
                                                             .Select(d => (ISymbol)d.Symbol).FirstOrDefault(s => IsRegionType(((ILocalSymbol)s).Type)),
                    var e when IsRegionType(e.Type) => SymbolOf(e),
                    _ => null,
                };
                if (region != null) ends.Add((region, u.Syntax.Span.End));
            }
            else if (op is IUsingDeclarationOperation ud)
            {
                foreach (IVariableDeclaratorOperation d in ud.DeclarationGroup.Declarations.SelectMany(x => x.Declarators))
                    if (IsRegionType(d.Symbol.Type) && ud.Syntax.Parent is BlockSyntax block)
                        ends.Add((d.Symbol, block.Span.End));
            }
        }
        if (ends.Count == 0) return;

        foreach (IOperation op in root.DescendantsAndSelf())
        {
            if (!IsUse(op, t, out ISymbol? region, out string name) || region == null) continue;
            foreach ((ISymbol r, int end) in ends)
                if (SymbolEqualityComparer.Default.Equals(r, region) && op.Syntax.SpanStart >= end)
                    say(UsedAfterRelease, op.Syntax.GetLocation(), name, r.Name, "disposed by its using");
        }
    }

    // A region reference being read: Root of a region, or a local holding one.
    private static bool IsUse(IOperation op, Tracker t, out ISymbol? region, out string name)
    {
        region = null;
        name = "";
        switch (op)
        {
            case IPropertyReferenceOperation p when p.Property.Name == "Root" && IsRegionType(p.Property.ContainingType):
                region = SymbolOf(p.Instance);
                name = p.Syntax.ToString();
                return true;
            case ILocalReferenceOperation l when t.Locals.TryGetValue(l.Local, out ISymbol? r)
                                                 && !(l.Parent is ISimpleAssignmentOperation a && a.Target == l):
                region = r;
                name = l.Local.Name;
                return true;
        }
        return false;
    }

    // ---- SOSR003 (Dispose, Move) and SOSR005: forward flow over the graph ----

    private static void Flow(ControlFlowGraph cfg, Tracker t, Reporter say)
    {
        ImmutableArray<BasicBlock> blocks = cfg.Blocks;
        var killedIn = new HashSet<ISymbol>?[blocks.Length];
        var liveIn = new HashSet<ISymbol>?[blocks.Length];
        var declared = new Dictionary<ISymbol, Location>(SymbolEqualityComparer.Default);
        killedIn[0] = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        liveIn[0] = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var work = new Queue<int>();
        work.Enqueue(0);
        var exitLive = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        int guard = 0;

        while (work.Count > 0 && guard++ < 10_000)
        {
            int index = work.Dequeue();
            BasicBlock b = blocks[index];
            var killed = new HashSet<ISymbol>(killedIn[index]!, SymbolEqualityComparer.Default);
            var live = new HashSet<ISymbol>(liveIn[index]!, SymbolEqualityComparer.Default);

            foreach (IOperation statement in b.Operations)
                Step(statement, t, killed, live, declared, say);
            if (b.BranchValue != null)
            {
                Step(b.BranchValue, t, killed, live, declared, say);
                // `return r;` is a branch to the exit carrying r: handed to the caller.
                if (b.FallThroughSuccessor?.Semantics == ControlFlowBranchSemantics.Return
                    && RegionLocalOf(b.BranchValue) is ISymbol returned)
                    live.Remove(returned);
            }

            if (b.Kind == BasicBlockKind.Exit)
                exitLive.UnionWith(live);

            void Push(ControlFlowBranch? branch, bool whenTrue)
            {
                if (branch == null) return;
                var l = new HashSet<ISymbol>(live, SymbolEqualityComparer.Default);
                PruneNull(b, whenTrue, l);
                // Finally blocks run on the way out of a try: a Dispose there
                // (the one a using lowers to) releases on this branch too.
                foreach (ControlFlowRegion f in branch.FinallyRegions)
                    for (int i = f.FirstBlockOrdinal; i <= f.LastBlockOrdinal; i++)
                        foreach (IOperation fo in blocks[i].Operations)
                            foreach (IOperation inner in fo.DescendantsAndSelf())
                                if (inner is IInvocationOperation fi && fi.TargetMethod.Name == "Dispose"
                                    && RegionLocalOf(fi.Instance) is ISymbol disposed)
                                    l.Remove(disposed);
                if (branch.Destination == null)
                {
                    // Leaving through a throw or a finally: nothing to carry.
                    return;
                }
                int d = branch.Destination.Ordinal;
                bool changed = false;
                if (killedIn[d] == null) { killedIn[d] = new HashSet<ISymbol>(killed, SymbolEqualityComparer.Default); changed = true; }
                else changed |= Union(killedIn[d]!, killed);
                if (liveIn[d] == null) { liveIn[d] = l; changed = true; }
                else changed |= Union(liveIn[d]!, l);
                if (changed) work.Enqueue(d);
            }

            if (b.ConditionalSuccessor != null)
            {
                bool condTrue = b.ConditionKind == ControlFlowConditionKind.WhenTrue;
                Push(b.ConditionalSuccessor, condTrue);
                Push(b.FallThroughSuccessor, !condTrue);
            }
            else
            {
                Push(b.FallThroughSuccessor, true);
            }
        }

        foreach (ISymbol region in exitLive)
            if (declared.TryGetValue(region, out Location? where))
                say(NotReleased, where, region.Name);
    }

    private static bool Union(HashSet<ISymbol> into, HashSet<ISymbol> from)
    {
        int before = into.Count;
        into.UnionWith(from);
        return into.Count != before;
    }

    // On the branch where a region local is known to be null, it holds nothing to release.
    private static void PruneNull(BasicBlock b, bool branchTaken, HashSet<ISymbol> live)
    {
        IOperation? v = Strip(b.BranchValue);
        ISymbol? local = null;
        bool equalsNull = false;
        if (v is IBinaryOperation bin && (bin.OperatorKind == BinaryOperatorKind.Equals || bin.OperatorKind == BinaryOperatorKind.NotEquals))
        {
            IOperation? other = null;
            if (IsNull(bin.RightOperand)) other = bin.LeftOperand;
            else if (IsNull(bin.LeftOperand)) other = bin.RightOperand;
            local = RegionLocalOf(other);
            equalsNull = bin.OperatorKind == BinaryOperatorKind.Equals;
        }
        else if (v is IIsPatternOperation isp && isp.Pattern is IConstantPatternOperation cp && IsNull(cp.Value))
        {
            local = RegionLocalOf(isp.Value);
            equalsNull = true;
        }
        else if (v is IIsNullOperation isn)
        {
            local = RegionLocalOf(isn.Operand);
            equalsNull = true;
        }
        if (local == null) return;
        bool isNullOnThisBranch = branchTaken == equalsNull;
        if (isNullOnThisBranch) live.Remove(local);
    }

    private static ISymbol? RegionLocalOf(IOperation? op)
    {
        op = Strip(op);
        if (op is ISimpleAssignmentOperation a) op = Strip(a.Target);
        if (op is IFlowCaptureReferenceOperation) return null;
        ISymbol? s = SymbolOf(op);
        return s is ILocalSymbol l && IsRegionType(l.Type) ? s : null;
    }

    private static bool IsNull(IOperation? op) => Strip(op) is ILiteralOperation lit && lit.ConstantValue.HasValue && lit.ConstantValue.Value == null;

    private static void Step(IOperation statement, Tracker t, HashSet<ISymbol> killed, HashSet<ISymbol> live,
                             Dictionary<ISymbol, Location> declared, Reporter say)
    {
        foreach (IOperation op in statement.DescendantsAndSelf())
        {
            // Uses first: a region read here was read before anything this
            // statement does to it later.
            if (IsUse(op, t, out ISymbol? used, out string name) && used != null && killed.Contains(used))
                say(UsedAfterRelease, op.Syntax.GetLocation(), name, used.Name, "disposed or moved");

            switch (op)
            {
                case IInvocationOperation inv when inv.TargetMethod.Name == "Dispose" && SymbolOf(inv.Instance) is ISymbol r
                                                   && IsRegionType(inv.Instance!.Type):
                    if (!inv.IsImplicit) killed.Add(r);
                    live.Remove(r);
                    break;
                case IInvocationOperation inv when inv.TargetMethod.Name == "Move"
                                                   && inv.TargetMethod.ContainingType?.OriginalDefinition.ToDisplayString() == "SharpOS.Std.Pipes.PipeWriter<T>":
                    foreach (IArgumentOperation a in inv.Arguments)
                        if (SymbolOf(a.Value) is ISymbol moved && IsRegionType(a.Value.Type))
                        {
                            killed.Add(moved);
                            live.Remove(moved);
                        }
                    break;
                case IArgumentOperation a when IsRegionType(a.Value.Type) && SymbolOf(a.Value) is ISymbol passed:
                    live.Remove(passed);
                    break;
                case IReturnOperation ret when ret.ReturnedValue != null && SymbolOf(ret.ReturnedValue) is ISymbol returned:
                    live.Remove(returned);
                    break;
                case ISimpleAssignmentOperation asg:
                {
                    ISymbol? target = SymbolOf(asg.Target);
                    if (target is ILocalSymbol tl && IsRegionType(tl.Type))
                    {
                        killed.Remove(tl);
                        if (IsNull(asg.Value)) live.Remove(tl);
                        else
                        {
                            live.Add(tl);
                            if (!declared.ContainsKey(tl))
                                declared[tl] = tl.Locations.FirstOrDefault() ?? asg.Syntax.GetLocation();
                        }
                    }
                    // Handed to something else that holds it: a field, an out parameter, another local.
                    if (SymbolOf(asg.Value) is ISymbol handed && IsRegionType(asg.Value.Type) && !(target is ILocalSymbol same && SymbolEqualityComparer.Default.Equals(same, handed)))
                        live.Remove(handed);
                    break;
                }
                case IVariableDeclaratorOperation d when IsRegionType(d.Symbol.Type) && d.Initializer != null && !IsNull(d.Initializer.Value):
                    live.Add(d.Symbol);
                    killed.Remove(d.Symbol);
                    if (!declared.ContainsKey(d.Symbol))
                        declared[d.Symbol] = d.Symbol.Locations.FirstOrDefault() ?? d.Syntax.GetLocation();
                    break;
                case IAnonymousFunctionOperation or IFlowAnonymousFunctionOperation:
                    // A lambda that uses a region local takes over its release.
                    foreach (IOperation inner in op.Descendants())
                        if (inner is ILocalReferenceOperation lr && IsRegionType(lr.Local.Type))
                            live.Remove(lr.Local);
                    break;
            }
        }
    }
}
