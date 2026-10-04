// [Message] types into the image's pipe catalog (pipe spec Р16, Р20, Р21).
//
// For every class or struct marked SharpOS.Std.Pipes.MessageAttribute the
// generator emits a registration that runs on first pipe use: the full name,
// every instance field (inherited ones first) with its name and type, and the
// field offset measured on a witness at start-up — ILC decides the layout, and
// the generator does not try to repeat it. Arrays the fields reach are
// registered too.
//
// Build errors instead of runtime surprises:
//   SOSM001  a field is a delegate
//   SOSM002  a field is a pointer
//   SOSM003  a field's class is not in the catalog (not [Message], not a
//            string, array, object or interface)
//   SOSM004  an auto-property (its field has no name to measure)
//   SOSM005  a field the registration cannot reach (private, and the type is
//            not partial)
//   SOSM006  a generic [Message] type

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Pipes.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class MessageGenerator : IIncrementalGenerator
{
    public const string MessageAttributeName = "SharpOS.Std.Pipes.MessageAttribute";

    private static readonly DiagnosticDescriptor Delegate = new(
        "SOSM001", "Delegate field in a message", "Field '{0}' of [Message] type '{1}' is a delegate: code does not travel through pipes",
        "SharpOS.Pipes", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor Pointer = new(
        "SOSM002", "Pointer field in a message", "Field '{0}' of [Message] type '{1}' is a pointer: an address means nothing on the other side",
        "SharpOS.Pipes", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor OutsideCatalog = new(
        "SOSM003", "Field type outside the catalog", "Field '{0}' of [Message] type '{1}' has type '{2}', which is not in the catalog: mark it [Message]",
        "SharpOS.Pipes", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor AutoProperty = new(
        "SOSM004", "Auto-property in a message", "Auto-property '{0}' of [Message] type '{1}': use a field",
        "SharpOS.Pipes", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor Unreachable = new(
        "SOSM005", "Field out of the generator's reach", "Field '{0}' of [Message] type '{1}' is private: make the field internal or the type partial",
        "SharpOS.Pipes", DiagnosticSeverity.Error, true);
    private static readonly DiagnosticDescriptor Generic = new(
        "SOSM006", "Generic message type", "[Message] type '{0}' is generic: declare a closed type instead",
        "SharpOS.Pipes", DiagnosticSeverity.Error, true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<INamedTypeSymbol> types = context.SyntaxProvider.ForAttributeWithMetadataName(
            MessageAttributeName,
            static (node, _) => node is BaseTypeDeclarationSyntax,
            static (ctx, _) => (INamedTypeSymbol)ctx.TargetSymbol);

        context.RegisterSourceOutput(types.Collect(), static (ctx, symbols) => Emit(ctx, symbols));
    }

    private static void Emit(SourceProductionContext ctx, ImmutableArray<INamedTypeSymbol> symbols)
    {
        if (symbols.IsDefaultOrEmpty)
            return;

        var body = new StringBuilder();
        var calls = new StringBuilder();
        var partials = new StringBuilder();
        var arrays = new SortedDictionary<string, string>();
        int index = 0;

        foreach (INamedTypeSymbol type in symbols.Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default)
                                                 .OrderBy(t => FullName(t)))
        {
            if (type.IsGenericType)
            {
                ctx.ReportDiagnostic(Diagnostic.Create(Generic, type.Locations.FirstOrDefault(), FullName(type)));
                continue;
            }
            if (type.IsAbstract || type.TypeKind == TypeKind.Interface)
                continue;

            if (type.TypeKind == TypeKind.Enum)
            {
                string boxed = "            global::SharpOS.Std.Pipes.MessageCatalog.Register(\"" + FullName(type) + "\", (object)default("
                               + Global(type) + "), new global::SharpOS.Std.Exchange.TypeKeys.Field[] { new global::SharpOS.Std.Exchange.TypeKeys.Field(\"value\", \""
                               + TypeName(type) + "\", 8) });\n";
                string enumMethod = "Register_" + index++;
                body.Append("        private static void ").Append(enumMethod).Append("()\n        {\n").Append(boxed).Append("        }\n\n");
                calls.Append("            ").Append(enumMethod).Append("();\n");
                continue;
            }

            List<IFieldSymbol> fields = InstanceFields(type);
            bool partial = IsPartial(type);
            bool ok = true;
            foreach (IFieldSymbol f in fields)
                ok &= CheckField(ctx, type, f, partial, arrays);
            if (!ok)
                continue;

            string code = Registration(type, fields);
            if (partial && type.ContainingType == null)
            {
                partials.Append(PartialHost(type, code));
                calls.Append("            ").Append(Global(type)).Append(".__SharpOSRegisterMessage();\n");
            }
            else
            {
                string method = "Register_" + index++;
                body.Append("        private static void ").Append(method).Append("()\n        {\n")
                    .Append(code).Append("        }\n\n");
                calls.Append("            ").Append(method).Append("();\n");
            }
        }

        foreach (KeyValuePair<string, string> array in arrays)
            calls.Append("            RegisterArray(new ").Append(array.Value).Append(", \"").Append(array.Key).Append("\");\n");

        var source = new StringBuilder();
        source.Append("// <auto-generated/> by Pipes.Generator.MessageGenerator\n");
        source.Append("#pragma warning disable\n");
        source.Append("namespace SharpOS.Std.Pipes\n{\n");
        source.Append("    public static unsafe partial class MessageCatalog\n    {\n");
        source.Append("        static partial void RegisterGenerated()\n        {\n").Append(calls).Append("        }\n\n");
        source.Append(body);
        source.Append("    }\n}\n");
        source.Append(partials);
        ctx.AddSource("MessageCatalog.g.cs", SourceText.From(source.ToString(), Encoding.UTF8));
    }

    private static bool CheckField(SourceProductionContext ctx, INamedTypeSymbol owner, IFieldSymbol f, bool partial,
                                   SortedDictionary<string, string> arrays)
    {
        Location where = f.Locations.FirstOrDefault() ?? owner.Locations.FirstOrDefault();
        string fieldName = f.AssociatedSymbol?.Name ?? f.Name;

        if (f.AssociatedSymbol is IPropertySymbol)
        {
            ctx.ReportDiagnostic(Diagnostic.Create(AutoProperty, where, fieldName, FullName(owner)));
            return false;
        }
        if (f.DeclaredAccessibility == Accessibility.Private && !(partial && SymbolEqualityComparer.Default.Equals(f.ContainingType, owner)))
        {
            ctx.ReportDiagnostic(Diagnostic.Create(Unreachable, where, fieldName, FullName(owner)));
            return false;
        }
        return CheckType(ctx, owner, fieldName, f.Type, where, arrays);
    }

    private static bool CheckType(SourceProductionContext ctx, INamedTypeSymbol owner, string fieldName, ITypeSymbol t,
                                  Location where, SortedDictionary<string, string> arrays)
    {
        if (t.TypeKind == TypeKind.Pointer || t.TypeKind == TypeKind.FunctionPointer)
        {
            ctx.ReportDiagnostic(Diagnostic.Create(Pointer, where, fieldName, FullName(owner)));
            return false;
        }
        if (t.TypeKind == TypeKind.Delegate)
        {
            ctx.ReportDiagnostic(Diagnostic.Create(Delegate, where, fieldName, FullName(owner)));
            return false;
        }
        if (t is IArrayTypeSymbol array)
        {
            if (!CheckType(ctx, owner, fieldName, array.ElementType, where, arrays))
                return false;
            if (array.Rank == 1 && !IsBuiltinArray(array))
                arrays[TypeName(array.ElementType)] = Global(array.ElementType) + "[1]";
            return true;
        }
        if (t.SpecialType == SpecialType.System_IntPtr || t.SpecialType == SpecialType.System_UIntPtr)
        {
            ctx.ReportDiagnostic(Diagnostic.Create(Pointer, where, fieldName, FullName(owner)));
            return false;
        }
        if (IsPrimitive(t) || t.TypeKind == TypeKind.Enum || t.SpecialType == SpecialType.System_String
            || t.SpecialType == SpecialType.System_Object || t.TypeKind == TypeKind.Interface)
            return true;
        if (HasMessage(t))
            return true;

        ctx.ReportDiagnostic(Diagnostic.Create(OutsideCatalog, where, fieldName, FullName(owner), t.ToDisplayString()));
        return false;
    }

    private static string Registration(INamedTypeSymbol type, List<IFieldSymbol> fields)
    {
        var code = new StringBuilder();
        string global = Global(type);
        string name = FullName(type);
        if (type.TypeKind == TypeKind.Struct)
        {
            code.Append("            ").Append(global).Append(" v = default;\n");
            code.Append("            global::SharpOS.Std.Pipes.MessageCatalog.Register(\"").Append(name).Append("\", (object)v, new global::SharpOS.Std.Exchange.TypeKeys.Field[]\n            {\n");
            foreach (IFieldSymbol f in fields)
                code.Append("                new global::SharpOS.Std.Exchange.TypeKeys.Field(\"").Append(f.Name).Append("\", \"")
                    .Append(TypeName(f.Type)).Append("\", global::SharpOS.Std.Exchange.TypeKeys.StructOffset(ref v, ref global::System.Runtime.CompilerServices.Unsafe.AsRef(in v.")
                    .Append(Escape(f.Name)).Append("))),\n");
        }
        else
        {
            code.Append("            var w = global::System.Runtime.CompilerServices.Unsafe.As<").Append(global)
                .Append(">(global::SharpOS.Std.Pipes.MessageCatalog.Witness(typeof(").Append(global).Append(")));\n");
            code.Append("            global::SharpOS.Std.Pipes.MessageCatalog.Register(\"").Append(name).Append("\", w, new global::SharpOS.Std.Exchange.TypeKeys.Field[]\n            {\n");
            foreach (IFieldSymbol f in fields)
                code.Append("                global::SharpOS.Std.Pipes.MessageCatalog.Field(\"").Append(f.Name).Append("\", \"")
                    .Append(TypeName(f.Type)).Append("\", w, ref global::System.Runtime.CompilerServices.Unsafe.AsRef(in ")
                    .Append(FieldAccess(type, f)).Append(")),\n");
        }
        code.Append("            });\n");
        return code.ToString();
    }

    // A field of a base type is reached through a cast, so that a field hidden
    // by a derived one of the same name is the base's.
    private static string FieldAccess(INamedTypeSymbol type, IFieldSymbol f)
        => SymbolEqualityComparer.Default.Equals(f.ContainingType, type)
            ? "w." + Escape(f.Name)
            : "((" + Global(f.ContainingType) + ")w)." + Escape(f.Name);

    private static string PartialHost(INamedTypeSymbol type, string code)
    {
        var sb = new StringBuilder();
        string ns = type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString();
        if (ns != null) sb.Append("namespace ").Append(ns).Append("\n{\n");
        sb.Append("    partial ").Append(type.TypeKind == TypeKind.Struct ? "struct " : "class ").Append(type.Name).Append("\n    {\n");
        sb.Append("        internal static void __SharpOSRegisterMessage()\n        {\n").Append(code).Append("        }\n    }\n");
        if (ns != null) sb.Append("}\n");
        return sb.ToString();
    }

    internal static List<IFieldSymbol> InstanceFields(INamedTypeSymbol type)
    {
        var chain = new List<INamedTypeSymbol>();
        for (INamedTypeSymbol t = type; t != null && t.SpecialType != SpecialType.System_Object
                                        && t.SpecialType != SpecialType.System_ValueType; t = t.BaseType)
            chain.Insert(0, t);
        var fields = new List<IFieldSymbol>();
        foreach (INamedTypeSymbol t in chain)
            fields.AddRange(t.GetMembers().OfType<IFieldSymbol>().Where(f => !f.IsStatic && !f.IsConst));
        return fields;
    }

    internal static bool HasMessage(ITypeSymbol t)
        => t.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == MessageAttributeName);

    private static bool IsPartial(INamedTypeSymbol type)
        => type.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is TypeDeclarationSyntax d
                                                    && d.Modifiers.Any(m => m.Text == "partial"));

    private static bool IsPrimitive(ITypeSymbol t)
    {
        switch (t.SpecialType)
        {
            case SpecialType.System_Boolean:
            case SpecialType.System_Char:
            case SpecialType.System_SByte:
            case SpecialType.System_Byte:
            case SpecialType.System_Int16:
            case SpecialType.System_UInt16:
            case SpecialType.System_Int32:
            case SpecialType.System_UInt32:
            case SpecialType.System_Int64:
            case SpecialType.System_UInt64:
            case SpecialType.System_Single:
            case SpecialType.System_Double:
                return true;
            default:
                return false;
        }
    }

    private static bool IsBuiltinArray(IArrayTypeSymbol array)
        => IsPrimitive(array.ElementType) || array.ElementType.SpecialType == SpecialType.System_String
           || array.ElementType.SpecialType == SpecialType.System_Object;

    /// <summary>The name a type has in the catalog: namespace, '+' between nested types, '[]' for arrays.</summary>
    internal static string TypeName(ITypeSymbol t)
    {
        if (t is IArrayTypeSymbol a)
            return TypeName(a.ElementType) + "[" + new string(',', a.Rank - 1) + "]";
        if (t.TypeKind == TypeKind.Enum && t is INamedTypeSymbol e && e.EnumUnderlyingType != null)
            return TypeName(e.EnumUnderlyingType);
        return FullName(t);
    }

    internal static string FullName(ITypeSymbol t)
    {
        if (t.ContainingType != null)
            return FullName(t.ContainingType) + "+" + t.MetadataName;
        string ns = t.ContainingNamespace == null || t.ContainingNamespace.IsGlobalNamespace
            ? "" : t.ContainingNamespace.ToDisplayString() + ".";
        return ns + t.MetadataName;
    }

    private static string Global(ITypeSymbol t) => t.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static string Escape(string name) => SyntaxFacts.IsKeywordKind(SyntaxFacts.GetKeywordKind(name)) ? "@" + name : name;
}
