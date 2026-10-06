using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using SharpOS.Generators.Dynamic;

// Step 193, test 8: the call-site thunks and the SOSD rules on the build host.
// The compiler surface is std's own file (CompilerSurface.cs); what generated
// code calls at run time is a stub with the same signatures. The corpus is the
// battery's DynamicCases.cs: every dynamic operation in it must find its thunk.
internal static class Program
{
    private static int s_failed;
    private static string s_root;

    private const string Stubs = @"
using System;
namespace SharpOS.Std.Dynamic
{
    public static class DynamicRuntime { public static object Run(System.Runtime.CompilerServices.CallSite s, object[] a, Type[] t) => null; }
    public static partial class DynamicMembers
    {
        static partial void RegisterGenerated();
        public static void Member(Type owner, string name, Type type, bool isStatic, Func<object, object> get, Action<object, object> set) { }
        public static void Method(Type owner, string name, bool isStatic, Type[] parameters, Type paramsElement, Type returns, int required, Func<object, object[], object> invoke) { }
        public static void Generic(Type owner, string name, bool isStatic, Type[] typeArguments, Type[] parameters, Type paramsElement, Type returns, int required, Func<object, object[], object> invoke) { }
        public static void NeedsInference(Type owner, string name, bool isStatic, int arity) { }
        public static void Nullable(Type nullable, Type underlying) { }
        public static void Name(Type type, string name) { }
        public static void Anonymous<T>(T witness, string name, Type type, Func<T, object> get) where T : class { }
    }
    public sealed class DynamicMethod { public static object Missing => null; }
    public sealed class ParamsPack { public object[] Items; }
    public static class DynamicBox
    {
        public static ref T Ref<T>(object box) where T : struct => throw null;
        public static object OfNullable<T>(T? v) where T : struct => null;
        public static T? ToNullable<T>(object o) where T : struct => null;
    }
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class DynamicTypesAttribute : Attribute { public DynamicTypesAttribute(params Type[] types) { } }
}
namespace SharpOS.Std.Pipes { public sealed class Expando : System.Collections.Generic.Dictionary<string, object> { } }
";

    private static int Main()
    {
        s_root = FindRoot();
        Corpus();
        Diagnostics();
        Console.WriteLine(s_failed == 0 ? "ALL PASSED" : s_failed + " FAILED");
        return s_failed == 0 ? 0 : 1;
    }

    private static string FindRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "std-no-runtime", "Std.props"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("repository root not found");
    }

    private static SyntaxTree Tree(string text, string path = "") => CSharpSyntaxTree.ParseText(text, path: path);

    // No Microsoft.CSharp, System.Linq.Expressions or their facades: the surface is std's.
    private static IEnumerable<MetadataReference> References()
    {
        string[] keep = { "System.Private.CoreLib", "System.Runtime", "System.Collections", "System.Console" };
        return ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator)
            .Where(p => keep.Contains(Path.GetFileNameWithoutExtension(p)))
            .Select(p => MetadataReference.CreateFromFile(p));
    }

    private static CSharpCompilation Compile(params SyntaxTree[] code)
    {
        var trees = new List<SyntaxTree>
        {
            Tree(File.ReadAllText(Path.Combine(s_root, "std-no-runtime", "Dynamic", "CompilerSurface.cs")), "CompilerSurface.cs"),
            Tree(Stubs, "Stubs.cs"),
        };
        trees.AddRange(code);
        return CSharpCompilation.Create("Test", trees, References(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Disable));
    }

    private static void Check(string name, bool ok, string detail = null)
    {
        Console.WriteLine((ok ? "ok    " : "FAIL  ") + name + (ok || detail == null ? "" : "\n        " + detail));
        if (!ok) s_failed++;
    }

    private static List<string> Missing(Compilation c, out int sites, out List<Diagnostic> errors)
    {
        using var stream = new MemoryStream();
        var result = c.Emit(stream);
        errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        sites = 0;
        if (!result.Success) return null;
        stream.Position = 0;
        using var pe = new PEReader(stream);
        return DynamicCheck.Check(pe.GetMetadataReader(), out sites, out _);
    }

    private static void Corpus()
    {
        SyntaxTree cases = Tree(File.ReadAllText(Path.Combine(s_root, "apps_native", "AotTests", "DynamicCases.cs")), "DynamicCases.cs");
        CSharpCompilation plain = Compile(cases);

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new DynamicGenerator());
        driver.RunGeneratorsAndUpdateCompilation(plain, out Compilation generated, out ImmutableArray<Diagnostic> generatorDiagnostics);
        List<string> missing = Missing(generated, out int sites, out List<Diagnostic> errors);
        Check("the generated thunks and tables compile", errors.Count == 0 && generatorDiagnostics.Length == 0,
              string.Join("\n        ", errors.Take(8).Select(e => e.ToString())));
        Check("every CallSite<T> of DynamicCases.cs has a thunk (" + sites + " delegate types)",
              missing != null && missing.Count == 0 && sites > 30,
              missing == null ? "no assembly" : string.Join("\n        ", missing));

        ImmutableArray<Diagnostic> found = generated.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new DynamicAnalyzer()))
                                                    .GetAnalyzerDiagnosticsAsync().Result;
        Check("DynamicCases.cs: no SOSD diagnostics", found.Length == 0, string.Join("\n        ", found.Select(d => d.ToString())));

        // Without the generator: the check must name every call site.
        List<string> holes = Missing(plain, out int plainSites, out _);
        Check("the IL check finds the call sites with no thunk (" + (holes?.Count ?? -1) + " of " + plainSites + ")",
              holes != null && holes.Count == plainSites && plainSites == sites);

        // One thunk taken out of the generated list: exactly that one is missing.
        var trees = generated.SyntaxTrees.ToList();
        SyntaxTree thunks = trees.First(t => t.FilePath.EndsWith("CallSiteThunks.g.cs"));
        string text = thunks.ToString();
        int at = text.IndexOf("            Register(typeof(", StringComparison.Ordinal);
        int end = text.IndexOf('\n', at);
        string cut = text.Remove(at, end - at + 1);
        int listAt = cut.IndexOf("Generated = \"", StringComparison.Ordinal) + "Generated = \"".Length;
        int firstEnd = cut.IndexOf("\\n", listAt, StringComparison.Ordinal);
        string firstName = cut.Substring(listAt, firstEnd - listAt);
        cut = cut.Remove(listAt, firstEnd - listAt + 2);
        Compilation without = generated.ReplaceSyntaxTree(thunks, Tree(cut, thunks.FilePath));
        List<string> one = Missing(without, out _, out List<Diagnostic> cutErrors);
        Check("one thunk removed: the check names exactly it", one != null && one.Count == 1 && one[0] == firstName,
              one == null ? string.Join("; ", cutErrors.Take(3)) : string.Join(", ", one) + " vs " + firstName);
    }

    private static void Expect(string name, string[] expected, string code)
    {
        CSharpCompilation c = Compile(Tree("using System; namespace Sample {\n" + code + "\n}"));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new DynamicGenerator());
        driver.RunGeneratorsAndUpdateCompilation(c, out Compilation generated, out _);
        ImmutableArray<Diagnostic> found = generated.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new DynamicAnalyzer()))
                                                    .GetAnalyzerDiagnosticsAsync().Result;
        var ids = found.Select(d => d.Id).Distinct().OrderBy(x => x).ToList();
        var compileErrors = c.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (expected.Length == 0)
        {
            // Correct code: what the generator wrote compiles and serves every call site.
            List<string> missing = Missing(generated, out _, out List<Diagnostic> emitErrors);
            compileErrors.AddRange(emitErrors);
            if (missing != null && missing.Count > 0) compileErrors.Add(Diagnostic.Create("SOSD100", "test", "missing thunk " + missing[0], DiagnosticSeverity.Error, DiagnosticSeverity.Error, true, 0));
        }
        Check(name, compileErrors.Count == 0 && ids.SequenceEqual(expected.Distinct().OrderBy(x => x)),
              string.Join("\n        ", found.Select(d => d.ToString()).Concat(compileErrors.Take(3).Select(e => "compile: " + e))));
    }

    // The generated member tables contain each of the fragments.
    private static void Generated(string name, string code, params string[] fragments)
    {
        CSharpCompilation c = Compile(Tree("using System; namespace Sample {\n" + code + "\n}"));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new DynamicGenerator());
        driver.RunGeneratorsAndUpdateCompilation(c, out Compilation generated, out _);
        string members = generated.SyntaxTrees.FirstOrDefault(t => t.FilePath.EndsWith("DynamicMembers.g.cs"))?.ToString() ?? "";
        var absent = fragments.Where(f => !members.Contains(f)).ToList();
        Check(name, absent.Count == 0, "absent: " + string.Join(" | ", absent));
    }

    private static void Diagnostics()
    {
        Expect("SOSD001: ref argument", new[] { "SOSD001" }, @"
class C { void M(dynamic d) { int x = 0; d.F(ref x); } }");
        Expect("SOSD001: out argument", new[] { "SOSD001" }, @"
class C { void M(dynamic d) { d.F(out int x); } }");
        Expect("SOSD002: struct variable receiver", new[] { "SOSD002" }, @"
struct S { public void F(object o) { } }
class C { void M(dynamic d) { S s = default; s.F(d); } }");
        Expect("SOSD002: struct parameter receiver of an indexer assignment", new[] { "SOSD002" }, @"
struct S { public int this[int i] { get => 0; set { } } }
class C { void M(S s, dynamic d) { s[d] = 1; } }");
        Expect("SOSD003: a type parameter in an argument", new[] { "SOSD003" }, @"
class C { void M<T>(dynamic d, T t) { d.F(t); } }");
        Expect("SOSD004: named argument", new[] { "SOSD004" }, @"
class C { void M(dynamic d) { d.F(x: 1); } }");
        Expect("SOSD005: a private nested type as an argument", new[] { "SOSD005" }, @"
class C { private sealed class P { } void M(dynamic d) { d.F(new P()); } }");
        Expect("SOSD006: too many arguments", new[] { "SOSD006" }, @"
class C { void M(dynamic d) { d.F(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15); } }");
        Generated("a generic method closed by a call site's type argument is registered", @"
internal sealed class G { public string Id<T>(T x) => """"; public string Free<T>(T x) => """"; }
class C { void M(dynamic d) { object a = d.Id<int>(5); object b = d.Free(1); } }",
            "Generic(typeof(global::Sample.G), \"Id\"", "NeedsInference(typeof(global::Sample.G), \"Free\"");
        Expect("correct: rvalue struct receiver, internal types, static calls, anonymous argument", new string[0], @"
struct S { public string F(object o) => """"; }
internal sealed class Item { public int N; }
class C
{
    static S Make() => default;
    void M(dynamic d)
    {
        object a = Make().F(d);
        object b = d.F(new Item(), new { X = 1 });
        object c = Math.Max(d, 3);
        int n = d.N + 1;
        d.N = n;
        d[0] = d;
    }
}");
    }
}
