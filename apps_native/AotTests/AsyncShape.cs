// Does async/await actually run, not merely compile?
//
// The compiler rewrites an async method into a state machine and looks its
// supporting types up by name: a builder, an awaiter, IAsyncStateMachine. Until
// Task existed the question could not even be asked — an async method failed on
// its return type first, so the rewriter never ran and the gap stayed hidden
// behind an earlier error.
//
// Compiling proves the shapes match. It says nothing about whether the
// continuation resumes, which is the part that matters and the part that can go
// silently wrong: a machine that never resumes leaves the awaiting task
// unfinished forever, and the caller waits on it.
internal static class AsyncShape
{
    internal static volatile int Stage;

    internal static async System.Threading.Tasks.Task RunAsync()
    {
        Stage = 1;
        await System.Threading.Tasks.Task.Delay(10);

        // Reached only if the continuation resumed after the await. Everything
        // before this line runs on the caller's thread and proves nothing.
        Stage = 2;
        await System.Threading.Tasks.Task.Delay(10);
        Stage = 3;
    }
}
