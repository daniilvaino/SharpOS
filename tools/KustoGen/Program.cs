using System;
using System.IO;
using Kusto.Language.Generator;     // syntax nodes
using Kusto.Language.Generators;    // command grammars

// What each T4 template in vendor/KustoLanguage/src computes, written to the
// .cs file the template would produce (its name with .cs).
internal static class Program
{
    private static int Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : FindRoot();
        string src = Path.Combine(root, "vendor", "KustoLanguage", "src");

        Write(Path.Combine(src, "Syntax", "CodeGen", "GeneratedSyntaxNodes.cs"),
              SyntaxNodeGenerator.Generate(SyntaxNodeInfos.All, SyntaxNodeInfos.KnownTypes));

        string commands = Path.Combine(src, "Parser", "CodeGen");
        Commands(commands, "Engine", typeof(EngineCommandInfos));
        Commands(commands, "ClusterManager", typeof(ClusterManagerCommandInfos));
        Commands(commands, "DataManager", typeof(DataManagerCommandInfos));
        Commands(commands, "AriaBridge", typeof(AriaBridgeCommandInfos));
        return 0;
    }

    private static void Commands(string folder, string prefix, Type infos)
    {
        Write(Path.Combine(folder, prefix + "Commands.cs"), new CommandGenerator().GenerateSymbols(prefix + "Commands", infos));
        Write(Path.Combine(folder, prefix + "CommandGrammar.cs"), new CommandGenerator().GenerateParser(prefix + "CommandGrammar", infos));
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, text);
        Console.WriteLine(path + ": " + text.Length.ToString() + " chars");
    }

    // The repository root: the folder holding vendor/KustoLanguage, upward from here.
    private static string FindRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "vendor", "KustoLanguage")))
            dir = Path.GetDirectoryName(dir);
        if (dir == null) throw new InvalidOperationException("vendor/KustoLanguage not found above " + AppContext.BaseDirectory);
        return dir;
    }
}
