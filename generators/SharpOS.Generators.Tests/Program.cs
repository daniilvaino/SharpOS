using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using SharpOS.Generators;

// Pipe test 12: every rule of RegionAnalyzer and MessageGenerator against an
// example that must trip it, and correct code that must not. The pipe API is a
// stub with the same shapes as std-no-runtime/Pipes — the analyzers look at
// names and attributes, not at bodies.
internal static class Program
{
    private const string Api = @"
using System;
namespace SharpOS.Std.Exchange
{
    public static class TypeKeys
    {
        public readonly struct Field { public Field(string n, string t, int o) { } }
        public static int StructOffset<TS, TF>(ref TS v, ref TF f) => 0;
    }
}
namespace SharpOS.Std.Pipes
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum)]
    public sealed class MessageAttribute : Attribute { }
    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class RetainsAttribute : Attribute { }
    public static partial class MessageCatalog
    {
        static partial void RegisterGenerated();
        public static object Witness(Type t) => null;
        public static SharpOS.Std.Exchange.TypeKeys.Field Field<T>(string n, string t, object o, ref T f) => default;
        public static void Register(string n, object w, SharpOS.Std.Exchange.TypeKeys.Field[] f) { }
        public static void RegisterArray<T>(T[] w, string e) { }
    }
    public sealed class Region<T> : IDisposable where T : class
    {
        public T Root => null;
        public T ToHeap() => null;
        public void Dispose() { }
    }
    public sealed class PipeWriter<T> : IDisposable where T : class
    {
        public static int Connect(string name, out PipeWriter<T> w) { w = null; return 0; }
        public int Copy(T m) => 0;
        public int Move(Region<T> r) => 0;
        public void Dispose() { }
    }
    public sealed class PipeReader<T> : IDisposable where T : class
    {
        public static int Connect(string name, out PipeReader<T> r) { r = null; return 0; }
        public Region<T> Receive() => null;
        public void Dispose() { }
    }
    public static class Pipe
    {
        public static int Create<T>(out PipeWriter<T> w, out PipeReader<T> r) where T : class { w = null; r = null; return 0; }
    }
}
namespace Sample
{
    using SharpOS.Std.Pipes;
    [Message] public sealed class Node { public int Value; public string Name; public Node Next; public Node[] Kids; }
    public sealed class Plain { public int X; }
    public sealed class Holder { public Node Keep; }
}
";

    private static int s_failed;

    private static int Main()
    {
        // ---- SOSR001 ----
        Expect("SOSR001: a pipe end over an unmarked type", new[] { "SOSR001" }, @"
class C { void M() { PipeWriter<Plain>.Connect(""x"", out var w); } }");
        Expect("SOSR001: a region of an unmarked type", new[] { "SOSR001" }, @"
class C { void M() { Region<Plain> r = null; } }");
        Expect("SOSR001: Pipe.Create over an unmarked type", new[] { "SOSR001" }, @"
class C { void M() { Pipe.Create<Plain>(out var w, out var r); } }");

        // ---- SOSR002 ----
        Expect("SOSR002: into a static field", new[] { "SOSR002" }, @"
class C { static Node s_keep; void M(PipeReader<Node> p) { using (var r = p.Receive()) { s_keep = r.Root; } } }");
        Expect("SOSR002: into a field of a heap object", new[] { "SOSR002" }, @"
class C { void M(PipeReader<Node> p, Holder h) { using (var r = p.Receive()) { h.Keep = r.Root.Next; } } }");
        Expect("SOSR002: into an array outside the region", new[] { "SOSR002" }, @"
class C { void M(PipeReader<Node> p, Node[] a) { using (var r = p.Receive()) { var n = r.Root; a[0] = n; } } }");
        Expect("SOSR002: captured by a lambda", new[] { "SOSR002" }, @"
class C { void M(PipeReader<Node> p) { using (var r = p.Receive()) { var n = r.Root; Func<int> f = () => n.Value; f(); } } }");
        Expect("SOSR002: returned", new[] { "SOSR002" }, @"
class C { Node M(Region<Node> r) { return r.Root.Next; } }");

        // ---- SOSR003 ----
        Expect("SOSR003: used after Dispose", new[] { "SOSR003" }, @"
class C { int M(PipeReader<Node> p) { var r = p.Receive(); var n = r.Root; r.Dispose(); return n.Value; } }");
        Expect("SOSR003: used after Move", new[] { "SOSR003" }, @"
class C { int M(PipeReader<Node> p, PipeWriter<Node> w) { var r = p.Receive(); w.Move(r); return r.Root.Value; } }");
        Expect("SOSR003: used after its using", new[] { "SOSR003" }, @"
class C { int M(PipeReader<Node> p) { Node n; using (var r = p.Receive()) { n = r.Root; } return n.Value; } }");
        Expect("SOSR003 + SOSR005: disposed on one path, used after; leaked on the other", new[] { "SOSR003", "SOSR005" }, @"
class C { int M(PipeReader<Node> p, bool b) { var r = p.Receive(); var n = r.Root; if (b) r.Dispose(); return n.Value; } }");

        // ---- SOSR004 ----
        Expect("SOSR004: into a [Retains] parameter", new[] { "SOSR004" }, @"
class C { static void Keep([Retains] object o) { } void M(PipeReader<Node> p) { using (var r = p.Receive()) Keep(r.Root); } }");
        Expect("SOSR004: into a parameter the body stores", new[] { "SOSR004" }, @"
class C { static object s; static void Store(object o) { var x = o; s = x; } void M(PipeReader<Node> p) { using (var r = p.Receive()) Store(r.Root.Next); } }");
        Expect("SOSR004: through a chain of calls", new[] { "SOSR004" }, @"
class C { static object s; static void A(object o) => B(o); static void B(object o) { s = o; } void M(PipeReader<Node> p) { using (var r = p.Receive()) A(r.Root); } }");

        // ---- SOSR005 ----
        Expect("SOSR005: not released on an early return", new[] { "SOSR005" }, @"
class C { void M(PipeReader<Node> p, bool b) { var r = p.Receive(); if (b) return; r.Dispose(); } }");
        Expect("SOSR005: never released", new[] { "SOSR005" }, @"
class C { int M(PipeReader<Node> p) { var r = p.Receive(); return 1; } }");

        // ---- correct code: no diagnostics at all ----
        Expect("correct: read in place inside using, copy out, move on, null loop", new string[0], @"
class C
{
    static int Count(Node n) { int c = 0; for (Node x = n; x != null; x = x.Next) c++; return c; }
    int M(PipeReader<Node> p, PipeWriter<Node> w)
    {
        int total = 0;
        Node mine = null;
        Region<Node> r;
        while ((r = p.Receive()) != null)
        {
            using (r)
            {
                Node n = r.Root;
                total += n.Value + Count(n);
                n.Next = n.Kids[0];
                n.Kids[1] = n;
                foreach (Node k in n.Kids) total += k.Value;
                mine = r.ToHeap();
            }
        }
        var next = p.Receive();
        if (next != null && next.Root.Value > 100) w.Move(next);
        else if (next != null) next.Dispose();
        return total + mine.Value;
    }
}");
        Expect("correct: region handed to the caller", new string[0], @"
class C { Region<Node> M(PipeReader<Node> p) { var r = p.Receive(); return r; } }");

        // ---- the generator ----
        Generate("SOSM001: a delegate field", new[] { "SOSM001" }, "[Message] public sealed class G { public Action A; }");
        Generate("SOSM002: a pointer field", new[] { "SOSM002" }, "[Message] public unsafe sealed class G { public int* P; }");
        Generate("SOSM003: a field of a class outside the catalog", new[] { "SOSM003" }, "[Message] public sealed class G { public Sample.Plain P; }");
        Generate("SOSM004: an auto-property", new[] { "SOSM004" }, "[Message] public sealed class G { public int P { get; set; } }");
        Generate("SOSM005: a private field of a non-partial type", new[] { "SOSM005" }, "[Message] public sealed class G { private int p; public int Q() => p; }");
        Generate("SOSM006: a generic message type", new[] { "SOSM006" }, "[Message] public sealed class G<T> { public int X; }");
        Generate("generator: a correct type, enum, struct, partial with private fields, arrays", new string[0], @"
[Message] public enum Shade { Pale, Deep }
[Message] public struct Pair { public string Name; public int X; }
[Message] public sealed partial class G { private int hidden; public Shade S; public Pair[] Pairs; public Sample.Node[] Nodes; public string[] Names; public int[,] Grid; public object Any; public int Hidden => hidden; }");

        Console.WriteLine(s_failed == 0 ? "ALL PASSED" : s_failed + " FAILED");
        return s_failed == 0 ? 0 : 1;
    }

    private static CSharpCompilation Compile(string code)
    {
        string source = Api + "\nnamespace Sample { using System; using SharpOS.Std.Pipes;\n" + code + "\n}\n";
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")).Split(Path.PathSeparator)
            .Select(p => MetadataReference.CreateFromFile(p));
        return CSharpCompilation.Create("Test", new[] { CSharpSyntaxTree.ParseText(source) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
    }

    private static void Expect(string name, string[] expected, string code)
    {
        CSharpCompilation compilation = Compile(code);
        var errors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        ImmutableArray<Diagnostic> found = compilation.WithAnalyzers(ImmutableArray.Create<DiagnosticAnalyzer>(new RegionAnalyzer()))
                                                      .GetAnalyzerDiagnosticsAsync().Result;
        Report(name, expected, found.Select(d => d.Id).ToList(), errors, found);
    }

    private static void Generate(string name, string[] expected, string code)
    {
        CSharpCompilation compilation = Compile(code);
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new MessageGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out ImmutableArray<Diagnostic> generated);
        var errors = output.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Report(name, expected, generated.Select(d => d.Id).ToList(), expected.Length == 0 ? errors : new List<Diagnostic>(), generated);
    }

    private static void Report(string name, string[] expected, List<string> found, List<Diagnostic> compileErrors, IEnumerable<Diagnostic> all)
    {
        bool ok = compileErrors.Count == 0
                  && found.Distinct().OrderBy(x => x).SequenceEqual(expected.Distinct().OrderBy(x => x));
        Console.WriteLine((ok ? "ok    " : "FAIL  ") + name);
        if (!ok)
        {
            s_failed++;
            foreach (Diagnostic d in all) Console.WriteLine("        " + d);
            foreach (Diagnostic d in compileErrors.Take(5)) Console.WriteLine("        compile: " + d);
        }
    }
}
