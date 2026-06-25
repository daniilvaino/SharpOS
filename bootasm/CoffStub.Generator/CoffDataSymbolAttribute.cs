// step 121 — CoffStub.Generator attribute defn.
//
// Marks a static field as a "compile-time COFF data symbol" — at OS build
// time, EmitCoffStubsTask generates a tiny COFF .obj file containing this
// field's value as a native data symbol with the given name, section, and
// alignment. The .obj gets fed into the kernel link via LinkerArg.
//
// Use case: closing the ILC gap where [RuntimeExport] on a static field
// does NOT emit a native data symbol. With [CoffDataSymbol], the same
// field becomes a real link-time data symbol for MSVC's CRT-aware codegen
// (e.g., `__security_cookie`).
//
// Lives in CoffStub.Generator.csproj (compiled into the Task assembly) AND
// linked into consumer C# code via `<Compile Include="..." />` так что
// type identity matches the one EmitCoffStubsTask matches against.
//
// Rules (validated at build time):
//   - Field must be `public static` (not const, not readonly required but
//     value treated as immutable).
//   - Field type must be a primitive value type with deterministic
//     little-endian binary layout: byte, sbyte, short, ushort, int, uint,
//     long, ulong. (Floats/doubles allowed; structs deferred.)
//   - Initializer must be a compile-time constant literal (e.g., `= 42`,
//     `= 0xABUL`). No expressions / method calls.
//   - Section ∈ {".data", ".rdata", ".bss"}. ".bss" requires zero
//     initializer (value treated as uninitialized).
//   - Alignment must be a power of 2 in {1, 2, 4, 8, 16, 32, 64, 128, 256,
//     512, 1024, 2048, 4096, 8192}.

namespace BootAsm
{
    [System.AttributeUsage(System.AttributeTargets.Field, AllowMultiple = false, Inherited = false)]
    internal sealed class CoffDataSymbolAttribute : System.Attribute
    {
        public CoffDataSymbolAttribute(string name) { Name = name; }
        public string Name { get; }
        public string Section { get; set; } = ".data";
        public int Alignment { get; set; } = 8;
    }
}
