namespace SharpOS.Std.NoRuntime
{
    /// <summary>
    /// An environment as Windows keeps it (step207): NAME=value strings, and
    /// the block "A=1\0B=2\0" they travel in between a process and the kernel.
    /// The kernel and both tiers of std use these; where the environment is
    /// kept is the tier's own (EnvironmentStore).
    /// </summary>
    /// <remarks>
    /// An environment array is never changed in place: a change makes a new
    /// one, so it can be shared and read without a lock. Names compare
    /// ignoring ASCII case, as on Windows — pwsh's `$env:Path` and `PATH`
    /// are one variable.
    /// </remarks>
    public static unsafe class EnvironmentBlock
    {
        /// <summary>Characters a block may take.</summary>
        public const int MaxChars = 64 * 1024;

        /// <summary>The value of <paramref name="name"/>; null when it is not set.</summary>
        public static string Get(string[] environment, string name)
        {
            int at = IndexOf(environment, environment.Length, name);
            return at < 0 ? null : environment[at].Substring(name.Length + 1);
        }

        /// <summary>A new environment with <paramref name="name"/> set — removed when the value is null or empty.</summary>
        public static string[] With(string[] environment, string name, string value)
        {
            int at = IndexOf(environment, environment.Length, name);
            bool remove = string.IsNullOrEmpty(value);
            if (at < 0 && remove) return environment;
            var result = new string[environment.Length + (at < 0 ? 1 : remove ? -1 : 0)];
            int n = 0;
            for (int i = 0; i < environment.Length; i++)
            {
                if (i == at)
                {
                    if (!remove) result[n++] = name + "=" + value;
                    continue;
                }
                result[n++] = environment[i];
            }
            if (at < 0) result[n] = name + "=" + value;
            return result;
        }

        /// <summary>Whether <paramref name="name"/> can be a variable's name: not empty, no '=' and no NUL.</summary>
        public static bool IsName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            for (int i = 0; i < name.Length; i++)
                if (name[i] == '=' || name[i] == '\0') return false;
            return true;
        }

        /// <summary>
        /// The block into <paramref name="into"/> when it fits
        /// <paramref name="capacity"/> characters; its length either way.
        /// </summary>
        public static int Encode(string[] environment, char* into, int capacity)
        {
            int length = 0;
            for (int i = 0; i < environment.Length; i++) length += environment[i].Length + 1;
            if (into == null || capacity < length) return length;
            int at = 0;
            for (int i = 0; i < environment.Length; i++)
            {
                string entry = environment[i];
                for (int c = 0; c < entry.Length; c++) into[at++] = entry[c];
                into[at++] = '\0';
            }
            return length;
        }

        /// <summary>
        /// The entries of a block of <paramref name="length"/> characters; null
        /// when one has no name ("=x") or no '=', a name is set twice, or the
        /// block is longer than <see cref="MaxChars"/>.
        /// </summary>
        public static string[] Decode(char* block, int length)
        {
            if (length < 0 || length > MaxChars || (block == null && length > 0)) return null;
            int count = 0;
            for (int i = 0; i < length; i++)
                if (block[i] == '\0') count++;
            if (length > 0 && block[length - 1] != '\0') count++;
            var entries = new string[count];
            int n = 0, start = 0;
            for (int i = 0; i <= length; i++)
            {
                if (i < length && block[i] != '\0') continue;
                if (i > start)
                {
                    string entry = new string(new System.ReadOnlySpan<char>(block + start, i - start));
                    int eq = entry.IndexOf('=');
                    if (eq <= 0 || IndexOf(entries, n, entry.Substring(0, eq)) >= 0) return null;
                    entries[n++] = entry;
                }
                start = i + 1;
            }
            if (n == entries.Length) return entries;
            var trimmed = new string[n];
            for (int i = 0; i < n; i++) trimmed[i] = entries[i];
            return trimmed;
        }

        private static int IndexOf(string[] entries, int count, string name)
        {
            for (int i = 0; i < count; i++)
            {
                string entry = entries[i];
                if (entry.Length > name.Length && entry[name.Length] == '=' && SameName(entry, name)) return i;
            }
            return -1;
        }

        private static bool SameName(string entry, string name)
        {
            for (int i = 0; i < name.Length; i++)
            {
                char a = entry[i], b = name[i];
                if (a >= 'a' && a <= 'z') a = (char)(a - 32);
                if (b >= 'a' && b <= 'z') b = (char)(b - 32);
                if (a != b) return false;
            }
            return true;
        }
    }
}
