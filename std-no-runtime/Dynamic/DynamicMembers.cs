using System;
using System.Collections.Generic;

namespace SharpOS.Std.Dynamic
{
    /// <summary>
    /// Closed generic types whose members `dynamic` may reach although the
    /// image's source never names them: <c>[assembly: DynamicTypes(typeof(Pair&lt;int, string&gt;))]</c>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class DynamicTypesAttribute : Attribute
    {
        public DynamicTypesAttribute(params Type[] types) { }
    }

    /// <summary>
    /// What a value can do for a dynamic call site without being asked by
    /// name in metadata: Expando and View answer for themselves.
    /// </summary>
    public interface IDynamicObject
    {
        bool TryGetMember(string name, out object value);
        bool TrySetMember(string name, object value);
        bool TryGetIndex(object[] indexes, out object value);
        bool TrySetIndex(object[] indexes, object value);
        bool TryInvokeMember(string name, object[] args, out object result);
        bool TryConvert(Type type, bool isExplicit, out object result);
    }

    /// <summary>
    /// An IDynamicObject whose members sit at fixed places for a given shape:
    /// a call site keeps the place found for a shape and asks no name again.
    /// A view's shape is its type key in the pipe's description.
    /// </summary>
    public interface IDynamicShape
    {
        object Shape { get; }
        bool TryCell(string name, out object cell);
        object GetCell(object cell);
        void SetCell(object cell, object value);
    }

    /// <summary>A field or property, as the generator registered it.</summary>
    public sealed class DynamicMember
    {
        public Type Owner;
        public string Name;
        public Type Type;
        public bool IsStatic;
        public Func<object, object> Get;        // null: no getter
        public Action<object, object> Set;      // null: no setter
    }

    /// <summary>A method, constructor, indexer, operator or conversion, as the generator registered it.</summary>
    public sealed class DynamicMethod
    {
        public Type Owner;
        public string Name;                     // ".ctor", "Item", "op_Addition", "op_Implicit"…
        public bool IsStatic;
        public Type[] Parameters;
        public bool HasParamsArray;             // last parameter is `params T[]`
        public Type ParamsElement;
        public Type Returns;                    // null: void
        public Func<object, object[], object> Invoke;
        public int Required;                    // parameters without a default value
        public int GenericArity;                // a generic method: its type parameters
        public Type[] TypeArguments;            // a generic method's instance: what it was closed over

        /// <summary>An optional parameter left out: the invoker uses its default.</summary>
        public static object Missing => s_missing ??= new object();
        private static object s_missing;
    }

    /// <summary>
    /// The members an image's dynamic call sites may reach, by name; filled by
    /// the generator from the names its call sites use (RegisterGenerated).
    /// Also the type names the binder's messages need — there is no
    /// reflection to ask.
    /// </summary>
    public static partial class DynamicMembers
    {
        private static Dictionary<string, List<DynamicMember>> s_members;
        private static Dictionary<string, List<DynamicMethod>> s_methods;
        private static Dictionary<Type, string> s_names;
        private static Dictionary<Type, Type> s_nullables;
        private static bool s_ready;
        private static object s_lock;

        static partial void RegisterGenerated();

        // Filled once, under a lock: a thread preempted halfway must not leave the tables half-made for another.
        private static void Ensure()
        {
            if (s_ready) return;
            if (s_lock == null) System.Threading.Interlocked.CompareExchange(ref s_lock, new object(), null);
            lock (s_lock)
            {
                if (s_ready) return;
                s_members = new Dictionary<string, List<DynamicMember>>();
                s_methods = new Dictionary<string, List<DynamicMethod>>();
                s_names = new Dictionary<Type, string>();
                s_nullables = new Dictionary<Type, Type>();
                NameBuiltins();
                RegisterGenerated();
                s_ready = true;
            }
        }

        public static void Member(Type owner, string name, Type type, bool isStatic, Func<object, object> get, Action<object, object> set)
        {
            if (!s_members.TryGetValue(name, out List<DynamicMember> list))
                s_members[name] = list = new List<DynamicMember>();
            list.Add(new DynamicMember { Owner = owner, Name = name, Type = type, IsStatic = isStatic, Get = get, Set = set });
        }

        public static void Method(Type owner, string name, bool isStatic, Type[] parameters, Type paramsElement,
                                  Type returns, int required, Func<object, object[], object> invoke)
            => Add(new DynamicMethod
            {
                Owner = owner, Name = name, IsStatic = isStatic, Parameters = parameters,
                HasParamsArray = paramsElement != null, ParamsElement = paramsElement,
                Returns = returns, Required = required, Invoke = invoke,
            });

        /// <summary>A generic method closed over type arguments a call site named.</summary>
        public static void Generic(Type owner, string name, bool isStatic, Type[] typeArguments, Type[] parameters, Type paramsElement,
                                   Type returns, int required, Func<object, object[], object> invoke)
            => Add(new DynamicMethod
            {
                Owner = owner, Name = name, IsStatic = isStatic, Parameters = parameters,
                HasParamsArray = paramsElement != null, ParamsElement = paramsElement,
                Returns = returns, Required = required, Invoke = invoke,
                GenericArity = typeArguments.Length, TypeArguments = typeArguments,
            });

        /// <summary>A generic method no call site closed: binding it needs type inference, which is not done.</summary>
        public static void NeedsInference(Type owner, string name, bool isStatic, int arity)
            => Add(new DynamicMethod
            {
                Owner = owner, Name = name, IsStatic = isStatic, Parameters = new Type[0], GenericArity = arity,
            });

        private static void Add(DynamicMethod m)
        {
            if (!s_methods.TryGetValue(m.Name, out List<DynamicMethod> list))
                s_methods[m.Name] = list = new List<DynamicMethod>();
            list.Add(m);
        }

        /// <summary>T? and its T: the binder converts to T and the invoker wraps.</summary>
        public static void Nullable(Type nullable, Type underlying) => s_nullables[nullable] = underlying;

        internal static Type NullableOf(Type nullable)
        {
            Ensure();
            return s_nullables.TryGetValue(nullable, out Type t) ? t : null;
        }

        public static void Name(Type type, string name) => s_names[type] = name;

        /// <summary>A property of an anonymous type: the witness fixes T, which generated code cannot name.</summary>
        public static void Anonymous<T>(T witness, string name, Type type, Func<T, object> get) where T : class
        {
            Member(typeof(T), name, type, false, o => get(System.Runtime.CompilerServices.Unsafe.As<T>(o)), null);
            s_names[typeof(T)] = "<anonymous type>";
        }

        internal static List<DynamicMember> Members(string name)
        {
            Ensure();
            return s_members.TryGetValue(name, out List<DynamicMember> list) ? list : null;
        }

        internal static List<DynamicMethod> Methods(string name)
        {
            Ensure();
            return s_methods.TryGetValue(name, out List<DynamicMethod> list) ? list : null;
        }

        /// <summary>A type's name for a message: the generator's, then what the table says.</summary>
        internal static string NameOf(Type type)
        {
            Ensure();
            if (type == null) return "null";
            if (s_names.TryGetValue(type, out string name)) return name;
            return DynamicTypes.Describe(type);
        }

        private static void NameBuiltins()
        {
            s_names[typeof(object)] = "object";
            s_names[typeof(string)] = "string";
            s_names[typeof(bool)] = "bool";
            s_names[typeof(char)] = "char";
            s_names[typeof(sbyte)] = "sbyte";
            s_names[typeof(byte)] = "byte";
            s_names[typeof(short)] = "short";
            s_names[typeof(ushort)] = "ushort";
            s_names[typeof(int)] = "int";
            s_names[typeof(uint)] = "uint";
            s_names[typeof(long)] = "long";
            s_names[typeof(ulong)] = "ulong";
            s_names[typeof(float)] = "float";
            s_names[typeof(double)] = "double";
        }
    }
}
