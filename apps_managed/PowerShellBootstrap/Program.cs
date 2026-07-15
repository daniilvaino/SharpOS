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
