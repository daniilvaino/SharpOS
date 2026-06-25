// step 121 — MSBuild Task that scans consumer .cs files for
// [CoffDataSymbol] static fields and emits per-class COFF .obj artifacts.
//
// Pipeline (per consumer build invocation):
//   1. Parse all C# Compile items as Roslyn syntax trees.
//   2. Build a compilation so SemanticModel can resolve attribute symbol
//      identity and constant initializer values.
//   3. For each class with [CoffDataSymbol] static fields, validate +
//      collect entries, compute byte payload from initializer.
//   4. Group entries per (containing class, section) and emit ONE .obj
//      per class — keeps link cmdline less noisy.
//   5. Output: ITaskItem[] list of emitted .obj paths, consumed by
//      caller Target to add to @(NativeLibrary).
//
// Validation rules:
//   - Field must be `public static`.
//   - Type ∈ {byte, sbyte, short, ushort, int, uint, long, ulong, float,
//     double}.
//   - Initializer must be a compile-time constant.
//   - Section ∈ {".data", ".rdata", ".bss"}; .bss requires zero init.
//   - Alignment must be power of 2 in 1..8192.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace BootAsm
{
    public sealed class EmitCoffStubsTask : Task
    {
        [Required] public ITaskItem[] SourceFiles { get; set; }
        [Required] public string OutputDirectory { get; set; }

        [Output] public ITaskItem[] EmittedObjFiles { get; set; }

        public override bool Execute()
        {
            try
            {
                Directory.CreateDirectory(OutputDirectory);

                // ── Parse all sources ────────────────────────────────────
                var trees = new List<SyntaxTree>(SourceFiles.Length);
                foreach (var item in SourceFiles)
                {
                    string path = item.ItemSpec;
                    if (!File.Exists(path)) continue;
                    string text = File.ReadAllText(path);
                    trees.Add(CSharpSyntaxTree.ParseText(text, path: path));
                }

                // Minimal compilation — only enough for SemanticModel to
                // resolve attribute identity + evaluate constants. References
                // to mscorlib's primitive types are needed for SemanticModel
                // to type-check field initializers.
                var refs = new[]
                {
                    MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                };
                var compilation = CSharpCompilation.Create(
                    "CoffStubScan",
                    syntaxTrees: trees,
                    references: refs,
                    options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                        nullableContextOptions: NullableContextOptions.Disable));

                // ── Find [CoffDataSymbol] usages, group per class ───────
                var perClass = new Dictionary<string, List<EntryDescriptor>>();
                foreach (var tree in trees)
                {
                    var model = compilation.GetSemanticModel(tree);
                    var root = tree.GetCompilationUnitRoot();
                    foreach (var field in root.DescendantNodes().OfType<FieldDeclarationSyntax>())
                    {
                        foreach (var entry in CollectEntries(field, model))
                        {
                            string key = entry.ContainingTypeName;
                            if (!perClass.TryGetValue(key, out var list))
                                perClass[key] = list = new List<EntryDescriptor>();
                            list.Add(entry);
                        }
                    }
                }

                // ── Emit one .obj per class ─────────────────────────────
                var emitted = new List<ITaskItem>();
                foreach (var kv in perClass)
                {
                    string typeName = kv.Key;
                    var entries = kv.Value;

                    // All entries within a class must share the same
                    // section + writability + alignment chunk semantics for
                    // simplicity (one section per .obj). Group by section,
                    // emit one .obj per (class, section) pair.
                    var bySection = entries.GroupBy(e => e.Section);
                    foreach (var grp in bySection)
                    {
                        string section = grp.Key;
                        bool writable, initialized;
                        SectionKind(section, out writable, out initialized);

                        var coffEntries = grp.Select(e => new CoffStub.CoffDataEntry
                        {
                            SymbolName = e.SymbolName,
                            Payload    = e.Payload,
                            Alignment  = e.Alignment,
                        }).ToList();

                        int sectionAlign = grp.Max(e => e.Alignment);
                        byte[] bytes = CoffStub.CoffWriter.BuildObject(
                            section, writable, initialized, sectionAlign, coffEntries);

                        string fileName = $"{typeName}{SuffixForSection(section)}.obj";
                        string outPath = Path.Combine(OutputDirectory, fileName);
                        File.WriteAllBytes(outPath, bytes);

                        Log.LogMessage(MessageImportance.High,
                            $"CoffStub: emitted {fileName} ({bytes.Length}B, {coffEntries.Count} symbol(s), section={section}) -> {outPath}");

                        emitted.Add(new TaskItem(outPath));
                    }
                }

                EmittedObjFiles = emitted.ToArray();
                return true;
            }
            catch (Exception ex)
            {
                Log.LogErrorFromException(ex, true);
                return false;
            }
        }

        // ── Helpers ─────────────────────────────────────────────────────

        private sealed class EntryDescriptor
        {
            public string ContainingTypeName;
            public string SymbolName;
            public string Section;
            public int Alignment;
            public byte[] Payload;
        }

        private IEnumerable<EntryDescriptor> CollectEntries(FieldDeclarationSyntax field, SemanticModel model)
        {
            // Find [CoffDataSymbol(...)] on this field.
            AttributeSyntax attr = null;
            foreach (var list in field.AttributeLists)
            {
                foreach (var a in list.Attributes)
                {
                    var attrName = a.Name.ToString();
                    if (attrName == "CoffDataSymbol" || attrName == "CoffDataSymbolAttribute" ||
                        attrName.EndsWith(".CoffDataSymbol") || attrName.EndsWith(".CoffDataSymbolAttribute"))
                    {
                        attr = a;
                        break;
                    }
                }
                if (attr != null) break;
            }
            if (attr == null) yield break;

            // Field must be public static.
            bool isStatic = field.Modifiers.Any(m => m.Text == "static");
            bool isPublic = field.Modifiers.Any(m => m.Text == "public");
            if (!isStatic) throw new InvalidOperationException(
                $"[CoffDataSymbol] requires `static` field: {field}");
            if (!isPublic) throw new InvalidOperationException(
                $"[CoffDataSymbol] requires `public` field: {field}");

            // Parse attribute args: Name (positional), Section/Alignment (named).
            string name = null;
            string section = ".data";
            int alignment = 8;
            if (attr.ArgumentList != null)
            {
                int posIdx = 0;
                foreach (var arg in attr.ArgumentList.Arguments)
                {
                    if (arg.NameEquals != null)
                    {
                        var argName = arg.NameEquals.Name.Identifier.Text;
                        var val = model.GetConstantValue(arg.Expression);
                        if (!val.HasValue)
                            throw new InvalidOperationException(
                                $"[CoffDataSymbol] {argName}: not a constant");
                        switch (argName)
                        {
                            case "Section":   section   = (string)val.Value; break;
                            case "Alignment": alignment = (int)val.Value;    break;
                        }
                    }
                    else
                    {
                        var val = model.GetConstantValue(arg.Expression);
                        if (!val.HasValue)
                            throw new InvalidOperationException(
                                $"[CoffDataSymbol] arg #{posIdx}: not a constant");
                        if (posIdx == 0) name = (string)val.Value;
                        posIdx++;
                    }
                }
            }
            if (string.IsNullOrEmpty(name))
                throw new InvalidOperationException("[CoffDataSymbol] requires non-empty Name");
            ValidateSection(section);
            ValidateAlignment(alignment);

            string containing = ContainingTypeName(field);

            // Each declarator inside the FieldDeclaration is its own field.
            // For simplicity require single declarator per [CoffDataSymbol].
            if (field.Declaration.Variables.Count != 1)
                throw new InvalidOperationException(
                    "[CoffDataSymbol] requires a single declarator per field");

            var v = field.Declaration.Variables[0];
            if (v.Initializer == null)
            {
                if (section != ".bss")
                    throw new InvalidOperationException(
                        $"[CoffDataSymbol] field '{v.Identifier.Text}' lacks initializer (allowed only for .bss)");
            }

            ITypeSymbol type = model.GetTypeInfo(field.Declaration.Type).Type;
            int sizeBytes = PrimitiveSize(type);
            byte[] payload = new byte[sizeBytes];

            if (v.Initializer != null)
            {
                var cv = model.GetConstantValue(v.Initializer.Value);
                if (!cv.HasValue)
                    throw new InvalidOperationException(
                        $"[CoffDataSymbol] field '{v.Identifier.Text}' initializer is not a compile-time constant");
                if (section == ".bss" && !IsZero(cv.Value))
                    throw new InvalidOperationException(
                        $"[CoffDataSymbol] '.bss' field must have zero initializer: {v.Identifier.Text}");
                EncodeLE(cv.Value, payload);
            }
            // .bss field with no initializer → payload stays all zeros.

            yield return new EntryDescriptor
            {
                ContainingTypeName = containing,
                SymbolName = name,
                Section = section,
                Alignment = alignment,
                Payload = payload,
            };
        }

        private static string ContainingTypeName(FieldDeclarationSyntax field)
        {
            var t = field.Parent as TypeDeclarationSyntax;
            return t?.Identifier.Text ?? "Anonymous";
        }

        private static void ValidateSection(string s)
        {
            if (s != ".data" && s != ".rdata" && s != ".bss")
                throw new InvalidOperationException(
                    $"[CoffDataSymbol] Section must be .data, .rdata, or .bss (got '{s}')");
        }

        private static void ValidateAlignment(int a)
        {
            if (a < 1 || a > 8192 || (a & (a - 1)) != 0)
                throw new InvalidOperationException(
                    $"[CoffDataSymbol] Alignment must be a power of 2 in 1..8192 (got {a})");
        }

        private static void SectionKind(string s, out bool writable, out bool initialized)
        {
            switch (s)
            {
                case ".data":  writable = true;  initialized = true;  break;
                case ".rdata": writable = false; initialized = true;  break;
                case ".bss":   writable = true;  initialized = false; break;
                default: throw new InvalidOperationException(s);
            }
        }

        private static string SuffixForSection(string s)
        {
            switch (s)
            {
                case ".data":  return ".CoffData";
                case ".rdata": return ".CoffRData";
                case ".bss":   return ".CoffBss";
                default: return ".CoffStubs";
            }
        }

        private static int PrimitiveSize(ITypeSymbol type)
        {
            if (type == null) throw new InvalidOperationException("could not resolve field type");
            switch (type.SpecialType)
            {
                case SpecialType.System_Byte:   case SpecialType.System_SByte:   return 1;
                case SpecialType.System_Int16:  case SpecialType.System_UInt16:  return 2;
                case SpecialType.System_Int32:  case SpecialType.System_UInt32:  return 4;
                case SpecialType.System_Int64:  case SpecialType.System_UInt64:  return 8;
                case SpecialType.System_Single: return 4;
                case SpecialType.System_Double: return 8;
                default:
                    throw new InvalidOperationException(
                        $"[CoffDataSymbol] unsupported field type: {type}");
            }
        }

        private static bool IsZero(object v)
        {
            if (v == null) return true;
            switch (v)
            {
                case byte   b: return b == 0;
                case sbyte  b: return b == 0;
                case short  s: return s == 0;
                case ushort s: return s == 0;
                case int    i: return i == 0;
                case uint   i: return i == 0;
                case long   l: return l == 0;
                case ulong  l: return l == 0;
                case float  f: return f == 0;
                case double d: return d == 0;
                default: return false;
            }
        }

        private static void EncodeLE(object v, byte[] buf)
        {
            ulong u; int size;
            switch (v)
            {
                case byte   b: u = b;        size = 1; break;
                case sbyte  b: u = (ulong)(byte)b; size = 1; break;
                case short  s: u = (ulong)(ushort)s; size = 2; break;
                case ushort s: u = s;        size = 2; break;
                case int    i: u = (ulong)(uint)i; size = 4; break;
                case uint   i: u = i;        size = 4; break;
                case long   l: u = (ulong)l; size = 8; break;
                case ulong  l: u = l;        size = 8; break;
                case float  f:
                    u = BitConverterToUInt32(f); size = 4; break;
                case double d:
                    u = (ulong)BitConverterToInt64(d); size = 8; break;
                default:
                    throw new InvalidOperationException("unsupported constant type: " + v.GetType());
            }
            for (int i = 0; i < size; i++)
                buf[i] = (byte)(u >> (i * 8));
        }

        private static uint BitConverterToUInt32(float f)
        {
            byte[] bytes = BitConverter.GetBytes(f);
            return (uint)(bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24));
        }
        private static long BitConverterToInt64(double d) => BitConverter.DoubleToInt64Bits(d);
    }
}
