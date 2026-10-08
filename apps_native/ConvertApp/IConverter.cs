namespace PipeApps
{
    /// <summary>
    /// One conversion of CONVERT: `--from json`, `--to json`, `--from lines`.
    /// A new one is a new file in Converters\ — the registry is generated
    /// from the file names (ConvertApp.csproj).
    /// </summary>
    internal interface IConverter
    {
        /// <summary>"from" or "to".</summary>
        string Direction { get; }

        /// <summary>The format's name: "json", "lines".</summary>
        string Format { get; }

        /// <summary>The options it takes after `--from x`/`--to x`, for the list of known ones.</summary>
        string Options { get; }

        /// <summary>Reads the standard input, writes the standard output; the exit code.</summary>
        int Run(string[] options);
    }
}
