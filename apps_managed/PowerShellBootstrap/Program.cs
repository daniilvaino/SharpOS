// PowerShell bootstrap shim — runs on SharpOS bare metal via
// coreclr_execute_assembly, then hands off to stock PowerShell's
// ManagedPSEntry.Main.
//
// Purpose: PS 7.5 ConstrainedLanguage Mode persists on first
// SystemPolicy.GetSystemLockdownPolicy() call because our PAL stubs
// (Wldp, Safer, SHGetKnownFolderPath) can't all consistently report
// "FullLanguage" — at least one branch fails fail-secure and the
// result is cached for the whole runspace lifetime.
//
// The PS 5.1 master-switch env var __PSLockdownPolicy was removed in
// PS 7.x (security review), so the only practical FullLanguage flip
// for an embedder is to pre-populate SystemPolicy's static cache via
// reflection BEFORE PS ever reads it. That's all this shim does.
//
// After the override, we forward to Microsoft.PowerShell.ManagedPSEntry
// .Main(string[]) — the same entry coreclr_execute_assembly(pwsh.dll)
// would otherwise reach directly.

using System;
using System.Reflection;

namespace SharpOS.PowerShellBootstrap;

internal static class Program
{
    public static int Main(string[] args)
    {
        // Answered on 2026-08-20 and left in place behind a switch: a
        // collectible load context gives an assembly its own statics, and our
        // own survive untouched. That is what makes "run one app, then
        // another, each from scratch" possible from managed code.
        if (Environment.GetEnvironmentVariable("SHARPOS_ALC_PROBE") == "1")
        {
            try { ProbeLoadContexts(); }
            catch (Exception ex)
            {
                Console.WriteLine("[alc] probe threw: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        try { ForceFullLanguageMode(); }
        catch (Exception ex)
        {
            // Never let a reflection mishap kill PS startup — fall
            // through and PS will probe normally (back to CLM).
            Console.WriteLine("[bootstrap] FullLanguage override threw: "
                + ex.GetType().Name + ": " + ex.Message);
        }

        return InvokePsMain(args);
    }

    // Can one assembly be run more than once with a clean slate?
    //
    // Calling coreclr_execute_assembly twice already works, but it is the same
    // process and the same load context: statics survive between runs. That is
    // how PowerShell's second start died — its command-line parser keeps a
    // one-shot flag in a long-lived field, saw it still set, and refused. Any
    // app that initialises once would hit the same wall.
    //
    // The .NET answer is a collectible AssemblyLoadContext: its own statics,
    // discarded on unload. If it works here, the launcher can run assemblies
    // back to back, each with a fresh slate, from managed code alone.
    //
    // The target is THIS assembly, and the thing invoked is a counter rather
    // than an entry point: normal-hello's Main runs the whole census, which
    // would drown the answer in ten seconds of unrelated output. Each round
    // should report 1. A 1 then a 2 would mean the context shared statics with
    // us and isolation did not happen.
    private static int s_bumped;

    public static int Bump() => ++s_bumped;

    private static void ProbeLoadContexts()
    {
        string self = typeof(Program).Assembly.Location;
        Console.WriteLine("[alc] probe start, self=" + (string.IsNullOrEmpty(self) ? "<no location>" : self));

        // Location is empty for assemblies the host loaded from memory; fall
        // back to the path the kernel uses, since that is where it came from.
        if (string.IsNullOrEmpty(self))
            self = @"C:\sharpos\PowerShellBootstrap.dll";

        Console.WriteLine("[alc] default-context counter = " + Bump());

        for (int round = 1; round <= 2; round++)
        {
            var alc = new System.Runtime.Loader.AssemblyLoadContext(
                name: "probe" + round, isCollectible: true);
            try
            {
                Assembly asm = alc.LoadFromAssemblyPath(self);
                Type? t = asm.GetType("SharpOS.PowerShellBootstrap.Program", throwOnError: false);
                if (t == null) { Console.WriteLine("[alc] round " + round + ": type not found"); continue; }

                MethodInfo? m = t.GetMethod("Bump", BindingFlags.Public | BindingFlags.Static);
                if (m == null) { Console.WriteLine("[alc] round " + round + ": Bump not found"); continue; }

                object? rc = m.Invoke(null, null);
                Console.WriteLine("[alc] round " + round + ": counter = " + (rc?.ToString() ?? "<null>")
                    + "  (1 means its own statics)");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[alc] round " + round + " failed: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                try { alc.Unload(); Console.WriteLine("[alc] round " + round + ": unload requested"); }
                catch (Exception ex) { Console.WriteLine("[alc] round " + round + " unload threw: " + ex.GetType().Name); }
            }
        }

        // Unload is asynchronous — it completes once nothing references the
        // context. Collecting here makes "did it actually go" answerable rather
        // than assumed.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Console.WriteLine("[alc] probe end, default-context counter = " + Bump());
    }

    // Find SystemPolicy.s_systemLockdownPolicy (Nullable<SystemEnforcementMode>)
    // and set it to None. Defensive: PS may rename the field across
    // versions; we enumerate static fields and match by type, not name.
    private static void ForceFullLanguageMode()
    {
        Assembly sma = Assembly.Load("System.Management.Automation");
        Type? policyType = sma.GetType(
            "System.Management.Automation.Security.SystemPolicy",
            throwOnError: false);
        if (policyType == null)
        {
            Console.WriteLine("[bootstrap] SystemPolicy type not found");
            return;
        }
        Type? modeType = sma.GetType(
            "System.Management.Automation.Security.SystemEnforcementMode",
            throwOnError: false);
        if (modeType == null)
        {
            Console.WriteLine("[bootstrap] SystemEnforcementMode type not found");
            return;
        }
        if (!modeType.IsEnum)
        {
            Console.WriteLine("[bootstrap] SystemEnforcementMode is not an enum");
            return;
        }

        // SystemEnforcementMode.None = 0 — the "no policy / FullLanguage"
        // sentinel PS treats as "we successfully determined no lockdown".
        object none = Enum.ToObject(modeType, 0);
        Type nullableMode = typeof(Nullable<>).MakeGenericType(modeType);

        // Boxed Nullable<SystemEnforcementMode> wrapping None. SetValue
        // on a Nullable<T> static field accepts either the wrapped
        // value or the underlying value (reflection unwraps); we send
        // a real Nullable to be explicit.
        object boxed = Activator.CreateInstance(nullableMode, none)
            ?? throw new InvalidOperationException("Could not box None");

        int hits = 0;
        foreach (FieldInfo fld in policyType.GetFields(
                     BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static))
        {
            // Match any static Nullable<SystemEnforcementMode> field.
            // PS 7.5 source has exactly one: s_systemLockdownPolicy.
            // We grab anything matching the shape so a later rename
            // doesn't silently regress us.
            if (fld.FieldType != nullableMode) continue;
            try
            {
                fld.SetValue(null, boxed);
                hits++;
                Console.WriteLine("[bootstrap] " + fld.Name + " = None");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[bootstrap] could not set " + fld.Name
                    + ": " + ex.Message);
            }
        }
        if (hits == 0)
        {
            Console.WriteLine("[bootstrap] WARN: no Nullable<SystemEnforcementMode> static field found");
        }
    }

    // Load pwsh.dll and call Microsoft.PowerShell.ManagedPSEntry.Main(args).
    // This is the same entry that coreclr_execute_assembly("pwsh.dll")
    // reaches through the metadata-declared entry point.
    private static int InvokePsMain(string[] args)
    {
        Assembly pwsh = Assembly.Load("pwsh");
        Type entry = pwsh.GetType(
            "Microsoft.PowerShell.ManagedPSEntry",
            throwOnError: true)!;
        MethodInfo main = entry.GetMethod(
            "Main",
            BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException("ManagedPSEntry.Main");
        object? rc = main.Invoke(null, new object[] { args });
        return rc is int i ? i : 0;
    }
}
