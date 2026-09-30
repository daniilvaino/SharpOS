// Action / Func delegate type declarations. Bodies (ctor, Invoke, GetThunk
// override, thunks) are synthesised by ILC — these are pure declarations, the
// same as in dotnet/runtime (System/Action.cs, System/Func.cs). Variance
// annotations (in/out) match the BCL: they set the delegate EEType's
// GenericVariance flag. Variant CONVERSION (Func<string,bool> -> Func<object,
// bool>) is a separate castclass concern, deferred (plan phase 3 §11).
//
// Arities 0..4 cover the smoke matrix and DOOM's needs; extend as required.

namespace System
{
    public delegate void Action();
    public delegate void Action<in T>(T obj);
    public delegate void Action<in T1, in T2>(T1 arg1, T2 arg2);
    public delegate void Action<in T1, in T2, in T3>(T1 arg1, T2 arg2, T3 arg3);
    public delegate void Action<in T1, in T2, in T3, in T4>(T1 arg1, T2 arg2, T3 arg3, T4 arg4);

    public delegate TResult Func<out TResult>();
    public delegate TResult Func<in T, out TResult>(T arg);
    public delegate TResult Func<in T1, in T2, out TResult>(T1 arg1, T2 arg2);
    public delegate TResult Func<in T1, in T2, in T3, out TResult>(T1 arg1, T2 arg2, T3 arg3);
    public delegate TResult Func<in T1, in T2, in T3, in T4, out TResult>(T1 arg1, T2 arg2, T3 arg3, T4 arg4);

    // Predicate<T> — used by Array/List search helpers; same shape as BCL.
    public delegate bool Predicate<in T>(T obj);
}
