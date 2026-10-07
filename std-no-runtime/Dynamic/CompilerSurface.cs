// The surface the C# compiler lowers `dynamic` onto (Roslyn,
// LoweredDynamicOperationFactory): the names, signatures and enum values of
// .NET 8.0.27 — the compiler looks them up by name and writes the enums as
// numbers, so these must match exactly. Sources: dotnet/runtime release/8.0,
// System.Linq.Expressions (CallSite, CallSiteBinder, ExpressionType) and
// Microsoft.CSharp (Binder, CSharpArgumentInfo, the flags,
// RuntimeBinderException).
//
// A cut, not a port: no expression trees, no DLR rules, no reflection. A
// binder here only records what the call site asked for; a call site's Target
// is a thunk the image's generator emitted for that delegate type, and the
// binding is done by SharpOS.Std.Dynamic.DynamicRuntime. The canonical names
// are kept because the compiler requires them (invariant 2 names this the
// case where a partial type in System.* is unavoidable); only the members the
// compiler emits exist.

using System;
using System.Collections.Generic;
using SharpOS.Std.Dynamic;

namespace System.Runtime.CompilerServices
{
    /// <summary>A dynamic call site: the binder the compiler made for it.</summary>
    public class CallSite
    {
        internal readonly CallSiteBinder _binder;
        internal object _rules;   // SharpOS.Std.Dynamic.Rule: the bindings kept for this site
        // An argument array for a kept binding with no body of its own for
        // 1-3 operands (step196); _spareBusy is its lock (an atomic int: the
        // reference forms of Interlocked are not atomic here).
        internal object[] _spare;
        internal int _spareBusy;

        internal CallSite(CallSiteBinder binder) => _binder = binder;

        public CallSiteBinder Binder => _binder;
    }

    /// <summary>A dynamic call site of a given delegate type; <see cref="Target"/> is what the compiler calls.</summary>
    public class CallSite<T> : CallSite where T : class
    {
        public T Target = default!;

        private CallSite(CallSiteBinder binder) : base(binder) { }

        /// <summary>The thunk for <typeparamref name="T"/> from the image's registry (emitted by the generator).</summary>
        public static CallSite<T> Create(CallSiteBinder binder)
        {
            var site = new CallSite<T>(binder);
            site.Target = System.Runtime.CompilerServices.Unsafe.As<T>(CallSiteThunks.Find(typeof(T)));
            return site;
        }
    }

    /// <summary>What binds a call site. Here: the record of what it asked for (SharpOS.Std.Dynamic.DynamicBinder).</summary>
    public abstract class CallSiteBinder
    {
        protected CallSiteBinder() { }
    }

    /// <summary>Marks where `dynamic` stands for object in a signature.</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Parameter | AttributeTargets.Property | AttributeTargets.ReturnValue
                    | AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
    public sealed class DynamicAttribute : Attribute
    {
        public DynamicAttribute() { }
        public DynamicAttribute(bool[] transformFlags) { }
    }
}

namespace System.Linq.Expressions
{
    /// <summary>The operations of a dynamic binary or unary call site (values as in .NET: the compiler writes numbers).</summary>
    public enum ExpressionType
    {
        Add, AddChecked, And, AndAlso, ArrayLength, ArrayIndex, Call, Coalesce, Conditional, Constant,
        Convert, ConvertChecked, Divide, Equal, ExclusiveOr, GreaterThan, GreaterThanOrEqual, Invoke, Lambda,
        LeftShift, LessThan, LessThanOrEqual, ListInit, MemberAccess, MemberInit, Modulo, Multiply,
        MultiplyChecked, Negate, UnaryPlus, NegateChecked, New, NewArrayInit, NewArrayBounds, Not, NotEqual,
        Or, OrElse, Parameter, Power, Quote, RightShift, Subtract, SubtractChecked, TypeAs, TypeIs, Assign,
        Block, DebugInfo, Decrement, Dynamic, Default, Extension, Goto, Increment, Index, Label,
        RuntimeVariables, Loop, Switch, Throw, Try, Unbox, AddAssign, AndAssign, DivideAssign,
        ExclusiveOrAssign, LeftShiftAssign, ModuloAssign, MultiplyAssign, OrAssign, PowerAssign,
        RightShiftAssign, SubtractAssign, AddAssignChecked, MultiplyAssignChecked, SubtractAssignChecked,
        PreIncrementAssign, PreDecrementAssign, PostIncrementAssign, PostDecrementAssign, TypeEqual,
        OnesComplement, IsTrue, IsFalse,
    }
}

namespace Microsoft.CSharp.RuntimeBinder
{
    [Flags]
    public enum CSharpBinderFlags
    {
        None = 0x00000000,
        CheckedContext = 0x00000001,
        InvokeSimpleName = 0x00000002,
        InvokeSpecialName = 0x00000004,
        BinaryOperationLogical = 0x00000008,
        ConvertExplicit = 0x00000010,
        ConvertArrayIndex = 0x00000020,
        ResultIndexed = 0x00000040,
        ValueFromCompoundAssignment = 0x00000080,
        ResultDiscarded = 0x00000100,
    }

    [Flags]
    public enum CSharpArgumentInfoFlags
    {
        None = 0x00000000,
        UseCompileTimeType = 0x00000001,
        Constant = 0x00000002,
        NamedArgument = 0x00000004,
        IsRef = 0x00000008,
        IsOut = 0x00000010,
        IsStaticType = 0x00000020,
    }

    /// <summary>One argument of a dynamic call site, as the compiler describes it.</summary>
    public sealed class CSharpArgumentInfo
    {
        internal readonly CSharpArgumentInfoFlags Flags;
        internal readonly string? Name;

        private CSharpArgumentInfo(CSharpArgumentInfoFlags flags, string? name)
        {
            Flags = flags;
            Name = name;
        }

        public static CSharpArgumentInfo Create(CSharpArgumentInfoFlags flags, string? name) => new CSharpArgumentInfo(flags, name);
    }

    /// <summary>A dynamic operation that cannot bind: no member, no overload, no conversion.</summary>
    public class RuntimeBinderException : Exception
    {
        public RuntimeBinderException() { }
        public RuntimeBinderException(string? message) : base(message) { }
        public RuntimeBinderException(string? message, Exception? innerException) : base(message, innerException) { }
    }

    /// <summary>The binder factories the compiler calls, one per kind of dynamic operation.</summary>
    public static class Binder
    {
        public static System.Runtime.CompilerServices.CallSiteBinder BinaryOperation(
            CSharpBinderFlags flags, System.Linq.Expressions.ExpressionType operation, Type? context,
            IEnumerable<CSharpArgumentInfo>? argumentInfo)
            => new DynamicBinder(DynamicOperation.Binary, flags, null, operation, null, null, context, argumentInfo);

        public static System.Runtime.CompilerServices.CallSiteBinder Convert(CSharpBinderFlags flags, Type type, Type? context)
            => new DynamicBinder(DynamicOperation.Convert, flags, null, default, type, null, context, null);

        public static System.Runtime.CompilerServices.CallSiteBinder GetIndex(
            CSharpBinderFlags flags, Type? context, IEnumerable<CSharpArgumentInfo>? argumentInfo)
            => new DynamicBinder(DynamicOperation.GetIndex, flags, null, default, null, null, context, argumentInfo);

        public static System.Runtime.CompilerServices.CallSiteBinder GetMember(
            CSharpBinderFlags flags, string name, Type? context, IEnumerable<CSharpArgumentInfo>? argumentInfo)
            => new DynamicBinder(DynamicOperation.GetMember, flags, name, default, null, null, context, argumentInfo);

        public static System.Runtime.CompilerServices.CallSiteBinder Invoke(
            CSharpBinderFlags flags, Type? context, IEnumerable<CSharpArgumentInfo>? argumentInfo)
            => new DynamicBinder(DynamicOperation.Invoke, flags, null, default, null, null, context, argumentInfo);

        public static System.Runtime.CompilerServices.CallSiteBinder InvokeMember(
            CSharpBinderFlags flags, string name, IEnumerable<Type>? typeArguments, Type? context,
            IEnumerable<CSharpArgumentInfo>? argumentInfo)
            => new DynamicBinder(DynamicOperation.InvokeMember, flags, name, default, null, typeArguments, context, argumentInfo);

        public static System.Runtime.CompilerServices.CallSiteBinder InvokeConstructor(
            CSharpBinderFlags flags, Type? context, IEnumerable<CSharpArgumentInfo>? argumentInfo)
            => new DynamicBinder(DynamicOperation.InvokeConstructor, flags, null, default, null, null, context, argumentInfo);

        public static System.Runtime.CompilerServices.CallSiteBinder IsEvent(CSharpBinderFlags flags, string name, Type? context)
            => new DynamicBinder(DynamicOperation.IsEvent, flags, name, default, null, null, context, null);

        public static System.Runtime.CompilerServices.CallSiteBinder SetIndex(
            CSharpBinderFlags flags, Type? context, IEnumerable<CSharpArgumentInfo>? argumentInfo)
            => new DynamicBinder(DynamicOperation.SetIndex, flags, null, default, null, null, context, argumentInfo);

        public static System.Runtime.CompilerServices.CallSiteBinder SetMember(
            CSharpBinderFlags flags, string name, Type? context, IEnumerable<CSharpArgumentInfo>? argumentInfo)
            => new DynamicBinder(DynamicOperation.SetMember, flags, name, default, null, null, context, argumentInfo);

        public static System.Runtime.CompilerServices.CallSiteBinder UnaryOperation(
            CSharpBinderFlags flags, System.Linq.Expressions.ExpressionType operation, Type? context,
            IEnumerable<CSharpArgumentInfo>? argumentInfo)
            => new DynamicBinder(DynamicOperation.Unary, flags, null, operation, null, null, context, argumentInfo);
    }
}

namespace SharpOS.Std.Dynamic
{
    /// <summary>What kind of dynamic operation a call site is.</summary>
    public enum DynamicOperation : byte
    {
        Binary, Unary, Convert, GetMember, SetMember, GetIndex, SetIndex, Invoke, InvokeMember, InvokeConstructor, IsEvent,
    }

    /// <summary>A call site's request, as the compiler described it; bound by DynamicRuntime.</summary>
    public sealed class DynamicBinder : System.Runtime.CompilerServices.CallSiteBinder
    {
        public readonly DynamicOperation Operation;
        public readonly Microsoft.CSharp.RuntimeBinder.CSharpBinderFlags Flags;
        public readonly string Name;
        public readonly System.Linq.Expressions.ExpressionType Expression;
        public readonly Type ConvertTo;
        public readonly Type[] TypeArguments;
        public readonly Type Context;
        public readonly Microsoft.CSharp.RuntimeBinder.CSharpArgumentInfo[] Arguments;
        internal readonly uint StaticMask;   // bit i: argument i is a type (a static receiver)

        internal DynamicBinder(DynamicOperation operation, Microsoft.CSharp.RuntimeBinder.CSharpBinderFlags flags, string name,
                               System.Linq.Expressions.ExpressionType expression, Type convertTo, IEnumerable<Type> typeArguments,
                               Type context, IEnumerable<Microsoft.CSharp.RuntimeBinder.CSharpArgumentInfo> arguments)
        {
            Operation = operation;
            Flags = flags;
            Name = name;
            Expression = expression;
            ConvertTo = convertTo;
            TypeArguments = ToArray(typeArguments);
            Context = context;
            Arguments = ToArray(arguments);
            for (int i = 0; i < Arguments.Length && i < 32; i++)
                if ((Arguments[i].Flags & Microsoft.CSharp.RuntimeBinder.CSharpArgumentInfoFlags.IsStaticType) != 0)
                    StaticMask |= 1u << i;
        }

        internal Microsoft.CSharp.RuntimeBinder.CSharpArgumentInfoFlags FlagsOf(int i)
            => i < Arguments.Length ? Arguments[i].Flags : Microsoft.CSharp.RuntimeBinder.CSharpArgumentInfoFlags.None;

        internal bool IsStaticType(int i) => (FlagsOf(i) & Microsoft.CSharp.RuntimeBinder.CSharpArgumentInfoFlags.IsStaticType) != 0;

        private static TItem[] ToArray<TItem>(IEnumerable<TItem> items)
        {
            if (items == null) return new TItem[0];
            var list = new List<TItem>();
            foreach (TItem item in items) list.Add(item);
            return list.ToArray();
        }
    }

    /// <summary>
    /// The call-site thunks of this image, by delegate type: one per
    /// Func/Action shape the compiler emitted for a dynamic operation. Filled
    /// by the generator (RegisterGenerated).
    /// </summary>
    public static partial class CallSiteThunks
    {
        private static Dictionary<Type, object> s_thunks;
        private static Dictionary<Type, object> s_filling;
        private static object s_lock;

        static partial void RegisterGenerated();

        public static void Register(Type delegateType, object thunk) => s_filling[delegateType] = thunk;

        internal static object Find(Type delegateType)
        {
            if (s_thunks == null)
            {
                if (s_lock == null) System.Threading.Interlocked.CompareExchange(ref s_lock, new object(), null);
                lock (s_lock)
                {
                    if (s_thunks == null)
                    {
                        s_filling = new Dictionary<Type, object>();
                        RegisterGenerated();
                        s_thunks = s_filling;   // published whole
                    }
                }
            }
            if (s_thunks.TryGetValue(delegateType, out object thunk)) return thunk;
            throw new Microsoft.CSharp.RuntimeBinder.RuntimeBinderException(
                "no call-site thunk for this delegate type in the image: the dynamic generator missed a call site");
        }
    }
}
