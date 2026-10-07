namespace PipeApps
{
    /// <summary>
    /// `--gc-stress N rest...`: the rest runs with a collection before every
    /// N-th allocation, the heap walked around each and freed blocks poisoned
    /// (as AOTTESTS.EXE --gc-stress, app side only). For the pipe tests run
    /// under stress in every process at once (step194 §7, test 13).
    /// </summary>
    internal static class StressArgs
    {
        public static string[] Apply(string[] args)
        {
            if (args.Length < 2 || args[0] != "--gc-stress") return args;
            uint every = (uint)int.Parse(args[1]);
            SharpOS.Std.NoRuntime.GcSweep.PoisonFreed = true;
            SharpOS.Std.NoRuntime.GcStress.VerifyHeap = true;
            SharpOS.Std.NoRuntime.GcStress.Every = every;
            Every = every;
            string[] rest = new string[args.Length - 2];
            for (int i = 0; i < rest.Length; i++) rest[i] = args[i + 2];
            return rest;
        }

        /// <summary>The N this process runs with; 0 without stress.</summary>
        public static uint Every;

        /// <summary>The same prefix for a child: stress goes down the line.</summary>
        public static string[] Pass(params string[] args)
        {
            if (Every == 0) return args;
            string[] all = new string[args.Length + 2];
            all[0] = "--gc-stress";
            all[1] = Every.ToString();
            for (int i = 0; i < args.Length; i++) all[i + 2] = args[i];
            return all;
        }
    }
}
