using System;
using System.Collections.Generic;
using System.Runtime;
using SharpOS.AppSdk;
using SharpOS.Std.Pipes;

namespace UtilsApp
{
    // UTILS COMMAND [arguments]: the small commands, one program for all of
    // them. The shell knows each command's name as an alias (Executor.Aliases):
    // thirteen programs of 600 KiB each would be 8 MB of the same runtime.
    internal static unsafe partial class AppEntry
    {
        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            return AppRuntime.Finish(Run(PipeApps.StressArgs.Apply(AppHost.Arguments)));
        }

        [RuntimeExport("SharpAppBootstrap")]
        private static int SharpAppBootstrap(ulong startupPointer)
        {
            RuntimeImports.ManagedStartup();
            return SharpAppEntry(startupPointer);
        }

        private static int Main() => Run(AppHost.Arguments);

        private static int Run(string[] args)
        {
            if (args.Length == 0) return Unknown("");
            var rest = new string[args.Length - 1];
            Array.Copy(args, 1, rest, 0, rest.Length);
            string command = args[0];
            try
            {
                switch (command)
                {
                    case "head": return Filters.Head(rest);
                    case "tail": return Filters.Tail(rest);
                    case "grep": return Filters.Grep(rest);
                    case "wc": return Filters.Wc(rest);
                    case "sort": return Filters.Sort(rest);
                    case "uniq": return Filters.Uniq(rest);
                    case "tee": return Tee.Run(rest);
                    case "find": return Find.Run(rest);
                    case "xxd": return Xxd.Run(rest);
                    case "ps": return SystemCommands.Ps(rest);
                    case "kill": return SystemCommands.Kill(rest);
                    case "sleep": return SystemCommands.Sleep(rest);
                    case "date": return SystemCommands.Date(rest);
                    case "tone": return Tone.Run(rest);
                    case "beep": return Tone.Beep(rest);
                    default: return Unknown(command);
                }
            }
            catch (PipeException e) when (e.Status == PipeStatus.Broken)
            {
                // The neighbour's end broke: quiet, as for any stage.
                return 141;
            }
            catch (PipeException e)
            {
                Console.Error.WriteLine(command + ": " + e.Message);
                return 1;
            }
            catch (System.IO.IOException e)
            {
                Console.Error.WriteLine(command + ": " + e.Message);
                return 1;
            }
        }

        private static int Unknown(string command)
        {
            Console.Error.WriteLine("UTILS: unknown command '" + command + "': head tail grep wc sort uniq tee find xxd ps kill sleep date tone beep");
            return 2;
        }
    }

    /// <summary>Options as the commands take them: flags, flags with a value, and the words left.</summary>
    internal sealed class Options
    {
        private readonly string _usage;
        public readonly List<string> Words = new List<string>();
        public bool Help;
        public string Wrong;

        public Options(string usage) => _usage = usage;

        public int Refuse()
        {
            if (Help)
            {
                Console.WriteLine("usage: " + _usage);
                return 0;
            }
            if (Wrong != null) Console.Error.WriteLine(Wrong);
            Console.Error.WriteLine("usage: " + _usage);
            return 2;
        }

        public bool Failed => Help || Wrong != null;

        // Single-letter options, which may be joined (-iv); those in valued take
        // a value, joined (-n5) or the next word (-n 5). Long ones are as written.
        public Dictionary<string, string> Parse(string[] args, string flags, string valued, string[] longValued = null)
        {
            var found = new Dictionary<string, string>();
            bool optionsOver = false;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (optionsOver || a.Length < 2 || a[0] != '-')
                {
                    Words.Add(a);
                    continue;
                }
                if (a == "--") { optionsOver = true; continue; }
                if (a == "--help" || a == "-h" && flags.IndexOf('h') < 0 || a == "-?") { Help = true; return found; }
                if (a[1] == '-' || (longValued != null && Array.IndexOf(longValued, a) >= 0))
                {
                    if (longValued == null || Array.IndexOf(longValued, a) < 0 || i + 1 >= args.Length)
                    {
                        Wrong = "unknown option: " + a;
                        return found;
                    }
                    found[a] = args[++i];
                    continue;
                }
                for (int k = 1; k < a.Length; k++)
                {
                    char c = a[k];
                    if (valued.IndexOf(c) >= 0)
                    {
                        string value = k + 1 < a.Length ? a.Substring(k + 1) : i + 1 < args.Length ? args[++i] : null;
                        if (value == null)
                        {
                            Wrong = "-" + c.ToString() + " needs a value";
                            return found;
                        }
                        found[c.ToString()] = value;
                        break;
                    }
                    // -5 as -n 5, for head and tail.
                    if (c >= '0' && c <= '9' && valued.IndexOf('n') >= 0)
                    {
                        found["n"] = a.Substring(k);
                        break;
                    }
                    if (flags.IndexOf(c) < 0)
                    {
                        Wrong = "unknown option: -" + c.ToString();
                        return found;
                    }
                    found[c.ToString()] = "";
                }
            }
            return found;
        }
    }
}
