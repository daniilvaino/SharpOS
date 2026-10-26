// SharpOS cut: no ITimer/TimerCallback in std; nothing in the engine uses this factory.
#if !SHARPOS
namespace Okojo.JavaScript.Execution;

internal interface ITimerFactory
{
    ITimer CreateJsTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period);
    ITimer CreateWaitTimer(TimerCallback callback, object? state, TimeSpan dueTime);
}
#endif
