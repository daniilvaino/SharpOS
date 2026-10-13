using SharpOS.Std.NoRuntime;

namespace System
{
    // Environment variables (step207): the process's own, kept by the kernel
    // and copied to every process it starts. Get/SetEnvironmentVariable as in
    // the BCL; GetEnvironmentVariables is not here — it returns the
    // non-generic IDictionary, which this std does not have
    // (EnvironmentStore.GetAll lists them instead).
    public static partial class Environment
    {
        /// <summary>The variable's value; null when it is not set. Names ignore case, as on Windows.</summary>
        public static string? GetEnvironmentVariable(string variable)
        {
            if (variable == null) throw new ArgumentNullException(nameof(variable));
            return EnvironmentBlock.Get(EnvironmentStore.GetAll(), variable);
        }

        /// <summary>Sets the variable for this process and those it starts from now on; null or "" removes it.</summary>
        public static void SetEnvironmentVariable(string variable, string? value)
        {
            if (variable == null) throw new ArgumentNullException(nameof(variable));
            if (!EnvironmentBlock.IsName(variable))
                throw new ArgumentException("Environment variable name cannot contain equal character or be empty.", nameof(variable));
            if (value != null && value.IndexOf('\0') >= 0)
                throw new ArgumentException("The value contains a NUL character.", nameof(value));
            EnvironmentStore.SetAll(EnvironmentBlock.With(EnvironmentStore.GetAll(), variable, value));
        }
    }
}
