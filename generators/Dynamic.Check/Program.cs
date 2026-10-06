// The check after compilation (step 193): every CallSite<T> the compiler put
// into the assembly has a thunk the generator wrote for T. The generator works
// from its reading of the compiler's lowering rules; this reads what the
// compiler actually emitted. A miss is a build error here, not a
// RuntimeBinderException at the first call on the machine.
//
//   Dynamic.Check <assembly.dll>
//
// The thunks are listed by DynamicGenerator in CallSiteThunks.Generated (a
// constant), as metadata names; an anonymous type is "*" on both sides.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

internal static class DynamicCheck
{
    public static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            Console.Error.WriteLine("usage: Dynamic.Check <assembly.dll>");
            return 2;
        }
        using var stream = File.OpenRead(args[0]);
        using var pe = new PEReader(stream);
        MetadataReader md = pe.GetMetadataReader();
        List<string> missing = Check(md, out int sites, out int thunks);
        foreach (string m in missing)
            Console.WriteLine($"{args[0]}: error SOSD100: a dynamic call site of type CallSite<{m}> has no thunk: Dynamic.Generator missed this operation");
        if (sites > 0)
            Console.WriteLine($"Dynamic.Check: {sites} call-site delegate type(s), {thunks} thunk(s), {missing.Count} missing");
        return missing.Count == 0 ? 0 : 1;
    }

    /// <summary>The CallSite&lt;T&gt; types of the assembly with no thunk for T.</summary>
    public static List<string> Check(MetadataReader md, out int sites, out int thunks)
    {
        var generated = new HashSet<string>(Generated(md), StringComparer.Ordinal);
        thunks = generated.Count;
        var provider = new Names(md);
        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (TypeSpecificationHandle h in Enumerable.Range(1, md.GetTableRowCount(TableIndex.TypeSpec)).Select(i => MetadataTokens.TypeSpecificationHandle(i)))
        {
            BlobReader blob = md.GetBlobReader(md.GetTypeSpecification(h).Signature);
            string name;
            try { name = new SignatureDecoder<string, object?>(provider, md, null).DecodeType(ref blob); }
            catch (BadImageFormatException) { continue; }
            const string prefix = "System.Runtime.CompilerServices.CallSite`1<";
            // CallSite<!0> is std's own generic code, not a call site.
            if (name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(">", StringComparison.Ordinal) && !name.Contains('!'))
                used.Add(name.Substring(prefix.Length, name.Length - prefix.Length - 1));
        }
        sites = used.Count;
        return used.Where(u => !generated.Contains(u)).ToList();
    }

    private static IEnumerable<string> Generated(MetadataReader md)
    {
        foreach (TypeDefinitionHandle th in md.TypeDefinitions)
        {
            TypeDefinition t = md.GetTypeDefinition(th);
            if (md.GetString(t.Name) != "CallSiteThunks" || md.GetString(t.Namespace) != "SharpOS.Std.Dynamic") continue;
            foreach (FieldDefinitionHandle fh in t.GetFields())
            {
                FieldDefinition f = md.GetFieldDefinition(fh);
                if (md.GetString(f.Name) != "Generated") continue;
                ConstantHandle ch = f.GetDefaultValue();
                if (ch.IsNil) continue;
                Constant c = md.GetConstant(ch);
                BlobReader r = md.GetBlobReader(c.Value);
                string list = r.ReadUTF16(r.Length);
                return list.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            }
        }
        return Array.Empty<string>();
    }

    // Metadata names as DynTypes.MetaName writes them: Namespace.Name`n+Nested<args>,
    // primitives by their System name, an anonymous type as "*".
    private sealed class Names : ISignatureTypeProvider<string, object?>
    {
        private readonly MetadataReader _md;
        public Names(MetadataReader md) => _md = md;

        public string GetPrimitiveType(PrimitiveTypeCode code) => code switch
        {
            PrimitiveTypeCode.Boolean => "System.Boolean",
            PrimitiveTypeCode.Byte => "System.Byte",
            PrimitiveTypeCode.Char => "System.Char",
            PrimitiveTypeCode.Double => "System.Double",
            PrimitiveTypeCode.Int16 => "System.Int16",
            PrimitiveTypeCode.Int32 => "System.Int32",
            PrimitiveTypeCode.Int64 => "System.Int64",
            PrimitiveTypeCode.IntPtr => "System.IntPtr",
            PrimitiveTypeCode.Object => "System.Object",
            PrimitiveTypeCode.SByte => "System.SByte",
            PrimitiveTypeCode.Single => "System.Single",
            PrimitiveTypeCode.String => "System.String",
            PrimitiveTypeCode.TypedReference => "System.TypedReference",
            PrimitiveTypeCode.UInt16 => "System.UInt16",
            PrimitiveTypeCode.UInt32 => "System.UInt32",
            PrimitiveTypeCode.UInt64 => "System.UInt64",
            PrimitiveTypeCode.UIntPtr => "System.UIntPtr",
            PrimitiveTypeCode.Void => "System.Void",
            _ => code.ToString(),
        };

        public string GetTypeFromDefinition(MetadataReader md, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            TypeDefinition t = md.GetTypeDefinition(handle);
            string name = md.GetString(t.Name);
            if (name.StartsWith("<>f__AnonymousType", StringComparison.Ordinal)) return "*";
            TypeDefinitionHandle outer = t.GetDeclaringType();
            if (!outer.IsNil) return GetTypeFromDefinition(md, outer, rawTypeKind) + "+" + name;
            string ns = md.GetString(t.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        public string GetTypeFromReference(MetadataReader md, TypeReferenceHandle handle, byte rawTypeKind)
        {
            TypeReference t = md.GetTypeReference(handle);
            string name = md.GetString(t.Name);
            if (t.ResolutionScope.Kind == HandleKind.TypeReference)
                return GetTypeFromReference(md, (TypeReferenceHandle)t.ResolutionScope, rawTypeKind) + "+" + name;
            string ns = md.GetString(t.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        public string GetTypeFromSpecification(MetadataReader md, object? context, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            BlobReader blob = md.GetBlobReader(md.GetTypeSpecification(handle).Signature);
            return new SignatureDecoder<string, object?>(this, md, context).DecodeType(ref blob);
        }

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
            => genericType == "*" ? "*" : genericType + "<" + string.Join(",", typeArguments) + ">";

        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetGenericMethodParameter(object? context, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? context, int index) => "!" + index;
        public string GetPinnedType(string elementType) => elementType;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetFunctionPointerType(MethodSignature<string> signature) => "fnptr";
    }
}
