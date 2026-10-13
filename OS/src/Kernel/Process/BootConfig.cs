using System.Collections.Generic;
using SharpOS.Std.Dotenv;
using OS.Hal;
using OS.Kernel.File;
using OS.Kernel.Util;

namespace OS.Kernel.Process
{
    /// <summary>
    /// The configuration file (step207): the environment the kernel gives
    /// everything it starts, and what it starts.
    /// </summary>
    /// <remarks>
    /// The .env format as Docker Compose reads it (std: SharpOS.Std.Dotenv,
    /// which behaves as compose-go's parser does): NAME=value, `export`, `#`
    /// comments, single quotes literal, double quotes with escapes,
    /// `$NAME`, `${NAME}`, `${NAME:-default}` and the rest of the substitution
    /// forms. Not a shell script: the launcher's tiles start pwsh and games
    /// without the shell, and they need the same environment.
    ///
    /// The defaults below come first, so the file overrides them and may
    /// refer to them (PATH=$PATH;\more). Two names are the kernel's, not the
    /// environment's:
    ///   INIT       the program to start, then its arguments (split at spaces)
    ///              — the launcher, or the shell with the battery script that
    ///              run_build.ps1 -Autorun writes;
    ///   BUILDINFO  "on": programs print their build line at start.
    /// A missing file, or one the parser refuses, is the defaults: the machine
    /// still boots, and the log says why.
    /// </remarks>
    internal static class BootConfig
    {
        internal const string Path = "\\config\\system.env";

        internal const string DefaultInit = "\\bin\\LAUNCHER.EXE";

        /// <summary>The program and its arguments to start after boot.</summary>
        internal static string[] Init { get; private set; }

        /// <summary>Whether programs announce their build (BUILDINFO=on).</summary>
        internal static bool AnnounceBuild { get; private set; }

        /// <summary>Reads the file (or takes the defaults) and sets the boot environment.</summary>
        internal static unsafe void Load()
        {
            Dictionary<string, string> vars = Defaults();

            if (FileSystem.ReadAll(Path, out FileBuffer file))
            {
                // Read once at boot and kept: a few hundred bytes, not worth
                // knowing which allocator the platform read it into.
                string text = System.Text.Encoding.UTF8.GetString(new System.ReadOnlySpan<byte>((byte*)file.Pointer, (int)file.Length));
                try
                {
                    DotenvParser.ParseWithLookup(text, vars);
                    DebugLog.Write(LogLevel.Info, "config: " + Path + " read");
                }
                catch (System.Exception e)
                {
                    vars = Defaults();
                    DebugLog.Write(LogLevel.Warn, "config: " + Path + " refused, defaults taken: " + e.Message);
                }
            }
            else
            {
                DebugLog.Write(LogLevel.Info, "config: no " + Path + " - defaults");
            }

            vars.TryGetValue("INIT", out string init);
            vars.TryGetValue("BUILDINFO", out string announce);
            vars.Remove("INIT");
            vars.Remove("BUILDINFO");

            var env = new string[vars.Count];
            int n = 0;
            foreach (KeyValuePair<string, string> kv in vars) env[n++] = kv.Key + "=" + kv.Value;
            ProcessEnvironment.Boot = env;
            Init = SplitInit(init ?? DefaultInit);
            AnnounceBuild = announce == "on";
        }

        private static Dictionary<string, string> Defaults() => new Dictionary<string, string>
        {
            ["PATH"] = "\\bin;\\tests;\\games",
            ["HOME"] = "\\home",
            ["INIT"] = DefaultInit,
        };

        private static string[] SplitInit(string init)
        {
            string[] parts = init.Split(' ');
            int n = 0;
            foreach (string p in parts)
                if (p.Length > 0) n++;
            var result = new string[n];
            n = 0;
            foreach (string p in parts)
                if (p.Length > 0) result[n++] = p;
            return result.Length > 0 ? result : new[] { DefaultInit };
        }
    }
}
