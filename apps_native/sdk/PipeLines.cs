using System;
using SharpOS.AppSdk;

namespace SharpOS.Std.Pipes
{
    /// <summary>A pipeline started from code ended with a code other than 0: the stage that failed, and its code.</summary>
    public sealed class PipelineException : Exception
    {
        public PipelineException(string pipeline, string stage, int exitCode)
            : base("the pipeline '" + pipeline + "' failed: " + (stage ?? "a stage") + " exited with " + exitCode.ToString())
        {
            Pipeline = pipeline;
            Stage = stage;
            ExitCode = exitCode;
        }

        public string Pipeline { get; }
        public string Stage { get; }
        public int ExitCode { get; }
    }

    // A pipeline from code, written as in the shell (step197):
    //
    //     foreach (LogLine e in Pipe.From("READ log.json | CONVERT --from json").Into<LogLine>()) …
    //     Pipe.Read("myapp.log").WriteTo(Pipe.To("CONVERT --to json | WRITE errors.json"));
    //
    // The shell runs the line (SHELL.EXE -c): the parse and the start are its
    // own, not a second copy. Its standard output is the reader From gives,
    // its standard input the end To gives. The end of reading or writing
    // waits for the shell, which waits for every stage; a code other than 0
    // throws PipelineException with the failed stage's name, which the shell
    // sends back over a pipe of its own (sh -c LINE --report NAME).
    public static partial class Pipe
    {
        private static int s_lines;

        /// <summary>Starts <paramref name="pipeline"/> and gives its last stage's output, read through views.</summary>
        public static RawPipeReader From(string pipeline)
        {
            PipePair output = Create();
            Started shell = Start(pipeline, input: null, output: output.WriteEnd);
            RawPipeReader reader = output.ReadEnd.Read();
            reader.Closed = shell.Finish;
            return reader;
        }

        /// <summary>Starts <paramref name="pipeline"/> and gives its first stage's input, for WriteTo or Write&lt;T&gt;.</summary>
        public static PipeWriteEnd To(string pipeline)
        {
            PipePair input = Create();
            Started shell = Start(pipeline, input: input.ReadEnd, output: null);
            input.WriteEnd.Closed = shell.Finish;
            return input.WriteEnd;
        }

        private static Started Start(string pipeline, PipeReadEnd input, PipeWriteEnd output)
        {
            if (pipeline == null) throw new ArgumentNullException(nameof(pipeline));
            string name = "sh-report-" + Process.CurrentId.ToString() + "-" + (++s_lines).ToString();
            PipeReader<string> report = null;
            if (PipeReader<string>.Connect(name, out PipeReader<string> r, out _) == PipeStatus.Ok) report = r;
            Process shell;
            try
            {
                shell = Process.Start("\\apps\\SHELL.EXE", new[] { "-c", pipeline, "--report", name }, input, output);
            }
            catch
            {
                report?.Dispose();
                input?.Dispose();
                output?.Dispose();
                throw;
            }
            return new Started(pipeline, shell, report);
        }

        private sealed class Started
        {
            private readonly string _pipeline;
            private Process _shell;
            private readonly PipeReader<string> _report;

            internal Started(string pipeline, Process shell, PipeReader<string> report)
            {
                _pipeline = pipeline;
                _shell = shell;
                _report = report;
            }

            // Every stage gone; a failure named.
            internal void Finish()
            {
                Process shell = _shell;
                if (shell == null) return;
                _shell = null;
                shell.WaitForExit();
                int code = shell.ExitCode;
                shell.Dispose();
                string stage = null;
                if (_report != null)
                {
                    if (code != 0)
                    {
                        if (_report.TryReceive(out Region<string> said) == PipeStatus.Ok && said != null)
                        {
                            using (said) stage = said.ToHeap();
                        }
                    }
                    _report.Dispose();
                }
                if (code != 0) throw new PipelineException(_pipeline, stage, code);
            }
        }
    }
}
