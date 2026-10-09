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
using System.Threading.Tasks;

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

    // Results and async streams (step198, for BabyKusto): Task<T>, ValueTask<T>
    // finishing with and without suspending, and an async iterator consumed by
    // await foreach — each resumes only if its own builder works.
    internal static async System.Threading.Tasks.Task<int> AddAsync(int a, int b)
    {
        await System.Threading.Tasks.Task.Delay(5);
        return a + b;
    }

    internal static async System.Threading.Tasks.ValueTask<int> TwiceAsync(int x, bool suspend)
    {
        if (suspend)
            await System.Threading.Tasks.Task.Delay(5);
        return 2 * x;
    }

    internal static async System.Collections.Generic.IAsyncEnumerable<int> CountAsync(int n,
        [System.Runtime.CompilerServices.EnumeratorCancellation] System.Threading.CancellationToken token = default)
    {
        for (int i = 1; i <= n; i++)
        {
            token.ThrowIfCancellationRequested();
            await System.Threading.Tasks.Task.Delay(1);
            yield return i;
        }
    }

    internal static async System.Threading.Tasks.Task<int> SumAsync(int n)
    {
        int sum = 0;
        await foreach (int i in CountAsync(n))
            sum += i;
        return sum;
    }

    // Stops after `stopAt` items through WithCancellation: the token reaches
    // the iterator by [EnumeratorCancellation]; returns the items seen.
    internal static async System.Threading.Tasks.Task<int> CancelAsync(int stopAt)
    {
        var source = new System.Threading.CancellationTokenSource();
        int seen = 0;
        try
        {
            await foreach (int i in CountAsync(100).WithCancellation(source.Token))
            {
                seen++;
                if (seen == stopAt) source.Cancel();
            }
        }
        catch (System.OperationCanceledException)
        {
        }
        return seen;
    }
}
