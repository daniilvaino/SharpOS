// Runs a managed program several times inside one runtime, the way SharpOS
// does: every start from the launcher is another coreclr_execute_assembly on
// the same session (Kernel/Exec/CoreClrHost), which finds the assembly already
// loaded, its code already compiled, the heap already mapped. A desktop
// reference taken as separate `dotnet Bench.dll` processes starts cold every
// time and does not compare with the second and third of those runs.
//
//   dotnet BenchHost.dll Bench.dll 3
//
// The program itself is untouched; the runtime settings are SharpOS's (see
// BenchHost.csproj and runtimeconfig.template.json).

if (args.Length < 1)
{
    Console.Error.WriteLine("usage: BenchHost <assembly> [runs]");
    return 2;
}

string path = Path.GetFullPath(args[0]);
int runs = args.Length > 1 && int.TryParse(args[1], out int requested) && requested > 0 ? requested : 3;

for (int run = 1; run <= runs; run++)
{
    Console.WriteLine("[host] run " + run + "/" + runs);
    int exitCode = AppDomain.CurrentDomain.ExecuteAssembly(path, Array.Empty<string>());
    Console.WriteLine("[host] run " + run + " exit=" + exitCode);
}

return 0;
