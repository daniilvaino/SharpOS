// What `await` needs, and only that.
//
// C# does not call an interface to await something: the compiler matches a
// shape. `await x` requires x.GetAwaiter() returning a type with IsCompleted,
// OnCompleted and GetResult; the method containing the await is then rewritten
// into a state machine, and the rewriter looks up ITS supporting types by name
// — AsyncTaskMethodBuilder, IAsyncStateMachine, and so on. Miss one and the
// compiler does not degrade, it refuses.
//
// Until Task existed, none of this could even be asked for: an async method
// failed on its return type first and the rewriter never ran. That is why this
// gap stayed invisible for so long — it was hidden behind an earlier error.
//
// The continuations run inline or on a thread, exactly like Task itself: there
// is no scheduler here and no synchronisation context to capture. For a TUI
// waiting on a delay that is the whole requirement; for anything expecting
// continuations to resume on a particular thread, it is not, and that limit is
// deliberate rather than forgotten.

using System.Runtime.CompilerServices;

namespace System.Threading.Tasks
{
    public partial class Task
    {
        public TaskAwaiter GetAwaiter() => new TaskAwaiter(this);
    }
}

namespace System.Runtime.CompilerServices
{
    /// <summary>Marks a method the compiler has rewritten into a state machine.</summary>
    public interface IAsyncStateMachine
    {
        void MoveNext();
        void SetStateMachine(IAsyncStateMachine stateMachine);
    }

    public interface INotifyCompletion
    {
        void OnCompleted(Action continuation);
    }

    public interface ICriticalNotifyCompletion : INotifyCompletion
    {
        void UnsafeOnCompleted(Action continuation);
    }

    /// <summary>
    /// The awaiter for Task. Completion is observed by waiting, because that is
    /// what this Task offers: a thread finishing its work.
    /// </summary>
    public readonly struct TaskAwaiter : ICriticalNotifyCompletion
    {
        private readonly System.Threading.Tasks.Task _task;

        internal TaskAwaiter(System.Threading.Tasks.Task task) { _task = task; }

        public bool IsCompleted => _task == null || _task.IsCompleted;

        public void GetResult() => _task?.Wait();

        public void OnCompleted(Action continuation) => Schedule(continuation);

        public void UnsafeOnCompleted(Action continuation) => Schedule(continuation);

        private void Schedule(Action continuation)
        {
            if (continuation == null) return;

            // Already done: no reason to involve a thread, and going through one
            // would turn every completed await into a context switch.
            if (IsCompleted) { continuation(); return; }

            var task = _task;
            System.Threading.Tasks.Task.Run(() =>
            {
                task.Wait();
                continuation();
            });
        }
    }

    /// <summary>
    /// The builder the rewriter drives: it creates the task an async method
    /// returns, advances the machine, and completes the task at the end.
    /// </summary>
    public struct AsyncTaskMethodBuilder
    {
        // Everything that must survive being copied lives behind this one
        // reference.
        //
        // Both the builder and the state machine are STRUCTS, and the compiler
        // copies them freely — into a closure, into a box, back out. The first
        // version copied each and paid for it twice: the continuation advanced a
        // copy of the machine, so the second await never happened; and the
        // caller got its task from the original builder while completion was
        // reported on a copy that had lazily made a task of its own. Two objects
        // where the design has one, and a caller waiting on the wrong half.
        private sealed class Shared
        {
            public readonly AsyncTask Task = new AsyncTask();
            public IAsyncStateMachine? Box;
        }

        private Shared _shared;

        public static AsyncTaskMethodBuilder Create()
            => new AsyncTaskMethodBuilder { _shared = new Shared() };

        public System.Threading.Tasks.Task Task => Ensure().Task;

        private Shared Ensure() => _shared ??= new Shared();

        public void Start<TStateMachine>(ref TStateMachine stateMachine)
            where TStateMachine : IAsyncStateMachine
            => stateMachine.MoveNext();

        public void SetStateMachine(IAsyncStateMachine stateMachine)
            => Ensure().Box = stateMachine;

        public void SetResult() => Ensure().Task.Complete(null);

        public void SetException(Exception exception) => Ensure().Task.Complete(exception);

        public void AwaitOnCompleted<TAwaiter, TStateMachine>(
            ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
            => Suspend(ref awaiter, ref stateMachine, unsafeVariant: false);

        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
            ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion
            where TStateMachine : IAsyncStateMachine
            => Suspend(ref awaiter, ref stateMachine, unsafeVariant: true);

        private void Suspend<TAwaiter, TStateMachine>(
            ref TAwaiter awaiter, ref TStateMachine stateMachine, bool unsafeVariant)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            Shared shared = Ensure();

            // Boxed once, on the first suspension, and reused for every later
            // one: a method with two awaits must resume the same instance both
            // times, or the second resumption starts from a machine that never
            // saw the first.
            shared.Box ??= stateMachine;

            // A lambda rather than the method group `box.MoveNext`: a delegate
            // bound directly to an INTERFACE method is not something this
            // runtime can build. Calling one through a closure is ordinary
            // interface dispatch, which works.
            IAsyncStateMachine box = shared.Box;
            Action resume = () => box.MoveNext();

            if (unsafeVariant && awaiter is ICriticalNotifyCompletion critical)
                critical.UnsafeOnCompleted(resume);
            else
                awaiter.OnCompleted(resume);
        }
    }

    /// <summary>
    /// The builder for `async void`.
    /// </summary>
    /// <remarks>
    /// An async void method has no task to hand back, so nobody can wait for it
    /// and nobody can observe its failure. That is a property of the shape, not
    /// of this implementation — but here it is worth stating twice, because
    /// there is no synchronisation context to re-raise the exception on. An
    /// exception escaping one of these is simply lost, exactly as SetException
    /// below says.
    /// </remarks>
    public struct AsyncVoidMethodBuilder
    {
        private sealed class Shared
        {
            public IAsyncStateMachine? Box;
        }

        private Shared _shared;

        public static AsyncVoidMethodBuilder Create()
            => new AsyncVoidMethodBuilder { _shared = new Shared() };

        private Shared Ensure() => _shared ??= new Shared();

        public void Start<TStateMachine>(ref TStateMachine stateMachine)
            where TStateMachine : IAsyncStateMachine
            => stateMachine.MoveNext();

        public void SetStateMachine(IAsyncStateMachine stateMachine) => Ensure().Box = stateMachine;

        public void SetResult() { }

        public void SetException(Exception exception) { /* nowhere to report it */ }

        public void AwaitOnCompleted<TAwaiter, TStateMachine>(
            ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
            => Suspend(ref awaiter, ref stateMachine);

        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(
            ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion
            where TStateMachine : IAsyncStateMachine
            => Suspend(ref awaiter, ref stateMachine);

        private void Suspend<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion
            where TStateMachine : IAsyncStateMachine
        {
            Shared shared = Ensure();
            shared.Box ??= stateMachine;

            IAsyncStateMachine box = shared.Box;
            awaiter.OnCompleted(() => box.MoveNext());
        }
    }

    /// <summary>
    /// A task completed by a builder rather than by a thread finishing. Task's
    /// own completion is private to it, so async results get their own door in
    /// rather than a public setter everyone could push.
    /// </summary>
    internal sealed class AsyncTask : System.Threading.Tasks.Task
    {
        internal void Complete(Exception error) => CompleteFromBuilder(error);
    }
}
