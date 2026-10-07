// The binder of `dynamic`: what a call site asked for (DynamicBinder), the
// values it got and their compile-time types, bound by the C# rules against
// what the value answers for itself (IDynamicObject), the predefined types and
// the member tables the image's generator wrote. Messages follow
// Microsoft.CSharp's.
//
// A binding that depends on the types alone is kept on the call site as a rule
// keyed by the arguments' type tables (and, for a view, by its shape): the
// next call with the same types runs the rule without asking a name again. A
// rule list is immutable and replaced by one reference write, so a call site
// shared by preempted threads at worst binds twice.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.CSharp.RuntimeBinder;
using SharpOS.Std.NoRuntime;
using Expr = System.Linq.Expressions.ExpressionType;

namespace SharpOS.Std.Dynamic
{
    /// <summary>One argument as binding sees it.</summary>
    internal struct Arg
    {
        public object Value;
        public Type Type;          // the type binding uses: compile-time when asked, else the value's; null: a null with no type
        public bool Constant;
        public bool IsStaticType;
    }

    /// <summary>A binding kept on a call site.</summary>
    internal sealed class Rule
    {
        internal ulong[] Keys;
        internal object Shape;
        internal Func<object[], object> Body;
        // The same binding for one or two operands without an argument array
        // (step196); null: Body, with the call site's spare array.
        internal Func<object, object> Body1;
        internal Func<object, object, object> Body2;
        internal Rule Next;
        internal int Depth;

        internal bool Matches(object[] args, DynamicBinder binder)
        {
            ulong[] keys = Keys;
            uint statics = binder.StaticMask;
            for (int i = 0; i < keys.Length; i++)
            {
                ulong key = (statics & (1u << i)) != 0 ? (ulong)((Type)args[i])._handle : DynamicTypes.TableOf(args[i]);
                if (key != keys[i]) return false;
            }
            if (Shape != null)
                return args[0] is IDynamicShape s && ReferenceEquals(s.Shape, Shape);
            return true;
        }

        // The same test for 1-3 operands, without an array.
        internal bool Matches(int n, object a0, object a1, object a2, DynamicBinder binder)
        {
            ulong[] keys = Keys;
            if (keys.Length != n) return false;
            uint statics = binder.StaticMask;
            if (Key(a0, 0, statics) != keys[0]) return false;
            if (n > 1 && Key(a1, 1, statics) != keys[1]) return false;
            if (n > 2 && Key(a2, 2, statics) != keys[2]) return false;
            if (Shape != null)
                return a0 is IDynamicShape s && ReferenceEquals(s.Shape, Shape);
            return true;
        }

        private static ulong Key(object a, int i, uint statics)
            => (statics & (1u << i)) != 0 ? (ulong)((Type)a)._handle : DynamicTypes.TableOf(a);

        internal static ulong KeyOf(object[] args, int i, DynamicBinder binder)
        {
            if (binder.IsStaticType(i)) return (ulong)((Type)args[i])._handle;
            return DynamicTypes.TableOf(args[i]);
        }
    }

    /// <summary>The expanded tail of a `params` call: the generated invoker builds the typed array from it.</summary>
    public sealed class ParamsPack
    {
        public readonly object[] Items;
        internal ParamsPack(object[] items) => Items = items;
    }

    public static class DynamicRuntime
    {
        /// <summary>Names looked up: in the member tables, in a shape, or asked of a value that answers for itself.</summary>
        public static int Lookups;

        private static void Looked() => Interlocked.Increment(ref Lookups);

        private const int MaxRules = 8;

        /// <summary>What every call-site thunk calls: receiver, arguments and right-hand side in order, with their static types.</summary>
        public static object Run(CallSite site, object[] args, Type[] types)
        {
            DynamicBinder binder = Unsafe.As<DynamicBinder>(site._binder);
            for (Rule r = Unsafe.As<Rule>(site._rules); r != null; r = r.Next)
                if (r.Matches(args, binder)) return r.Body(args);

            Arg[] a = Describe(binder, args, types);
            Rule rule = Bind(binder, a, out object result, out object shape);
            if (rule == null) return result;
            // A value that answers for itself may answer differently next time: a binding made
            // after it declined (its real members) is not kept, unless keyed by its shape.
            if (shape == null && args.Length > 0 && args[0] is IDynamicObject && AsksTheValue(binder.Operation))
                return rule.Body(args);

            rule.Keys = new ulong[args.Length];
            for (int i = 0; i < args.Length; i++) rule.Keys[i] = Rule.KeyOf(args, i, binder);
            rule.Shape = shape;
            Rule old = Unsafe.As<Rule>(site._rules);
            rule.Next = old != null && old.Depth < MaxRules ? old : null;
            rule.Depth = rule.Next == null ? 1 : rule.Next.Depth + 1;
            site._rules = rule;
            return rule.Body(args);
        }

        // ---- call sites of one to three operands (step196) ----
        //
        // The generated thunk passes the operands as they are: a kept binding
        // runs on them directly when it has a body of that many operands (a
        // member read or write, a conversion, an operator on numbers), else on
        // the site's spare array. A member of a value that answers for itself
        // (an Expando) is asked directly too — such a binding is never kept.
        // Only a site's first call, or a new type at it, builds the array.

        public static object Run1(CallSite site, object a0, Type[] types)
        {
            DynamicBinder binder = Unsafe.As<DynamicBinder>(site._binder);
            for (Rule r = Unsafe.As<Rule>(site._rules); r != null; r = r.Next)
                if (r.Matches(1, a0, null, null, binder))
                    return r.Body1 != null ? r.Body1(a0) : Spare(site, r, 1, a0, null, null);
            if (binder.Operation == DynamicOperation.GetMember && binder.StaticMask == 0 && Answers(a0, out IDynamicObject self)
                && Asked() && self.TryGetMember(binder.Name, out object value))
                return value;
            return Run(site, new object[] { a0 }, types);
        }

        public static object Run2(CallSite site, object a0, object a1, Type[] types)
        {
            DynamicBinder binder = Unsafe.As<DynamicBinder>(site._binder);
            for (Rule r = Unsafe.As<Rule>(site._rules); r != null; r = r.Next)
                if (r.Matches(2, a0, a1, null, binder))
                    return r.Body2 != null ? r.Body2(a0, a1) : Spare(site, r, 2, a0, a1, null);
            if (binder.Operation == DynamicOperation.SetMember && binder.StaticMask == 0 && Answers(a0, out IDynamicObject self)
                && Asked() && self.TrySetMember(binder.Name, a1))
                return a1;
            return Run(site, new object[] { a0, a1 }, types);
        }

        public static object Run3(CallSite site, object a0, object a1, object a2, Type[] types)
        {
            DynamicBinder binder = Unsafe.As<DynamicBinder>(site._binder);
            for (Rule r = Unsafe.As<Rule>(site._rules); r != null; r = r.Next)
                if (r.Matches(3, a0, a1, a2, binder))
                    return Spare(site, r, 3, a0, a1, a2);
            return Run(site, new object[] { a0, a1, a2 }, types);
        }

        // A value that answers for itself and has no shape: Bind would ask it first.
        private static bool Answers(object a0, out IDynamicObject self)
        {
            self = a0 as IDynamicObject;
            return self != null && !(a0 is IDynamicShape shaped && shaped.Shape != null);
        }

        // A kept binding's Body on the site's spare array; a new one when the
        // spare is in use (a body that reaches the same site again).
        private static object Spare(CallSite site, Rule r, int n, object a0, object a1, object a2)
        {
            bool mine = Interlocked.CompareExchange(ref site._spareBusy, 1, 0) == 0;
            object[] args = mine ? site._spare : null;
            if (args == null || args.Length != n)
            {
                args = new object[n];
                if (mine) site._spare = args;
            }
            args[0] = a0;
            if (n > 1) args[1] = a1;
            if (n > 2) args[2] = a2;
            try
            {
                return r.Body(args);
            }
            finally
            {
                if (mine)
                {
                    args[0] = null;
                    if (n > 1) args[1] = null;
                    if (n > 2) args[2] = null;
                    site._spareBusy = 0;
                }
            }
        }

        private static object Bool(bool value) => BoolBox.Of(value);

        private static bool AsksTheValue(DynamicOperation op)
            => op == DynamicOperation.GetMember || op == DynamicOperation.SetMember || op == DynamicOperation.GetIndex
            || op == DynamicOperation.SetIndex || op == DynamicOperation.InvokeMember || op == DynamicOperation.Convert;

        private static Arg[] Describe(DynamicBinder binder, object[] args, Type[] types)
        {
            var a = new Arg[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                CSharpArgumentInfoFlags flags = binder.FlagsOf(i);
                a[i].Value = args[i];
                a[i].Constant = (flags & CSharpArgumentInfoFlags.Constant) != 0;
                a[i].IsStaticType = (flags & CSharpArgumentInfoFlags.IsStaticType) != 0;
                if (a[i].IsStaticType)
                    a[i].Type = (Type)args[i];
                else if ((flags & (CSharpArgumentInfoFlags.UseCompileTimeType | CSharpArgumentInfoFlags.Constant)) != 0
                         && types[i] != typeof(object))
                    a[i].Type = types[i];
                else
                    a[i].Type = DynamicTypes.Of(args[i]);
            }
            return a;
        }

        // A rule, or null with the result already made (a value that answered for itself).
        private static Rule Bind(DynamicBinder b, Arg[] a, out object result, out object shape)
        {
            result = null;
            shape = null;
            switch (b.Operation)
            {
                case DynamicOperation.GetMember: return GetMember(b, a, out result, out shape);
                case DynamicOperation.SetMember: return SetMember(b, a, out result, out shape);
                case DynamicOperation.GetIndex: return GetIndex(b, a, out result);
                case DynamicOperation.SetIndex: return SetIndex(b, a, out result);
                case DynamicOperation.InvokeMember: return InvokeMember(b, a, out result);
                case DynamicOperation.Invoke: return Invoke(b, a, out result);
                case DynamicOperation.InvokeConstructor: return InvokeConstructor(b, a);
                case DynamicOperation.Binary: return Binary(b, a);
                case DynamicOperation.Unary: return Unary(b, a);
                case DynamicOperation.Convert: return Convert(b, a, out result);
                case DynamicOperation.IsEvent: return Make(static _ => Bool(false), static (object _) => Bool(false));   // no events are bound: `+=` is always arithmetic
            }
            throw DynamicTypes.Error("unknown dynamic operation");
        }

        private static Rule Make(Func<object[], object> body) => new Rule { Body = body };
        private static Rule Make(Func<object[], object> body, Func<object, object> body1) => new Rule { Body = body, Body1 = body1 };
        private static Rule Make(Func<object[], object> body, Func<object, object, object> body2) => new Rule { Body = body, Body2 = body2 };

        private static bool Asked()
        {
            Looked();
            return true;
        }

        // ---- names for messages ----

        private static string N(Type t) => DynamicMembers.NameOf(t);

        private static string Signature(DynamicMethod m)
        {
            string name = m.Name == ".ctor" ? N(m.Owner) : N(m.Owner) + "." + m.Name;
            string s = name + "(";
            for (int i = 0; i < m.Parameters.Length; i++)
            {
                if (i > 0) s += ", ";
                if (m.HasParamsArray && i == m.Parameters.Length - 1) s += "params ";
                s += N(m.Parameters[i]);
            }
            return s + ")";
        }

        private static RuntimeBinderException NoDefinition(Type t, string name)
            => DynamicTypes.Error("'" + N(t) + "' does not contain a definition for '" + name + "'");

        private static RuntimeBinderException NullReceiver()
            => DynamicTypes.Error("Cannot perform runtime binding on a null reference");

        // ---- members ----

        private static DynamicMember FindMember(Type t, string name, bool isStatic)
        {
            Looked();
            List<DynamicMember> list = DynamicMembers.Members(name);
            if (list == null) return null;
            DynamicMember best = null;
            for (int i = 0; i < list.Count; i++)
            {
                DynamicMember m = list[i];
                if (m.IsStatic != isStatic) continue;
                bool fits = isStatic ? m.Owner == t || DynamicTypes.IsSubclass(t, m.Owner) : Receives(t, m.Owner);
                if (!fits) continue;
                // The most derived declaration hides the others.
                if (best == null || DynamicTypes.IsSubclass(m.Owner, best.Owner)) best = m;
            }
            return best;
        }

        // An instance member of `owner` applies to a receiver bound as `t`.
        private static bool Receives(Type t, Type owner)
        {
            if (DynamicTypes.IsInterface(owner)) return t == owner;
            return DynamicTypes.IsAssignable(t, owner);
        }

        private static Rule GetMember(DynamicBinder b, Arg[] a, out object result, out object shape)
        {
            result = null;
            shape = null;
            string name = b.Name;
            if (a[0].IsStaticType)
            {
                DynamicMember s = FindMember(a[0].Type, name, true);
                if (s == null || s.Get == null) throw NoDefinition(a[0].Type, name);
                return Make(args => s.Get(null), _ => s.Get(null));
            }
            object receiver = a[0].Value;
            if (receiver == null) throw NullReceiver();
            if (receiver is IDynamicShape shaped && shaped.Shape != null && Asked() && shaped.TryCell(name, out object cell))
            {
                shape = shaped.Shape;
                return Make(args => Unsafe.As<IDynamicShape>(args[0]).GetCell(cell), a0 => Unsafe.As<IDynamicShape>(a0).GetCell(cell));
            }
            if (receiver is IDynamicObject self && Asked() && self.TryGetMember(name, out result)) return null;

            DynamicMember m = FindMember(a[0].Type, name, false);
            if (m == null)
            {
                if (HasMethod(a[0].Type, name))
                    throw DynamicTypes.Error("Cannot convert method group '" + name + "' to non-delegate type 'object'. Did you intend to invoke the method?");
                throw NoDefinition(a[0].Type, name);
            }
            if (m.Get == null)
                throw DynamicTypes.Error("The property or indexer '" + N(m.Owner) + "." + name + "' cannot be used in this context because it lacks the get accessor");
            Func<object, object> get = m.Get;
            return Make(args => get(args[0]), get);
        }

        private static bool HasMethod(Type t, string name)
        {
            List<DynamicMethod> list = DynamicMembers.Methods(name);
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
                if (Receives(t, list[i].Owner)) return true;
            return false;
        }

        private static Rule SetMember(DynamicBinder b, Arg[] a, out object result, out object shape)
        {
            result = null;
            shape = null;
            string name = b.Name;
            bool compound = (b.Flags & CSharpBinderFlags.ValueFromCompoundAssignment) != 0;
            bool isChecked = (b.Flags & CSharpBinderFlags.CheckedContext) != 0;
            DynamicMember m;
            if (a[0].IsStaticType)
            {
                m = FindMember(a[0].Type, name, true);
                if (m == null) throw NoDefinition(a[0].Type, name);
            }
            else
            {
                object receiver = a[0].Value;
                if (receiver == null) throw NullReceiver();
                if (receiver is IDynamicShape shaped && shaped.Shape != null && Asked() && shaped.TryCell(name, out object cell))
                {
                    shape = shaped.Shape;
                    return Make(args =>
                    {
                        Unsafe.As<IDynamicShape>(args[0]).SetCell(cell, args[1]);
                        return args[1];
                    }, (a0, a1) =>
                    {
                        Unsafe.As<IDynamicShape>(a0).SetCell(cell, a1);
                        return a1;
                    });
                }
                if (receiver is IDynamicObject self && Asked() && self.TrySetMember(name, a[1].Value))
                {
                    result = a[1].Value;
                    return null;
                }
                m = FindMember(a[0].Type, name, false);
                if (m == null) throw NoDefinition(a[0].Type, name);
            }
            if (m.Set == null)
                throw DynamicTypes.Error("Property or indexer '" + N(m.Owner) + "." + name + "' cannot be assigned to -- it is read only");
            Action<object, object> set = m.Set;
            Type to = m.Type;
            Arg value = a[1];
            CheckAssignable(value, to, compound);
            return Make(args =>
            {
                Arg v = value;
                v.Value = args[1];
                object converted = Assign(v, to, compound, isChecked);
                set(args[0], converted);
                return converted;
            }, (a0, a1) =>
            {
                Arg v = value;
                v.Value = a1;
                object converted = Assign(v, to, compound, isChecked);
                set(a0, converted);
                return converted;
            });
        }

        // An assignment's conversion: implicit; a compound assignment may also narrow a number (§12.21.4).
        private static void CheckAssignable(in Arg value, Type to, bool compound)
        {
            if (CanImplicit(value, to)) return;
            if (compound && value.Type != null && IsNumberLike(value.Type) && IsNumberLike(to)) return;
            throw CannotImplicit(value.Type, to);
        }

        private static object Assign(in Arg value, Type to, bool compound, bool isChecked)
        {
            if (CanImplicit(value, to)) return ConvertImplicit(value, to);
            return DynamicTypes.ConvertNumeric(value.Value, to, isChecked);
        }

        private static bool IsNumberLike(Type t) => DynamicTypes.IsNumeric(t) || DynamicTypes.IsEnum(t);

        // ---- indexers ----

        private static Arg[] Tail(Arg[] a, int from, int dropEnd)
        {
            var r = new Arg[a.Length - from - dropEnd];
            for (int i = 0; i < r.Length; i++) r[i] = a[from + i];
            return r;
        }

        private static object[] Values(Arg[] a, int from, int dropEnd)
        {
            var r = new object[a.Length - from - dropEnd];
            for (int i = 0; i < r.Length; i++) r[i] = a[from + i].Value;
            return r;
        }

        private static Rule GetIndex(DynamicBinder b, Arg[] a, out object result)
        {
            result = null;
            object receiver = a[0].Value;
            if (receiver == null) throw NullReceiver();
            if (receiver is IDynamicObject self && self.TryGetIndex(Values(a, 1, 0), out result)) return null;
            Type t = a[0].Type;
            if (a.Length == 2 && (DynamicTypes.IsArray(t) || t == typeof(string)))
            {
                Arg index = a[1];
                CheckIndex(index);
                if (t == typeof(string))
                    return Make(args => ((string)args[0])[(int)IndexValue(args[1])]);
                return Make(args => ArrayGet(args[0], IndexValue(args[1])));
            }
            Arg[] indexes = Tail(a, 1, 0);
            DynamicMethod getter = Resolve("get_Item", Instance(t), indexes, null, out bool expanded, () => IndexerError(t));
            return Make(args => Call(getter, args[0], Fill(indexes, args, 1), expanded));
        }

        private static Rule SetIndex(DynamicBinder b, Arg[] a, out object result)
        {
            result = null;
            object receiver = a[0].Value;
            if (receiver == null) throw NullReceiver();
            object value = a[a.Length - 1].Value;
            if (receiver is IDynamicObject self && self.TrySetIndex(Values(a, 1, 1), value))
            {
                result = value;
                return null;
            }
            Type t = a[0].Type;
            bool compound = (b.Flags & CSharpBinderFlags.ValueFromCompoundAssignment) != 0;
            bool isChecked = (b.Flags & CSharpBinderFlags.CheckedContext) != 0;
            Arg right = a[a.Length - 1];
            if (a.Length == 3 && DynamicTypes.IsArray(t))
            {
                Arg index = a[1];
                CheckIndex(index);
                Type element = DynamicTypes.ElementOf(t);
                CheckAssignable(right, element, compound);
                return Make(args =>
                {
                    Arg v = right;
                    v.Value = args[2];
                    object converted = Assign(v, element, compound, isChecked);
                    ArraySet(args[0], IndexValue(args[1]), element, converted);
                    return converted;
                });
            }
            if (t == typeof(string))
                throw DynamicTypes.Error("Property or indexer 'string.this[int]' cannot be assigned to -- it is read only");
            Arg[] all = Tail(a, 1, 0);   // indexes then the value: set_Item's parameters
            DynamicMethod setter = Resolve("set_Item", Instance(t), all, null, out bool expanded, () => IndexerError(t));
            return Make(args =>
            {
                Call(setter, args[0], Fill(all, args, 1), expanded);
                return args[args.Length - 1];
            });
        }

        private static RuntimeBinderException IndexerError(Type t)
            => DynamicTypes.Error("Cannot apply indexing with [] to an expression of type '" + N(t) + "'");

        private static void CheckIndex(in Arg index)
        {
            if (index.Type != null && DynamicTypes.IsNumeric(index.Type)
                && DynamicTypes.IsIntegral(DynamicTypes.Element(index.Type)) && index.Type != typeof(char)) return;
            if (index.Type == typeof(char)) return;
            throw CannotImplicit(index.Type, typeof(int));
        }

        private static long IndexValue(object index)
        {
            Type t = DynamicTypes.Of(index);
            GcEETypeElementType et = DynamicTypes.Element(t);
            long v = DynamicTypes.Integer(index, et);
            if (et == GcEETypeElementType.UInt64 && v < 0) throw new OverflowException("Arithmetic operation resulted in an overflow.");
            return v;
        }

        private static unsafe object ArrayGet(object array, long index)
        {
            int length = Unsafe.As<object[]>(array).Length;
            if ((ulong)index >= (uint)length) throw new IndexOutOfRangeException();
            Type element = DynamicTypes.ElementOf(DynamicTypes.Of(array));
            if (!DynamicTypes.IsValueType(element)) return Unsafe.As<object[]>(array)[(int)index];
            GcMethodTable* mt = DynamicTypes.Table(DynamicTypes.Of(array));
            byte* slot = (byte*)Unsafe.As<object, ulong>(ref array) + 16 + index * mt->ComponentSize;
            return DynamicTypes.Box(element, slot);
        }

        private static unsafe void ArraySet(object array, long index, Type element, object value)
        {
            int length = Unsafe.As<object[]>(array).Length;
            if ((ulong)index >= (uint)length) throw new IndexOutOfRangeException();
            if (!DynamicTypes.IsValueType(element))
            {
                Unsafe.As<object[]>(array)[(int)index] = value;   // stelem.ref: the element type is checked
                return;
            }
            GcMethodTable* mt = DynamicTypes.Table(DynamicTypes.Of(array));
            if (DynamicTypes.Table(element)->HasPointers)
                throw DynamicTypes.Error("an element of a struct array holding references cannot be assigned dynamically");
            byte* slot = (byte*)Unsafe.As<object, ulong>(ref array) + 16 + index * mt->ComponentSize;
            byte* from = (byte*)Unsafe.As<object, ulong>(ref value) + 8;
            for (int i = 0; i < mt->ComponentSize; i++) slot[i] = from[i];
        }

        // ---- calls ----

        private enum Scope : byte { Instance, Static, Exact }

        private struct Where
        {
            public Type Type;
            public Scope Scope;
        }

        private static Where Instance(Type t) => new Where { Type = t, Scope = Scope.Instance };

        private static bool InScope(DynamicMethod m, Where w)
        {
            switch (w.Scope)
            {
                case Scope.Instance: return !m.IsStatic && Receives(w.Type, m.Owner);
                case Scope.Static: return m.IsStatic && (m.Owner == w.Type || DynamicTypes.IsSubclass(w.Type, m.Owner));
                default: return m.Owner == w.Type;
            }
        }

        private static Rule InvokeMember(DynamicBinder b, Arg[] a, out object result)
        {
            result = null;
            string name = b.Name;
            bool discarded = (b.Flags & CSharpBinderFlags.ResultDiscarded) != 0;
            Arg[] args = Tail(a, 1, 0);
            Where w;
            if (a[0].IsStaticType) w = new Where { Type = a[0].Type, Scope = Scope.Static };
            else
            {
                object receiver = a[0].Value;
                if (receiver == null) throw NullReceiver();
                if (receiver is IDynamicObject self && Asked() && self.TryInvokeMember(name, Values(a, 1, 0), out result)) return null;
                w = Instance(a[0].Type);
                // A field or property holding a delegate is called through it.
                if (FindMethodsInScope(name, w) == 0)
                {
                    DynamicMember field = FindMember(a[0].Type, name, false);
                    if (field != null && field.Get != null)
                    {
                        Type delegateType = field.Type;
                        Func<object, object> get = field.Get;
                        DynamicMethod invoke = Resolve("Invoke", new Where { Type = delegateType, Scope = Scope.Exact }, args, null,
                                                       out bool exp, () => DynamicTypes.Error("Cannot invoke a non-delegate type"));
                        CheckResult(invoke, discarded);
                        return Make(xs => Call(invoke, get(xs[0]), Fill(args, xs, 1), exp));
                    }
                }
            }
            Type[] typeArguments = b.TypeArguments;
            DynamicMethod m = Resolve(name, w, args, typeArguments, out bool expanded, () => NoDefinition(w.Type, name));
            CheckResult(m, discarded);
            return Make(xs => Call(m, w.Scope == Scope.Static ? null : xs[0], Fill(args, xs, 1), expanded));
        }

        private static void CheckResult(DynamicMethod m, bool discarded)
        {
            if (m.Returns == null && !discarded)
                throw DynamicTypes.Error("Cannot implicitly convert type 'void' to 'object'");
        }

        private static int FindMethodsInScope(string name, Where w)
        {
            List<DynamicMethod> list = DynamicMembers.Methods(name);
            int n = 0;
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                    if (InScope(list[i], w)) n++;
            return n;
        }

        private static Rule Invoke(DynamicBinder b, Arg[] a, out object result)
        {
            result = null;
            object receiver = a[0].Value;
            if (receiver == null) throw NullReceiver();
            Arg[] args = Tail(a, 1, 0);
            bool discarded = (b.Flags & CSharpBinderFlags.ResultDiscarded) != 0;
            DynamicMethod m = Resolve("Invoke", new Where { Type = a[0].Type, Scope = Scope.Exact }, args, null, out bool expanded,
                                      () => DynamicTypes.Error("Cannot invoke a non-delegate type"));
            CheckResult(m, discarded);
            return Make(xs => Call(m, xs[0], Fill(args, xs, 1), expanded));
        }

        private static Rule InvokeConstructor(DynamicBinder b, Arg[] a)
        {
            Type t = a[0].Type;
            Arg[] args = Tail(a, 1, 0);
            DynamicMethod m = Resolve(".ctor", new Where { Type = t, Scope = Scope.Exact }, args, null, out bool expanded,
                                      () => DynamicTypes.Error("'" + N(t) + "' does not contain a constructor that takes " + args.Length.ToString() + " arguments"));
            return Make(xs => Call(m, null, Fill(args, xs, 1), expanded));
        }

        private static Arg[] Fill(Arg[] template, object[] values, int from)
        {
            var r = new Arg[template.Length];
            for (int i = 0; i < r.Length; i++)
            {
                r[i] = template[i];
                r[i].Value = values[from + i];
            }
            return r;
        }

        private static object Call(DynamicMethod m, object receiver, Arg[] args, bool expanded)
        {
            int n = m.Parameters.Length;
            var call = new object[n];
            if (expanded)
            {
                for (int i = 0; i < n - 1; i++) call[i] = ConvertImplicit(args[i], m.Parameters[i]);
                var items = new object[args.Length - (n - 1)];
                for (int i = 0; i < items.Length; i++) items[i] = ConvertImplicit(args[n - 1 + i], m.ParamsElement);
                call[n - 1] = new ParamsPack(items);
            }
            else
            {
                for (int i = 0; i < n; i++)
                    call[i] = i < args.Length ? ConvertImplicit(args[i], m.Parameters[i]) : DynamicMethod.Missing;
            }
            return m.Invoke(receiver, call);
        }

        // ---- overload resolution (C# §12.6.4) ----

        private struct Candidate
        {
            public DynamicMethod Method;
            public bool Expanded;
            public int Defaults;
        }

        private static DynamicMethod Resolve(string name, Where w, Arg[] args, Type[] typeArguments, out bool expanded,
                                             Func<RuntimeBinderException> missing)
        {
            expanded = false;
            Looked();
            List<DynamicMethod> list = DynamicMembers.Methods(name);
            var applicable = new List<Candidate>();
            DynamicMethod any = null, rightCount = null, needsInference = null;
            bool wantGeneric = typeArguments != null && typeArguments.Length > 0;
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    DynamicMethod m = list[i];
                    if (!InScope(m, w)) continue;
                    if (m.GenericArity > 0 && m.TypeArguments == null)
                    {
                        if (!wantGeneric) needsInference ??= m;
                        continue;
                    }
                    if (!wantGeneric && m.TypeArguments != null)
                    {
                        needsInference ??= m;   // only instances closed by other call sites: still inference here
                        continue;
                    }
                    if (wantGeneric && m.TypeArguments == null) continue;
                    if (wantGeneric && !SameTypes(m.TypeArguments, typeArguments)) continue;
                    any ??= m;
                    if (Applicable(m, args, false, out int defaults))
                        applicable.Add(new Candidate { Method = m, Defaults = defaults });
                    else if (m.HasParamsArray && Applicable(m, args, true, out _))
                        applicable.Add(new Candidate { Method = m, Expanded = true });
                    if (CountFits(m, args.Length)) rightCount ??= m;
                }
            }
            if (applicable.Count == 0)
            {
                if (any == null && needsInference != null)
                    throw DynamicTypes.Error("The type arguments for method '" + N(needsInference.Owner) + "." + needsInference.Name
                                             + "' cannot be inferred from the usage. Try specifying the type arguments explicitly.");
                if (any == null) throw missing();
                if (rightCount == null)
                    throw DynamicTypes.Error(name == ".ctor"
                        ? "'" + N(any.Owner) + "' does not contain a constructor that takes " + args.Length.ToString() + " arguments"
                        : "No overload for method '" + name + "' takes " + args.Length.ToString() + " arguments");
                throw DynamicTypes.Error("The best overloaded method match for '" + Signature(rightCount) + "' has some invalid arguments");
            }

            return Best(applicable, args, out expanded);
        }

        private static DynamicMethod Best(List<Candidate> applicable, Arg[] args, out bool expanded)
        {
            // Methods of a base type drop out when a derived type has an applicable one.
            for (int i = applicable.Count - 1; i >= 0; i--)
                for (int j = 0; j < applicable.Count; j++)
                    if (DynamicTypes.IsSubclass(applicable[j].Method.Owner, applicable[i].Method.Owner))
                    {
                        applicable.RemoveAt(i);
                        break;
                    }

            int best = -1;
            for (int i = 0; i < applicable.Count; i++)
            {
                bool wins = true;
                for (int j = 0; j < applicable.Count && wins; j++)
                    if (i != j && !Better(applicable[i], applicable[j], args)) wins = false;
                if (wins) { best = i; break; }
            }
            if (best < 0)
            {
                Candidate x = applicable[0], y = applicable[1];
                for (int i = 0; i < applicable.Count; i++)
                    for (int j = i + 1; j < applicable.Count; j++)
                        if (!Better(applicable[i], applicable[j], args) && !Better(applicable[j], applicable[i], args))
                        {
                            x = applicable[i];
                            y = applicable[j];
                            i = applicable.Count;
                            break;
                        }
                throw DynamicTypes.Error("The call is ambiguous between the following methods or properties: '"
                                         + Signature(x.Method) + "' and '" + Signature(y.Method) + "'");
            }
            expanded = applicable[best].Expanded;
            return applicable[best].Method;
        }

        private static bool SameTypes(Type[] x, Type[] y)
        {
            if (x.Length != y.Length) return false;
            for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) return false;
            return true;
        }

        private static bool CountFits(DynamicMethod m, int count)
            => (count <= m.Parameters.Length && count >= m.Required + (m.HasParamsArray ? 1 : 0))
               || (m.HasParamsArray && count >= m.Parameters.Length - 1);

        private static Type ParameterFor(DynamicMethod m, int i, bool expanded)
            => expanded && i >= m.Parameters.Length - 1 ? m.ParamsElement : m.Parameters[i];

        private static bool Applicable(DynamicMethod m, Arg[] args, bool expanded, out int defaults)
        {
            defaults = 0;
            int n = m.Parameters.Length;
            if (expanded) { if (args.Length < n - 1) return false; }
            else
            {
                // The params array has no default: the normal form needs it given.
                if (args.Length > n || args.Length < m.Required + (m.HasParamsArray ? 1 : 0)) return false;
                defaults = n - args.Length;
            }
            for (int i = 0; i < args.Length; i++)
                if (!CanImplicit(args[i], ParameterFor(m, i, expanded))) return false;
            return true;
        }

        // Is x a better function member than y for these arguments (§12.6.4.3)?
        private static bool Better(in Candidate x, in Candidate y, Arg[] args)
        {
            bool someBetter = false;
            for (int i = 0; i < args.Length; i++)
            {
                Type px = ParameterFor(x.Method, i, x.Expanded), py = ParameterFor(y.Method, i, y.Expanded);
                int c = BetterConversion(args[i], px, py);
                if (c < 0) return false;
                if (c > 0) someBetter = true;
            }
            if (someBetter) return true;
            if (!x.Expanded && y.Expanded) return true;           // normal form over expanded
            if (x.Expanded && !y.Expanded) return false;
            if (x.Defaults == 0 && y.Defaults > 0) return true;   // all arguments given over defaults filled in
            if (x.Expanded && y.Expanded)
                return x.Method.Parameters.Length > y.Method.Parameters.Length;   // more declared parameters
            return false;
        }

        // 1: to x is better, -1: to y is better, 0: neither (§12.6.4.5, §12.6.4.7).
        private static int BetterConversion(in Arg a, Type x, Type y)
        {
            if (x == y) return 0;
            if (a.Type != null)
            {
                if (a.Type == x) return 1;
                if (a.Type == y) return -1;
            }
            bool xy = Implicit(x, y), yx = Implicit(y, x);
            if (xy && !yx) return 1;
            if (yx && !xy) return -1;
            if (SignedOverUnsigned(x, y)) return 1;
            if (SignedOverUnsigned(y, x)) return -1;
            return 0;
        }

        private static bool Implicit(Type from, Type to)
            => DynamicTypes.IsAssignable(from, to) || DynamicTypes.ImplicitNumeric(from, to);

        private static bool SignedOverUnsigned(Type s, Type u)
        {
            if (!DynamicTypes.IsNumeric(s) || !DynamicTypes.IsNumeric(u)) return false;
            GcEETypeElementType x = DynamicTypes.Element(s), y = DynamicTypes.Element(u);
            switch (x)
            {
                case GcEETypeElementType.SByte:
                    return y == GcEETypeElementType.Byte || y == GcEETypeElementType.UInt16 || y == GcEETypeElementType.UInt32 || y == GcEETypeElementType.UInt64;
                case GcEETypeElementType.Int16:
                    return y == GcEETypeElementType.UInt16 || y == GcEETypeElementType.UInt32 || y == GcEETypeElementType.UInt64;
                case GcEETypeElementType.Int32:
                    return y == GcEETypeElementType.UInt32 || y == GcEETypeElementType.UInt64;
                case GcEETypeElementType.Int64:
                    return y == GcEETypeElementType.UInt64;
            }
            return false;
        }

        // ---- conversions ----

        /// <summary>Whether the argument converts implicitly to <paramref name="to"/> (§10.2).</summary>
        private static bool CanImplicit(in Arg a, Type to)
        {
            if (to == typeof(object)) return true;
            if (a.Type == null) return !DynamicTypes.IsValueType(to) || DynamicTypes.IsNullable(to);   // a null
            if (DynamicTypes.IsAssignable(a.Type, to)) return true;
            if (a.Value == null && !DynamicTypes.IsValueType(a.Type)) return false;   // a typed null: reference conversions only
            if (DynamicTypes.IsNullable(to))
            {
                Type under = DynamicMembers.NullableOf(to);
                return under != null && CanImplicit(a, under);
            }
            if (DynamicTypes.ImplicitNumeric(a.Type, to)) return true;
            if (a.Constant && DynamicTypes.ConstantFits(a.Value, a.Type, to)) return true;
            if (a.Constant && a.Type == typeof(int) && (int)a.Value == 0 && DynamicTypes.IsEnum(to)) return true;
            return UserConversion(a.Type, to, false) != null;
        }

        private static object ConvertImplicit(in Arg a, Type to)
        {
            object v = a.Value;
            if (v == null || to == typeof(object)) return v;
            Type from = DynamicTypes.Of(v);
            if (DynamicTypes.IsAssignable(from, to)) return v;
            if (DynamicTypes.IsNullable(to))
            {
                return ConvertImplicit(a, DynamicMembers.NullableOf(to));
            }
            if (DynamicTypes.IsNumeric(from) && (DynamicTypes.IsNumeric(to) || DynamicTypes.IsEnum(to)))
                return DynamicTypes.ConvertNumeric(v, to, false);
            DynamicMethod op = UserConversion(from, to, false);
            if (op != null) return ConvertImplicit(new Arg { Value = op.Invoke(null, new[] { v }), Type = op.Returns }, to);
            throw CannotImplicit(from, to);
        }

        // A user-defined conversion from `from` to `to` declared in either (or their bases), §10.5.
        private static DynamicMethod UserConversion(Type from, Type to, bool isExplicit)
        {
            DynamicMethod best = UserConversionNamed("op_Implicit", from, to);
            if (best == null && isExplicit) best = UserConversionNamed("op_Explicit", from, to);
            return best;
        }

        private static DynamicMethod UserConversionNamed(string name, Type from, Type to)
        {
            if (from == null) return null;
            List<DynamicMethod> list = DynamicMembers.Methods(name);
            if (list == null) return null;
            DynamicMethod best = null;
            for (int i = 0; i < list.Count; i++)
            {
                DynamicMethod m = list[i];
                bool declaredHere = DynamicTypes.IsAssignable(from, m.Owner) || m.Owner == to || DynamicTypes.IsSubclass(to, m.Owner)
                                    || (DynamicTypes.IsNullable(to) && m.Owner == DynamicMembers.NullableOf(to));
                if (!declaredHere) continue;
                Type p = m.Parameters[0];
                if (!(DynamicTypes.IsAssignable(from, p) || DynamicTypes.ImplicitNumeric(from, p))) continue;
                Type r = m.Returns;
                if (!(DynamicTypes.IsAssignable(r, to) || DynamicTypes.ImplicitNumeric(r, to))) continue;
                // Most specific: the exact source, then the exact target.
                if (best == null || (p == from && best.Parameters[0] != from) || (r == to && best.Returns != to && best.Parameters[0] == p))
                    best = m;
            }
            return best;
        }

        private static RuntimeBinderException CannotImplicit(Type from, Type to)
        {
            if (from == null)
                return DynamicTypes.Error("Cannot convert null to '" + N(to) + "' because it is a non-nullable value type");
            bool explicitExists = ExplicitExists(from, to);
            return DynamicTypes.Error("Cannot implicitly convert type '" + N(from) + "' to '" + N(to) + "'"
                                      + (explicitExists ? ". An explicit conversion exists (are you missing a cast?)" : ""));
        }

        private static bool ExplicitExists(Type from, Type to)
            => (IsNumberLike(from) && IsNumberLike(to)) || DynamicTypes.IsAssignable(to, from)
               || UserConversionNamed("op_Explicit", from, to) != null;

        private static Rule Convert(DynamicBinder b, Arg[] a, out object result)
        {
            result = null;
            Type to = b.ConvertTo;
            bool isExplicit = (b.Flags & CSharpBinderFlags.ConvertExplicit) != 0;
            bool isChecked = (b.Flags & CSharpBinderFlags.CheckedContext) != 0;
            object v = a[0].Value;
            if (v == null)
            {
                if (DynamicTypes.IsValueType(to) && !DynamicTypes.IsNullable(to)) throw CannotImplicit(null, to);
                return Make(static _ => null, static (object _) => null);
            }
            if (v is IDynamicObject self && self.TryConvert(to, isExplicit, out result)) return null;
            Type from = a[0].Type;
            Type target = DynamicTypes.IsNullable(to) ? DynamicMembers.NullableOf(to) ?? to : to;
            if (DynamicTypes.IsAssignable(from, target)) return Make(static xs => xs[0], static (object x) => x);
            if (DynamicTypes.ImplicitNumeric(from, target))
                return Make(xs => DynamicTypes.ConvertNumeric(xs[0], target, false), x => DynamicTypes.ConvertNumeric(x, target, false));
            if (isExplicit && IsNumberLike(from) && IsNumberLike(target))
                return Make(xs => DynamicTypes.ConvertNumeric(xs[0], target, isChecked), x => DynamicTypes.ConvertNumeric(x, target, isChecked));
            DynamicMethod op = UserConversion(from, target, isExplicit);
            if (op != null)
                return Make(xs =>
                {
                    object r = op.Invoke(null, new[] { xs[0] });
                    Type rt = DynamicTypes.Of(r);
                    if (r == null || DynamicTypes.IsAssignable(rt, target)) return r;
                    return DynamicTypes.ConvertNumeric(r, target, isChecked);
                });
            if (isExplicit)
                throw DynamicTypes.Error("Cannot convert type '" + N(from) + "' to '" + N(to) + "'");
            throw CannotImplicit(from, to);
        }

        /// <summary>A value converted as `(T)value` would be in C#: for IDynamicObject implementations that delegate the predefined cases.</summary>
        public static object ConvertValue(object value, Type to, bool isExplicit)
        {
            if (value == null) return null;
            Type from = DynamicTypes.Of(value);
            if (DynamicTypes.IsAssignable(from, to)) return value;
            if (DynamicTypes.ImplicitNumeric(from, to) || (isExplicit && IsNumberLike(from) && IsNumberLike(to)))
                return DynamicTypes.ConvertNumeric(value, to, false);
            if (isExplicit) throw DynamicTypes.Error("Cannot convert type '" + N(from) + "' to '" + N(to) + "'");
            throw CannotImplicit(from, to);
        }

        // ---- operators ----

        private static string OperatorName(Expr e)
        {
            switch (e)
            {
                case Expr.Add: return "op_Addition";
                case Expr.Subtract: return "op_Subtraction";
                case Expr.Multiply: return "op_Multiply";
                case Expr.Divide: return "op_Division";
                case Expr.Modulo: return "op_Modulus";
                case Expr.And: return "op_BitwiseAnd";
                case Expr.Or: return "op_BitwiseOr";
                case Expr.ExclusiveOr: return "op_ExclusiveOr";
                case Expr.LeftShift: return "op_LeftShift";
                case Expr.RightShift: return "op_RightShift";
                case Expr.Equal: return "op_Equality";
                case Expr.NotEqual: return "op_Inequality";
                case Expr.LessThan: return "op_LessThan";
                case Expr.GreaterThan: return "op_GreaterThan";
                case Expr.LessThanOrEqual: return "op_LessThanOrEqual";
                case Expr.GreaterThanOrEqual: return "op_GreaterThanOrEqual";
                case Expr.Negate: return "op_UnaryNegation";
                case Expr.UnaryPlus: return "op_UnaryPlus";
                case Expr.Not: return "op_LogicalNot";
                case Expr.OnesComplement: return "op_OnesComplement";
                case Expr.Increment: return "op_Increment";
                case Expr.Decrement: return "op_Decrement";
                case Expr.IsTrue: return "op_True";
                case Expr.IsFalse: return "op_False";
            }
            return null;
        }

        private static string Symbol(Expr e, bool logical)
        {
            switch (e)
            {
                case Expr.Add: return "+";
                case Expr.Subtract: return "-";
                case Expr.Multiply: return "*";
                case Expr.Divide: return "/";
                case Expr.Modulo: return "%";
                case Expr.And: return logical ? "&&" : "&";
                case Expr.Or: return logical ? "||" : "|";
                case Expr.ExclusiveOr: return "^";
                case Expr.LeftShift: return "<<";
                case Expr.RightShift: return ">>";
                case Expr.Equal: return "==";
                case Expr.NotEqual: return "!=";
                case Expr.LessThan: return "<";
                case Expr.GreaterThan: return ">";
                case Expr.LessThanOrEqual: return "<=";
                case Expr.GreaterThanOrEqual: return ">=";
                case Expr.Negate: return "-";
                case Expr.UnaryPlus: return "+";
                case Expr.Not: return "!";
                case Expr.OnesComplement: return "~";
                case Expr.Increment: return "++";
                case Expr.Decrement: return "--";
                case Expr.IsTrue: return "true";
                case Expr.IsFalse: return "false";
            }
            return e.ToString();
        }

        // `x op= y` binds as `x op y`; the checked forms only set the context.
        private static Expr Plain(Expr e, ref bool isChecked)
        {
            switch (e)
            {
                case Expr.AddChecked: isChecked = true; return Expr.Add;
                case Expr.SubtractChecked: isChecked = true; return Expr.Subtract;
                case Expr.MultiplyChecked: isChecked = true; return Expr.Multiply;
                case Expr.NegateChecked: isChecked = true; return Expr.Negate;
                case Expr.AddAssign: return Expr.Add;
                case Expr.SubtractAssign: return Expr.Subtract;
                case Expr.MultiplyAssign: return Expr.Multiply;
                case Expr.DivideAssign: return Expr.Divide;
                case Expr.ModuloAssign: return Expr.Modulo;
                case Expr.AndAssign: return Expr.And;
                case Expr.OrAssign: return Expr.Or;
                case Expr.ExclusiveOrAssign: return Expr.ExclusiveOr;
                case Expr.LeftShiftAssign: return Expr.LeftShift;
                case Expr.RightShiftAssign: return Expr.RightShift;
                case Expr.AddAssignChecked: isChecked = true; return Expr.Add;
                case Expr.SubtractAssignChecked: isChecked = true; return Expr.Subtract;
                case Expr.MultiplyAssignChecked: isChecked = true; return Expr.Multiply;
            }
            return e;
        }

        // User-defined operators declared in an operand's type or its bases (§12.4.6).
        private static DynamicMethod UserOperator(string name, Arg[] args)
        {
            if (name == null) return null;
            List<DynamicMethod> list = DynamicMembers.Methods(name);
            if (list == null) return null;
            var applicable = new List<Candidate>();
            for (int i = 0; i < list.Count; i++)
            {
                DynamicMethod m = list[i];
                bool declared = false;
                for (int k = 0; k < args.Length && !declared; k++)
                    declared = args[k].Type != null && (m.Owner == args[k].Type || DynamicTypes.IsSubclass(args[k].Type, m.Owner));
                if (declared && m.IsStatic && Applicable(m, args, false, out _))
                    applicable.Add(new Candidate { Method = m });
            }
            return applicable.Count == 0 ? null : Best(applicable, args, out _);
        }

        private static Rule Binary(DynamicBinder b, Arg[] a)
        {
            bool isChecked = (b.Flags & CSharpBinderFlags.CheckedContext) != 0;
            bool logical = (b.Flags & CSharpBinderFlags.BinaryOperationLogical) != 0;
            Expr op = Plain(b.Expression, ref isChecked);
            Arg x = a[0], y = a[1];

            DynamicMethod user = UserOperator(OperatorName(op), a);
            if (user != null)
            {
                Arg[] template = a;
                return Make(xs => Call(user, null, Fill(template, xs, 0), false));
            }

            Type tx = x.Type, ty = y.Type;
            // Strings: + concatenates anything with a string; == and != compare text.
            if (op == Expr.Add && (tx == typeof(string) || ty == typeof(string)))
                return Make(static xs => string.Concat(Text(xs[0]), Text(xs[1])), static (x, y) => string.Concat(Text(x), Text(y)));
            if ((op == Expr.Equal || op == Expr.NotEqual)
                && (tx == typeof(string) || tx == null) && (ty == typeof(string) || ty == null) && (tx != null || ty != null))
            {
                bool eq = op == Expr.Equal;
                return Make(xs => Bool(string.Equals((string)xs[0], (string)xs[1]) == eq), (x, y) => Bool(string.Equals((string)x, (string)y) == eq));
            }
            // bool
            if (tx == typeof(bool) && ty == typeof(bool))
            {
                switch (op)
                {
                    case Expr.And: return Make(static xs => Bool((bool)xs[0] & (bool)xs[1]), static (x, y) => Bool((bool)x & (bool)y));
                    case Expr.Or: return Make(static xs => Bool((bool)xs[0] | (bool)xs[1]), static (x, y) => Bool((bool)x | (bool)y));
                    case Expr.ExclusiveOr: return Make(static xs => Bool((bool)xs[0] ^ (bool)xs[1]), static (x, y) => Bool((bool)x ^ (bool)y));
                    case Expr.Equal: return Make(static xs => Bool((bool)xs[0] == (bool)xs[1]), static (x, y) => Bool((bool)x == (bool)y));
                    case Expr.NotEqual: return Make(static xs => Bool((bool)xs[0] != (bool)xs[1]), static (x, y) => Bool((bool)x != (bool)y));
                }
                throw OperatorError(op, logical, tx, ty);
            }
            // Lifted: a null against a number or an enum.
            if ((tx == null && ty != null && IsNumberLike(ty)) || (ty == null && tx != null && IsNumberLike(tx)))
            {
                switch (op)
                {
                    case Expr.Equal: return Make(static _ => Bool(false));
                    case Expr.NotEqual: return Make(static _ => Bool(true));
                    case Expr.LessThan:
                    case Expr.GreaterThan:
                    case Expr.LessThanOrEqual:
                    case Expr.GreaterThanOrEqual: return Make(static _ => Bool(false));
                    default: return Make(static _ => null);
                }
            }
            if (tx != null && ty != null && (DynamicTypes.IsEnum(tx) || DynamicTypes.IsEnum(ty)))
                return EnumBinary(op, logical, isChecked, x, y);
            if (tx != null && ty != null && DynamicTypes.IsNumeric(tx) && DynamicTypes.IsNumeric(ty))
                return NumericBinary(op, logical, isChecked, x, y);
            // Reference equality.
            if ((op == Expr.Equal || op == Expr.NotEqual)
                && (tx == null || !DynamicTypes.IsValueType(tx)) && (ty == null || !DynamicTypes.IsValueType(ty))
                && (tx == null || ty == null || DynamicTypes.IsAssignable(tx, ty) || DynamicTypes.IsAssignable(ty, tx)
                    || DynamicTypes.IsInterface(tx) || DynamicTypes.IsInterface(ty)))
            {
                bool eq = op == Expr.Equal;
                return Make(xs => Bool(ReferenceEquals(xs[0], xs[1]) == eq), (x, y) => Bool(ReferenceEquals(x, y) == eq));
            }
            throw OperatorError(op, logical, tx, ty);
        }

        private static string Text(object o) => o == null ? "" : o.ToString();

        private static RuntimeBinderException OperatorError(Expr op, bool logical, Type x, Type y)
            => DynamicTypes.Error("Operator '" + Symbol(op, logical) + "' cannot be applied to operands of type '"
                                  + (x == null ? "<null>" : N(x)) + "' and '" + (y == null ? "<null>" : N(y)) + "'");

        private static bool IsComparison(Expr op)
            => op == Expr.Equal || op == Expr.NotEqual || op == Expr.LessThan || op == Expr.GreaterThan
            || op == Expr.LessThanOrEqual || op == Expr.GreaterThanOrEqual;

        private static Rule NumericBinary(Expr op, bool logical, bool isChecked, Arg x, Arg y)
        {
            Type tx = x.Type, ty = y.Type;
            if (op == Expr.LeftShift || op == Expr.RightShift)
            {
                Type left = DynamicTypes.PromoteUnary(tx);
                if (!DynamicTypes.IsIntegral(DynamicTypes.Element(left)) || !CanImplicit(y, typeof(int)))
                    throw OperatorError(op, logical, tx, ty);
                bool shl = op == Expr.LeftShift;
                return Make(xs =>
                {
                    int count = (int)DynamicTypes.ConvertNumeric(xs[1], typeof(int), false);
                    long v = DynamicTypes.Integer(xs[0], DynamicTypes.ElementTypeOf(xs[0]));
                    if (left == typeof(int)) return shl ? (int)v << count : (int)v >> count;
                    if (left == typeof(uint)) return shl ? (uint)v << count : (uint)v >> count;
                    if (left == typeof(long)) return shl ? v << count : v >> count;
                    return shl ? (ulong)v << count : (ulong)v >> count;
                });
            }
            Type p = Promote(x, y);
            if (p == null) throw OperatorError(op, logical, tx, ty);   // ulong with a signed type: no operator
            bool integral = p != typeof(float) && p != typeof(double);
            if ((op == Expr.And || op == Expr.Or || op == Expr.ExclusiveOr) && !integral)
                throw OperatorError(op, logical, tx, ty);
            if (logical || (!IsComparison(op) && OperatorName(op) == null)) throw OperatorError(op, logical, tx, ty);
            return Make(xs => Arithmetic(op, p, xs[0], xs[1], isChecked), (x, y) => Arithmetic(op, p, x, y, isChecked));
        }

        // Binary promotion with C#'s constant rule: an int constant that fits the other operand's
        // unsigned type does not widen to long (`uintValue + 1` is uint).
        private static Type Promote(in Arg x, in Arg y)
        {
            Type tx = x.Type, ty = y.Type;
            if (x.Constant && DynamicTypes.ConstantFits(x.Value, tx, ty) && IsUnsignedWide(ty)) tx = ty;
            if (y.Constant && DynamicTypes.ConstantFits(y.Value, ty, tx) && IsUnsignedWide(tx)) ty = tx;
            return DynamicTypes.Promote(tx, ty);
        }

        private static bool IsUnsignedWide(Type t) => t == typeof(uint) || t == typeof(ulong);

        private static object Arithmetic(Expr op, Type p, object a, object b, bool isChecked)
        {
            GcEETypeElementType ea = DynamicTypes.ElementTypeOf(a), eb = DynamicTypes.ElementTypeOf(b);
            if (p == typeof(double) || p == typeof(float))
            {
                double x = DynamicTypes.Float(a, ea), y = DynamicTypes.Float(b, eb);
                if (p == typeof(float))
                {
                    float fx = (float)x, fy = (float)y;
                    switch (op)
                    {
                        case Expr.Add: return fx + fy;
                        case Expr.Subtract: return fx - fy;
                        case Expr.Multiply: return fx * fy;
                        case Expr.Divide: return fx / fy;
                        case Expr.Modulo: return fx % fy;
                    }
                    return Compare(op, fx < fy ? -1 : fx > fy ? 1 : fx == fy ? 0 : 2);
                }
                switch (op)
                {
                    case Expr.Add: return x + y;
                    case Expr.Subtract: return x - y;
                    case Expr.Multiply: return x * y;
                    case Expr.Divide: return x / y;
                    case Expr.Modulo: return x % y;
                }
                return Compare(op, x < y ? -1 : x > y ? 1 : x == y ? 0 : 2);
            }
            long la = DynamicTypes.Integer(a, ea), lb = DynamicTypes.Integer(b, eb);
            if (p == typeof(int))
            {
                int x = (int)la, y = (int)lb;
                switch (op)
                {
                    case Expr.Add: return isChecked ? checked(x + y) : unchecked(x + y);
                    case Expr.Subtract: return isChecked ? checked(x - y) : unchecked(x - y);
                    case Expr.Multiply: return isChecked ? checked(x * y) : unchecked(x * y);
                    case Expr.Divide: return x / y;
                    case Expr.Modulo: return x % y;
                    case Expr.And: return x & y;
                    case Expr.Or: return x | y;
                    case Expr.ExclusiveOr: return x ^ y;
                }
                return Compare(op, x < y ? -1 : x > y ? 1 : 0);
            }
            if (p == typeof(uint))
            {
                uint x = (uint)la, y = (uint)lb;
                switch (op)
                {
                    case Expr.Add: return isChecked ? checked(x + y) : unchecked(x + y);
                    case Expr.Subtract: return isChecked ? checked(x - y) : unchecked(x - y);
                    case Expr.Multiply: return isChecked ? checked(x * y) : unchecked(x * y);
                    case Expr.Divide: return x / y;
                    case Expr.Modulo: return x % y;
                    case Expr.And: return x & y;
                    case Expr.Or: return x | y;
                    case Expr.ExclusiveOr: return x ^ y;
                }
                return Compare(op, x < y ? -1 : x > y ? 1 : 0);
            }
            if (p == typeof(long))
            {
                long x = la, y = lb;
                switch (op)
                {
                    case Expr.Add: return isChecked ? checked(x + y) : unchecked(x + y);
                    case Expr.Subtract: return isChecked ? checked(x - y) : unchecked(x - y);
                    case Expr.Multiply: return isChecked ? checked(x * y) : unchecked(x * y);
                    case Expr.Divide: return x / y;
                    case Expr.Modulo: return x % y;
                    case Expr.And: return x & y;
                    case Expr.Or: return x | y;
                    case Expr.ExclusiveOr: return x ^ y;
                }
                return Compare(op, x < y ? -1 : x > y ? 1 : 0);
            }
            {
                ulong x = (ulong)la, y = (ulong)lb;
                switch (op)
                {
                    case Expr.Add: return isChecked ? checked(x + y) : unchecked(x + y);
                    case Expr.Subtract: return isChecked ? checked(x - y) : unchecked(x - y);
                    case Expr.Multiply: return isChecked ? checked(x * y) : unchecked(x * y);
                    case Expr.Divide: return x / y;
                    case Expr.Modulo: return x % y;
                    case Expr.And: return x & y;
                    case Expr.Or: return x | y;
                    case Expr.ExclusiveOr: return x ^ y;
                }
                return Compare(op, x < y ? -1 : x > y ? 1 : 0);
            }
        }

        // c: -1 less, 0 equal, 1 greater, 2 unordered (NaN).
        private static object Compare(Expr op, int c)
        {
            switch (op)
            {
                case Expr.Equal: return Bool(c == 0);
                case Expr.NotEqual: return Bool(c != 0);
                case Expr.LessThan: return Bool(c == -1);
                case Expr.GreaterThan: return Bool(c == 1);
                case Expr.LessThanOrEqual: return Bool(c == -1 || c == 0);
                case Expr.GreaterThanOrEqual: return Bool(c == 1 || c == 0);
            }
            throw DynamicTypes.Error("operator not defined");
        }

        private static Rule EnumBinary(Expr op, bool logical, bool isChecked, Arg x, Arg y)
        {
            Type tx = x.Type, ty = y.Type;
            bool ex = DynamicTypes.IsEnum(tx), ey = DynamicTypes.IsEnum(ty);
            Type e = ex ? tx : ty;
            Type under = UnderlyingOf(e);
            if (ex && ey)
            {
                if (tx != ty) throw OperatorError(op, logical, tx, ty);
                if (IsComparison(op)) return Make(xs => Arithmetic(op, typeof(long), AsLong(xs[0]), AsLong(xs[1]), false));
                if (op == Expr.And || op == Expr.Or || op == Expr.ExclusiveOr)
                    return Make(xs => DynamicTypes.BoxInteger(e, (long)Arithmetic(op, typeof(long), AsLong(xs[0]), AsLong(xs[1]), false)));
                if (op == Expr.Subtract)   // E - E is the underlying type
                    return Make(xs => DynamicTypes.ConvertNumeric(Arithmetic(op, typeof(long), AsLong(xs[0]), AsLong(xs[1]), isChecked), under, isChecked));
                throw OperatorError(op, logical, tx, ty);
            }
            // E + U, U + E, E - U: the other side converts to the underlying type.
            Arg other = ex ? y : x;
            bool fits = CanImplicit(other, under);
            if (fits && (op == Expr.Add || (op == Expr.Subtract && ex)))
                return Make(xs => DynamicTypes.ConvertNumeric(
                    Arithmetic(op, typeof(long), AsLong(xs[0]), AsLong(xs[1]), isChecked), e, isChecked));
            throw OperatorError(op, logical, tx, ty);
        }

        private static object AsLong(object v) => DynamicTypes.Integer(v, DynamicTypes.ElementTypeOf(v));

        private static Type UnderlyingOf(Type e)
        {
            switch (DynamicTypes.Element(e))
            {
                case GcEETypeElementType.SByte: return typeof(sbyte);
                case GcEETypeElementType.Byte: return typeof(byte);
                case GcEETypeElementType.Int16: return typeof(short);
                case GcEETypeElementType.UInt16: return typeof(ushort);
                case GcEETypeElementType.Int32: return typeof(int);
                case GcEETypeElementType.UInt32: return typeof(uint);
                case GcEETypeElementType.Int64: return typeof(long);
                default: return typeof(ulong);
            }
        }

        private static Rule Unary(DynamicBinder b, Arg[] a)
        {
            bool isChecked = (b.Flags & CSharpBinderFlags.CheckedContext) != 0;
            Expr op = Plain(b.Expression, ref isChecked);
            Arg x = a[0];
            Type t = x.Type;
            if (t == typeof(bool))
            {
                switch (op)
                {
                    case Expr.Not: return Make(static xs => Bool(!(bool)xs[0]), static (object x) => Bool(!(bool)x));
                    case Expr.IsTrue: return Make(static xs => xs[0], static (object x) => x);
                    case Expr.IsFalse: return Make(static xs => Bool(!(bool)xs[0]), static (object x) => Bool(!(bool)x));
                }
            }
            DynamicMethod user = t == null ? null : UserOperator(OperatorName(op), a);
            if (user != null)
                return Make(xs => Call(user, null, Fill(a, xs, 0), false));
            if (op == Expr.IsTrue || op == Expr.IsFalse)
            {
                // A value that converts to bool implicitly.
                if (t != null && CanImplicit(x, typeof(bool)))
                {
                    bool negate = op == Expr.IsFalse;
                    return Make(xs => Bool((bool)ConvertImplicit(new Arg { Value = xs[0], Type = DynamicTypes.Of(xs[0]) }, typeof(bool)) != negate));
                }
                throw CannotImplicit(t, typeof(bool));
            }
            if (t == null) return Make(static _ => null);   // lifted on a null
            if (DynamicTypes.IsNumeric(t) || DynamicTypes.IsEnum(t))
            {
                bool isEnum = DynamicTypes.IsEnum(t);
                switch (op)
                {
                    case Expr.Increment:
                    case Expr.Decrement:
                        {
                            long delta = op == Expr.Increment ? 1 : -1;
                            if (t == typeof(float)) return Make(xs => (float)xs[0] + delta);
                            if (t == typeof(double)) return Make(xs => (double)xs[0] + delta);
                            // Same type back; wraps unless checked.
                            return Make(xs =>
                            {
                                long v = DynamicTypes.Integer(xs[0], DynamicTypes.Element(t));
                                bool unsigned64 = DynamicTypes.Element(t) == GcEETypeElementType.UInt64;
                                long r = unchecked(v + delta);
                                object wide = unsigned64 ? (object)(ulong)r : (object)r;
                                if (isChecked && unsigned64 && ((delta > 0 && (ulong)r == 0) || (delta < 0 && (ulong)v == 0)))
                                    throw new OverflowException("Arithmetic operation resulted in an overflow.");
                                if (isChecked && !unsigned64 && DynamicTypes.Element(t) == GcEETypeElementType.Int64
                                    && ((delta > 0 && v == long.MaxValue) || (delta < 0 && v == long.MinValue)))
                                    throw new OverflowException("Arithmetic operation resulted in an overflow.");
                                return DynamicTypes.ConvertNumeric(wide, t, isChecked);
                            });
                        }
                    case Expr.OnesComplement:
                        if (isEnum) return Make(xs => DynamicTypes.BoxInteger(t, ~DynamicTypes.Integer(xs[0], DynamicTypes.Element(t))));
                        {
                            Type p = DynamicTypes.PromoteUnary(t);
                            if (!DynamicTypes.IsIntegral(DynamicTypes.Element(p))) break;
                            return Make(xs => DynamicTypes.ConvertNumeric(
                                (object)~DynamicTypes.Integer(xs[0], DynamicTypes.Element(t)), p, false));
                        }
                    case Expr.Negate:
                    case Expr.UnaryPlus:
                        if (isEnum) break;
                        {
                            Type p = DynamicTypes.PromoteUnary(t);
                            bool negate = op == Expr.Negate;
                            if (negate && p == typeof(uint)) p = typeof(long);
                            if (negate && p == typeof(ulong)) throw DynamicTypes.Error("Operator '-' is ambiguous on an operand of type 'ulong'");
                            if (!negate) return Make(xs => DynamicTypes.ConvertNumeric(xs[0], p, false));
                            if (p == typeof(double)) return Make(xs => -(double)DynamicTypes.ConvertNumeric(xs[0], p, false));
                            if (p == typeof(float)) return Make(xs => -(float)DynamicTypes.ConvertNumeric(xs[0], p, false));
                            if (p == typeof(long))
                                return Make(xs =>
                                {
                                    long v = (long)DynamicTypes.ConvertNumeric(xs[0], p, false);
                                    return isChecked ? checked(-v) : unchecked(-v);
                                });
                            return Make(xs =>
                            {
                                int v = (int)DynamicTypes.ConvertNumeric(xs[0], p, false);
                                return isChecked ? checked(-v) : unchecked(-v);
                            });
                        }
                }
            }
            throw DynamicTypes.Error("Operator '" + Symbol(op, false) + "' cannot be applied to operand of type '" + N(t) + "'");
        }
    }
}
