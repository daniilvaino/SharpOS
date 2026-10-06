// Dynamic operations this image cannot serve, reported where they are written:
//
//   SOSD001  error  ref/out argument of a dynamic call: the call site would
//                   need a delegate with by-ref parameters, which no thunk has
//   SOSD002  error  a struct variable as the receiver of a dynamic call or
//                   indexer assignment: the compiler passes it by reference
//                   (same reason); copy it to a local of type object, or call
//                   the member statically
//   SOSD003  error  an argument or receiver whose static type involves a
//                   generic type parameter: the thunk's delegate type is open,
//                   and generated code cannot close it
//   SOSD004  error  a named argument in a dynamic call: not bound
//   SOSD005  error  a type in a dynamic operation that generated code cannot
//                   name (private or protected nested, file-local): make it
//                   internal
//   SOSD006  error  more than 15 arguments in a dynamic operation: no Func or
//                   Action that long
//
// At run time, not here: a generic method called without type arguments
// throws RuntimeBinderException (no type inference).

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace SharpOS.Generators.Dynamic;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DynamicAnalyzer : DiagnosticAnalyzer
{
    private const string Category = "SharpOS.Dynamic";

    public static readonly DiagnosticDescriptor ByRef = new(
        "SOSD001", "ref/out argument in a dynamic call",
        "A ref or out argument in a dynamic call is not supported: the call site would need a by-reference delegate", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor StructReceiver = new(
        "SOSD002", "Struct variable as the receiver of a dynamic operation",
        "'{0}' is a struct variable: the compiler passes it by reference to the dynamic call site, which is not supported; copy it to an object, or call the member without dynamic arguments", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor OpenType = new(
        "SOSD003", "Generic type parameter in a dynamic operation",
        "The static type '{0}' of a dynamic operation's operand involves a type parameter: no thunk can be generated for it; convert the operand to object or dynamic", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor Named = new(
        "SOSD004", "Named argument in a dynamic call",
        "Named argument '{0}' in a dynamic call is not supported", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor Unnameable = new(
        "SOSD005", "Type of a dynamic operand is not accessible to generated code",
        "The static type '{0}' of a dynamic operation's operand cannot be named outside its declaring type: make it internal or convert the operand to object", Category, DiagnosticSeverity.Error, true);

    public static readonly DiagnosticDescriptor TooLong = new(
        "SOSD006", "Too many operands in a dynamic operation",
        "A dynamic operation with more than 15 operands is not supported", Category, DiagnosticSeverity.Error, true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
        => ImmutableArray.Create(ByRef, StructReceiver, OpenType, Named, Unnameable, TooLong);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(Invocation, OperationKind.DynamicInvocation);
        context.RegisterOperationAction(Indexer, OperationKind.DynamicIndexerAccess);
        context.RegisterOperationAction(Creation, OperationKind.DynamicObjectCreation);
        context.RegisterOperationAction(Member, OperationKind.DynamicMemberReference);
        context.RegisterOperationAction(Binary, OperationKind.Binary);
        context.RegisterOperationAction(Assignment, OperationKind.SimpleAssignment, OperationKind.CompoundAssignment);
    }

    private static void Arguments(OperationAnalysisContext ctx, IOperation op, ImmutableArray<IOperation> args)
    {
        if (args.Length > 14) ctx.ReportDiagnostic(Diagnostic.Create(TooLong, op.Syntax.GetLocation()));
        for (int i = 0; i < args.Length; i++)
        {
            RefKind? refKind = null;
            string? name = null;
            switch (op)
            {
                case IDynamicInvocationOperation d: refKind = d.GetArgumentRefKind(i); name = d.GetArgumentName(i); break;
                case IDynamicIndexerAccessOperation d: refKind = d.GetArgumentRefKind(i); name = d.GetArgumentName(i); break;
                case IDynamicObjectCreationOperation d: refKind = d.GetArgumentRefKind(i); name = d.GetArgumentName(i); break;
            }
            if (refKind.HasValue && refKind.Value != RefKind.None)
                ctx.ReportDiagnostic(Diagnostic.Create(ByRef, args[i].Syntax.GetLocation()));
            if (name != null)
                ctx.ReportDiagnostic(Diagnostic.Create(Named, args[i].Syntax.GetLocation(), name));
            Operand(ctx, args[i]);
        }
    }

    private static void Operand(OperationAnalysisContext ctx, IOperation? operand)
    {
        ITypeSymbol? t = operand?.Type;
        if (operand == null || t == null || DynTypes.IsDyn(t)) return;
        if (DynTypes.ContainsTypeParameter(t))
            ctx.ReportDiagnostic(Diagnostic.Create(OpenType, operand.Syntax.GetLocation(), t.ToDisplayString()));
        else if (!(t is INamedTypeSymbol n && n.IsAnonymousType) && !DynTypes.Nameable(ctx.Compilation, DynTypes.Normalize(ctx.Compilation, t))
                 && !t.IsRefLikeType && t.TypeKind != TypeKind.Pointer)
            ctx.ReportDiagnostic(Diagnostic.Create(Unnameable, operand.Syntax.GetLocation(), t.ToDisplayString()));
    }

    // The compiler passes a struct receiver by reference when it has a home: a
    // local, a parameter, a field, an array element, `this` in a struct.
    private static void StructByRef(OperationAnalysisContext ctx, IOperation? receiver)
    {
        if (receiver?.Type == null || !receiver.Type.IsValueType || DynTypes.IsDyn(receiver.Type)) return;
        bool home = receiver is ILocalReferenceOperation || receiver is IParameterReferenceOperation
                    || (receiver is IFieldReferenceOperation f && !f.Field.IsReadOnly)
                    || receiver is IArrayElementReferenceOperation || receiver is IInstanceReferenceOperation;
        if (home) ctx.ReportDiagnostic(Diagnostic.Create(StructReceiver, receiver.Syntax.GetLocation(), receiver.Syntax.ToString()));
    }

    private static void Invocation(OperationAnalysisContext ctx)
    {
        var inv = (IDynamicInvocationOperation)ctx.Operation;
        Arguments(ctx, inv, inv.Arguments);
        if (inv.Operation is IDynamicMemberReferenceOperation mr)
        {
            StructByRef(ctx, mr.Instance);
            Operand(ctx, mr.Instance);
        }
        else Operand(ctx, inv.Operation);
    }

    private static void Indexer(OperationAnalysisContext ctx)
    {
        var ix = (IDynamicIndexerAccessOperation)ctx.Operation;
        Arguments(ctx, ix, ix.Arguments);
        Operand(ctx, ix.Operation);
        if (ix.Parent is IAssignmentOperation a && a.Target == ix) StructByRef(ctx, ix.Operation);
        if (ix.Parent is IIncrementOrDecrementOperation) StructByRef(ctx, ix.Operation);
    }

    private static void Creation(OperationAnalysisContext ctx)
    {
        var cr = (IDynamicObjectCreationOperation)ctx.Operation;
        Arguments(ctx, cr, cr.Arguments);
        if (cr.Type != null && !DynTypes.Nameable(ctx.Compilation, cr.Type))
            ctx.ReportDiagnostic(Diagnostic.Create(Unnameable, cr.Syntax.GetLocation(), cr.Type.ToDisplayString()));
    }

    private static void Member(OperationAnalysisContext ctx)
    {
        var m = (IDynamicMemberReferenceOperation)ctx.Operation;
        if (m.Parent is IDynamicInvocationOperation) return;   // reported with the call
        Operand(ctx, m.Instance);
    }

    private static void Binary(OperationAnalysisContext ctx)
    {
        var b = (IBinaryOperation)ctx.Operation;
        if (b.OperatorMethod != null || !(DynTypes.IsDyn(b.LeftOperand.Type) || DynTypes.IsDyn(b.RightOperand.Type))) return;
        Operand(ctx, b.LeftOperand);
        Operand(ctx, b.RightOperand);
    }

    private static void Assignment(OperationAnalysisContext ctx)
    {
        var a = (IAssignmentOperation)ctx.Operation;
        if (a.Target is IDynamicMemberReferenceOperation || a.Target is IDynamicIndexerAccessOperation
            || (a is ICompoundAssignmentOperation c && c.OperatorMethod == null && (DynTypes.IsDyn(a.Target.Type) || DynTypes.IsDyn(a.Value.Type))))
            Operand(ctx, a.Value);
    }
}
