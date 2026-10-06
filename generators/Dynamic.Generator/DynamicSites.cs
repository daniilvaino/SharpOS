// The dynamic operations of a compilation, as the compiler will lower them
// (Roslyn LoweredDynamicOperationFactory): for each, the delegate type of its
// call site — [CallSite, receiver?, arguments…, right-hand side?, result],
// each the expression's static type (dynamic and null as object, a static
// receiver as System.Type), the result object for a dynamic value, the target
// type of a conversion, the type of `new`, bool for IsTrue/IsFalse/IsEvent, no
// result (Action) for a call whose value is discarded — and the names, types
// and operators the binder may have to find at run time.
//
// Where the shape is not certain (an invocation's result used or discarded),
// both delegate types are recorded; the IL check after compilation is what
// proves that every call site of the image has its thunk.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace SharpOS.Generators.Dynamic;

internal sealed class Sig
{
    public readonly ITypeSymbol[] Params;   // after CallSite, normalized
    public readonly ITypeSymbol? Result;     // null: Action
    public readonly string Key;

    public Sig(ITypeSymbol[] parameters, ITypeSymbol? result)
    {
        Params = parameters;
        Result = result;
        Key = string.Join(",", parameters.Select(DynTypes.Render)) + "->" + (result == null ? "void" : DynTypes.Render(result));
    }
}

internal sealed class DynamicSites
{
    public readonly Compilation C;
    public readonly Dictionary<string, Sig> Thunks = new();
    public readonly HashSet<string> MemberNames = new();
    public readonly HashSet<string> MethodNames = new();
    public readonly List<(string Name, ImmutableArray<ITypeSymbol> TypeArguments)> GenericCalls = new();
    public readonly HashSet<ITypeSymbol> Constructed = new(SymbolEqualityComparer.Default);
    public readonly HashSet<ITypeSymbol> ConvertTargets = new(SymbolEqualityComparer.Default);
    public readonly HashSet<string> Operators = new();
    public readonly HashSet<INamedTypeSymbol> Anonymous = new(SymbolEqualityComparer.Default);
    public readonly HashSet<INamedTypeSymbol> SeenGenerics = new(SymbolEqualityComparer.Default);
    public readonly List<SyntaxTree> Walked = new();
    public bool Index, Invoke, IsTrue;

    private readonly ITypeSymbol _object, _bool, _type, _enumerable;

    public DynamicSites(Compilation c)
    {
        C = c;
        _object = c.GetSpecialType(SpecialType.System_Object);
        _bool = c.GetSpecialType(SpecialType.System_Boolean);
        _type = c.GetTypeByMetadataName("System.Type") ?? _object;
        _enumerable = c.GetSpecialType(SpecialType.System_Collections_IEnumerable);
    }

    public bool Any => Thunks.Count > 0;

    /// <summary>Std's own files are walked only when they say `dynamic`; the image's own files always.</summary>
    public static bool ShouldWalk(SyntaxTree tree, out bool saysDynamic)
    {
        saysDynamic = tree.GetRoot().DescendantNodes().OfType<IdentifierNameSyntax>().Any(n => n.Identifier.ValueText == "dynamic");
        string path = tree.FilePath.Replace('\\', '/');
        return saysDynamic || !path.Contains("/std-no-runtime/");
    }

    public void Collect()
    {
        var trees = new List<SyntaxTree>();
        bool anyDynamic = false;
        foreach (SyntaxTree tree in C.SyntaxTrees)
        {
            if (!ShouldWalk(tree, out bool says)) continue;
            anyDynamic |= says;
            trees.Add(tree);
        }
        if (!anyDynamic) return;
        foreach (SyntaxTree tree in trees)
        {
            Walked.Add(tree);
            SemanticModel model = C.GetSemanticModel(tree);
            foreach (SyntaxNode node in tree.GetRoot().DescendantNodes())
            {
                if (!IsBodyRoot(node)) continue;
                IOperation? root = model.GetOperation(node);
                if (root == null) continue;
                foreach (IOperation op in root.DescendantsAndSelf()) Visit(op);
            }
            foreach (GenericNameSyntax g in tree.GetRoot().DescendantNodes().OfType<GenericNameSyntax>())
                if (model.GetSymbolInfo(g).Symbol is INamedTypeSymbol t) SeenGenerics.Add(t);
        }
    }

    // Nodes whose operation tree holds code: duplicates are harmless (everything goes into sets).
    private static bool IsBodyRoot(SyntaxNode node)
        => node is BaseMethodDeclarationSyntax
        || node is AccessorDeclarationSyntax
        || node is GlobalStatementSyntax
        || (node is ArrowExpressionClauseSyntax && (node.Parent is PropertyDeclarationSyntax || node.Parent is IndexerDeclarationSyntax))
        || (node is EqualsValueClauseSyntax && (node.Parent is PropertyDeclarationSyntax
                                                || (node.Parent is VariableDeclaratorSyntax && node.Parent.Parent?.Parent is FieldDeclarationSyntax)));

    private ITypeSymbol T(IOperation? op) => DynTypes.Normalize(C, op?.Type);

    private ITypeSymbol Receiver(IDynamicMemberReferenceOperation m) => m.Instance == null ? _type : T(m.Instance);

    private void Add(ITypeSymbol? result, params ITypeSymbol[] parameters)
    {
        var p = parameters.Select(t => DynTypes.Normalize(C, t)).ToArray();
        var sig = new Sig(p, result == null ? null : DynTypes.Normalize(C, result));
        Thunks[sig.Key] = sig;
        foreach (ITypeSymbol t in p) Note(t);
        if (sig.Result != null) Note(sig.Result);
    }

    private void Note(ITypeSymbol t)
    {
        if (t is INamedTypeSymbol n && n.IsAnonymousType) Anonymous.Add(n);
        else if (t is INamedTypeSymbol g && g.IsGenericType) SeenGenerics.Add(g);
    }

    private static ITypeSymbol[] Cat(ITypeSymbol first, IEnumerable<ITypeSymbol> rest, ITypeSymbol? last = null)
    {
        var list = new List<ITypeSymbol> { first };
        list.AddRange(rest);
        if (last != null) list.Add(last);
        return list.ToArray();
    }

    private void Visit(IOperation op)
    {
        switch (op)
        {
            case IDynamicMemberReferenceOperation m when !(m.Parent is IDynamicInvocationOperation inv0 && inv0.Operation == m):
                {
                    ITypeSymbol r = Receiver(m);
                    MemberNames.Add(m.MemberName);
                    IOperation? parent = m.Parent;
                    if (parent is ISimpleAssignmentOperation sa && sa.Target == m)
                        Add(_object, r, T(sa.Value));
                    else if (parent is ICompoundAssignmentOperation ca && ca.Target == m)
                    {
                        Add(_object, r);
                        Add(_object, _object, T(ca.Value));
                        Add(_object, r, _object);
                        if (ca.OperatorKind == BinaryOperatorKind.Add || ca.OperatorKind == BinaryOperatorKind.Subtract)
                        {
                            Add(_bool, r);                 // IsEvent
                            Add(_object, r, T(ca.Value));  // add_X / remove_X
                        }
                    }
                    else if (parent is ICoalesceAssignmentOperation co && co.Target == m)
                    {
                        Add(_object, r);
                        Add(_object, r, T(co.Value));
                    }
                    else if (parent is IIncrementOrDecrementOperation)
                    {
                        Add(_object, r);
                        Add(_object, _object);
                        Add(_object, r, _object);
                    }
                    else
                        Add(_object, r);
                    break;
                }
            case IDynamicInvocationOperation inv:
                {
                    IEnumerable<ITypeSymbol> args = inv.Arguments.Select(a => T(a));
                    ITypeSymbol r;
                    if (inv.Operation is IDynamicMemberReferenceOperation mr)
                    {
                        r = Receiver(mr);
                        MethodNames.Add(mr.MemberName);
                        if (mr.TypeArguments.Length > 0) GenericCalls.Add((mr.MemberName, mr.TypeArguments));
                    }
                    else
                    {
                        r = T(inv.Operation);
                        Invoke = true;
                    }
                    Add(_object, Cat(r, args));
                    Add(null, Cat(r, args));
                    break;
                }
            case IDynamicIndexerAccessOperation ix:
                {
                    Index = true;
                    ITypeSymbol r = T(ix.Operation);
                    ITypeSymbol[] idx = ix.Arguments.Select(a => T(a)).ToArray();
                    IOperation? parent = ix.Parent;
                    if (parent is ISimpleAssignmentOperation sa && sa.Target == ix)
                        Add(_object, Cat(r, idx, T(sa.Value)));
                    else if (parent is ICompoundAssignmentOperation ca && ca.Target == ix)
                    {
                        Add(_object, Cat(r, idx));
                        Add(_object, _object, T(ca.Value));
                        Add(_object, Cat(r, idx, _object));
                    }
                    else if (parent is ICoalesceAssignmentOperation co && co.Target == ix)
                    {
                        Add(_object, Cat(r, idx));
                        Add(_object, Cat(r, idx, T(co.Value)));
                    }
                    else if (parent is IIncrementOrDecrementOperation)
                    {
                        Add(_object, Cat(r, idx));
                        Add(_object, _object);
                        Add(_object, Cat(r, idx, _object));
                    }
                    else
                        Add(_object, Cat(r, idx));
                    break;
                }
            case IDynamicObjectCreationOperation cr when cr.Type != null:
                Constructed.Add(cr.Type);
                Add(cr.Type, Cat(_type, cr.Arguments.Select(a => T(a))));
                break;
            case IBinaryOperation b when b.OperatorMethod == null && (DynTypes.IsDyn(b.LeftOperand.Type) || DynTypes.IsDyn(b.RightOperand.Type)):
                if (b.OperatorKind == BinaryOperatorKind.ConditionalAnd || b.OperatorKind == BinaryOperatorKind.ConditionalOr)
                {
                    if (DynTypes.IsDyn(b.LeftOperand.Type))
                    {
                        Add(_bool, _object);   // IsFalse / IsTrue on the left
                        IsTrue = true;
                        Operators.Add("op_True");
                        Operators.Add("op_False");
                    }
                    Operators.Add(b.OperatorKind == BinaryOperatorKind.ConditionalAnd ? "op_BitwiseAnd" : "op_BitwiseOr");
                }
                else
                    Operators.Add(DynTypes.OperatorName(b.OperatorKind));
                Add(_object, T(b.LeftOperand), T(b.RightOperand));
                break;
            case IUnaryOperation u when u.OperatorMethod == null && DynTypes.IsDyn(u.Operand.Type):
                if (u.OperatorKind == UnaryOperatorKind.True || u.OperatorKind == UnaryOperatorKind.False)
                {
                    Add(_bool, _object);
                    IsTrue = true;
                    Operators.Add("op_True");
                    Operators.Add("op_False");
                }
                else
                {
                    Add(_object, _object);
                    Operators.Add(DynTypes.OperatorName(u.OperatorKind));
                }
                break;
            case IIncrementOrDecrementOperation inc when DynTypes.IsDyn(inc.Target.Type):
                Add(_object, _object);
                Operators.Add(inc.Kind == OperationKind.Increment ? "op_Increment" : "op_Decrement");
                break;
            case ICompoundAssignmentOperation ca when ca.OperatorMethod == null && (DynTypes.IsDyn(ca.Target.Type) || DynTypes.IsDyn(ca.Value.Type)):
                Add(_object, T(ca.Target), T(ca.Value));
                Operators.Add(DynTypes.OperatorName(ca.OperatorKind));
                if (!DynTypes.IsDyn(ca.Target.Type))
                {
                    Add(T(ca.Target), _object);
                    ConvertTargets.Add(T(ca.Target));
                }
                break;
            case IConversionOperation conv when DynTypes.IsDyn(conv.Operand.Type) && conv.Type != null
                                                && !DynTypes.IsDyn(conv.Type) && conv.Type.SpecialType != SpecialType.System_Object:
                Add(conv.Type, _object);
                ConvertTargets.Add(DynTypes.Normalize(C, conv.Type));
                if (conv.Type.SpecialType == SpecialType.System_Boolean) IsTrue = true;
                break;
            case IForEachLoopOperation fe:
                {
                    IOperation collection = fe.Collection;
                    while (collection is IConversionOperation c && c.IsImplicit) collection = c.Operand;
                    if (!DynTypes.IsDyn(collection.Type)) break;
                    Add(_enumerable, _object);
                    ConvertTargets.Add(_enumerable);
                    if (fe.LoopControlVariable is IVariableDeclaratorOperation vd && !DynTypes.IsDyn(vd.Symbol.Type)
                        && vd.Symbol.Type.SpecialType != SpecialType.System_Object)
                    {
                        Add(vd.Symbol.Type, _object);
                        ConvertTargets.Add(DynTypes.Normalize(C, vd.Symbol.Type));
                    }
                    break;
                }
            case IAnonymousObjectCreationOperation an when an.Type is INamedTypeSymbol at:
                Anonymous.Add(at);
                break;
        }
    }
}

/// <summary>Type helpers shared by the generator and the analyzer.</summary>
internal static class DynTypes
{
    public static readonly SymbolDisplayFormat Format = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                                  | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
                                  | SymbolDisplayMiscellaneousOptions.ExpandValueTuple);

    public static bool IsDyn(ITypeSymbol? t) => t != null && t.TypeKind == TypeKind.Dynamic;

    /// <summary>dynamic (also inside) and null as object, annotations dropped: what the delegate type holds.</summary>
    public static ITypeSymbol Normalize(Compilation c, ITypeSymbol? t)
    {
        ITypeSymbol obj = c.GetSpecialType(SpecialType.System_Object);
        if (t == null || t.TypeKind == TypeKind.Dynamic) return obj;
        if (t is IArrayTypeSymbol a)
        {
            ITypeSymbol e = Normalize(c, a.ElementType);
            return SymbolEqualityComparer.Default.Equals(e, a.ElementType) ? t : c.CreateArrayTypeSymbol(e, a.Rank);
        }
        if (t is INamedTypeSymbol n && n.IsGenericType && !n.IsUnboundGenericType && !n.IsAnonymousType)
        {
            var args = n.TypeArguments.Select(x => Normalize(c, x)).ToArray();
            bool same = true;
            for (int i = 0; i < args.Length; i++)
                if (!SymbolEqualityComparer.Default.Equals(args[i], n.TypeArguments[i]) || n.TypeArgumentNullableAnnotations[i] == NullableAnnotation.Annotated)
                    same = false;
            if (!same && n.ContainingType?.IsGenericType != true) return n.ConstructedFrom.Construct(args);
        }
        return t.WithNullableAnnotation(NullableAnnotation.None);
    }

    public static string Render(ITypeSymbol t) => t.TypeKind == TypeKind.Dynamic ? "object" : t.ToDisplayString(Format);

    /// <summary>The name the IL check decodes a signature type to: namespace.Name`n+Nested&lt;args&gt;; an anonymous type is "*".</summary>
    public static string MetaName(ITypeSymbol t)
    {
        switch (t)
        {
            case IArrayTypeSymbol a:
                return MetaName(a.ElementType) + (a.Rank == 1 ? "[]" : "[" + new string(',', a.Rank - 1) + "]");
            case IPointerTypeSymbol p:
                return MetaName(p.PointedAtType) + "*";
            case INamedTypeSymbol n when n.IsAnonymousType:
                return "*";
            case INamedTypeSymbol n:
                {
                    string head = Head(n);
                    var args = new List<ITypeSymbol>();
                    AllTypeArguments(n, args);
                    return args.Count == 0 ? head : head + "<" + string.Join(",", args.Select(MetaName)) + ">";
                }
        }
        if (t.TypeKind == TypeKind.Dynamic) return "System.Object";
        return t.ToDisplayString();
    }

    private static string Head(INamedTypeSymbol n)
    {
        if (n.ContainingType != null) return Head(n.ContainingType) + "+" + n.MetadataName;
        string ns = n.ContainingNamespace == null || n.ContainingNamespace.IsGlobalNamespace ? "" : n.ContainingNamespace.ToDisplayString() + ".";
        return ns + n.MetadataName;
    }

    private static void AllTypeArguments(INamedTypeSymbol n, List<ITypeSymbol> into)
    {
        if (n.ContainingType != null) AllTypeArguments(n.ContainingType, into);
        into.AddRange(n.TypeArguments);
    }

    public static bool ContainsTypeParameter(ITypeSymbol? t)
    {
        switch (t)
        {
            case null: return false;
            case ITypeParameterSymbol: return true;
            case IArrayTypeSymbol a: return ContainsTypeParameter(a.ElementType);
            case IPointerTypeSymbol p: return ContainsTypeParameter(p.PointedAtType);
            case INamedTypeSymbol n:
                if (n.TypeArguments.Any(ContainsTypeParameter)) return true;
                return n.ContainingType != null && ContainsTypeParameter(n.ContainingType);
        }
        return false;
    }

    /// <summary>Whether generated code (in this assembly, outside any type) can name it; anonymous types cannot.</summary>
    public static bool Nameable(Compilation c, ITypeSymbol? t)
    {
        switch (t)
        {
            case null: return false;
            case IArrayTypeSymbol a: return Nameable(c, a.ElementType);
            case INamedTypeSymbol n:
                if (n.IsAnonymousType || n.TypeKind == TypeKind.Error || n.IsUnboundGenericType) return false;
                if (!c.IsSymbolAccessibleWithin(n.OriginalDefinition, c.Assembly)) return false;
                if (IsFileLocal(n)) return false;
                if (n.ContainingType != null && !Nameable(c, n.ContainingType)) return false;
                return n.TypeArguments.All(x => Nameable(c, x));
            case IDynamicTypeSymbol: return true;
        }
        return false;   // type parameters, pointers, function pointers
    }

    private static bool IsFileLocal(INamedTypeSymbol n)
        => n.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is TypeDeclarationSyntax d && d.Modifiers.Any(SyntaxKind.FileKeyword));

    /// <summary>Can it be boxed into an object and back (not a ref struct, pointer or void)?</summary>
    public static bool Boxable(Compilation c, ITypeSymbol? t)
        => t != null && !t.IsRefLikeType && t.SpecialType != SpecialType.System_Void && Nameable(c, t)
           && t.SpecialType != SpecialType.System_TypedReference;

    public static ITypeSymbol? NullableUnderlying(ITypeSymbol t)
        => t is INamedTypeSymbol n && n.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T ? n.TypeArguments[0] : null;

    public static string OperatorName(BinaryOperatorKind k)
    {
        switch (k)
        {
            case BinaryOperatorKind.Add: return "op_Addition";
            case BinaryOperatorKind.Subtract: return "op_Subtraction";
            case BinaryOperatorKind.Multiply: return "op_Multiply";
            case BinaryOperatorKind.Divide: return "op_Division";
            case BinaryOperatorKind.Remainder: return "op_Modulus";
            case BinaryOperatorKind.And: return "op_BitwiseAnd";
            case BinaryOperatorKind.Or: return "op_BitwiseOr";
            case BinaryOperatorKind.ExclusiveOr: return "op_ExclusiveOr";
            case BinaryOperatorKind.LeftShift: return "op_LeftShift";
            case BinaryOperatorKind.RightShift: return "op_RightShift";
            case BinaryOperatorKind.Equals: return "op_Equality";
            case BinaryOperatorKind.NotEquals: return "op_Inequality";
            case BinaryOperatorKind.LessThan: return "op_LessThan";
            case BinaryOperatorKind.GreaterThan: return "op_GreaterThan";
            case BinaryOperatorKind.LessThanOrEqual: return "op_LessThanOrEqual";
            case BinaryOperatorKind.GreaterThanOrEqual: return "op_GreaterThanOrEqual";
        }
        return "op_" + k;
    }

    public static string OperatorName(UnaryOperatorKind k)
    {
        switch (k)
        {
            case UnaryOperatorKind.Minus: return "op_UnaryNegation";
            case UnaryOperatorKind.Plus: return "op_UnaryPlus";
            case UnaryOperatorKind.Not: return "op_LogicalNot";
            case UnaryOperatorKind.BitwiseNegation: return "op_OnesComplement";
        }
        return "op_" + k;
    }

    /// <summary>C# syntax of a user-defined operator, by metadata name; null when it has none to call.</summary>
    public static string? OperatorSyntax(string name)
    {
        switch (name)
        {
            case "op_Addition": return "+";
            case "op_Subtraction": return "-";
            case "op_Multiply": return "*";
            case "op_Division": return "/";
            case "op_Modulus": return "%";
            case "op_BitwiseAnd": return "&";
            case "op_BitwiseOr": return "|";
            case "op_ExclusiveOr": return "^";
            case "op_LeftShift": return "<<";
            case "op_RightShift": return ">>";
            case "op_Equality": return "==";
            case "op_Inequality": return "!=";
            case "op_LessThan": return "<";
            case "op_GreaterThan": return ">";
            case "op_LessThanOrEqual": return "<=";
            case "op_GreaterThanOrEqual": return ">=";
            case "op_UnaryNegation": return "-";
            case "op_UnaryPlus": return "+";
            case "op_LogicalNot": return "!";
            case "op_OnesComplement": return "~";
        }
        return null;
    }
}
