using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpOS.Std.Exchange;

namespace SharpOS.Std.Pipes
{
    /// <summary>Marks a type that may travel through a native pipe (pipe spec Р16).</summary>
    /// <remarks>
    /// Pipes.Generator registers every marked type in this image's catalog:
    /// full name, fields with names and types, offsets measured at start-up from
    /// the real layout. Compile-time errors: a field that is a delegate, a
    /// pointer, or a class outside the catalog; a pipe end over an unmarked type.
    /// </remarks>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Enum, Inherited = false)]
    public sealed class MessageAttribute : Attribute
    {
    }

    /// <summary>
    /// A parameter that keeps its argument beyond the call — in a field, a static
    /// or a collection. The region analyzer refuses a region reference passed to it.
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class RetainsAttribute : Attribute
    {
    }

    /// <summary>
    /// The types this image lets through pipes (pipe spec Р20, Р21): every
    /// [Message] type, the strings, boxes and arrays they reach, each with a
    /// key computed from its real layout and its full name.
    /// </summary>
    /// <remarks>
    /// Filled once, on first use, by the built-in registrations below and the
    /// generated RegisterGenerated. Not by a module initializer: those do not run
    /// in the kernel.
    /// </remarks>
    public static unsafe partial class MessageCatalog
    {
        private static bool s_ready;
        private static List<string> s_problems;
        private static byte[] s_schema;

        /// <summary>Registrations that failed (a key or a table taken twice).</summary>
        public static List<string> Problems => s_problems ??= new List<string>();

        public static void Ensure()
        {
            if (s_ready) return;
            s_ready = true;
            RegisterBuiltins();
            RegisterGenerated();
            RegisterExtra?.Invoke();
        }

        /// <summary>Registrations an image adds by hand, run after the generated ones.</summary>
        public static Action RegisterExtra;

        static partial void RegisterGenerated();

        /// <summary>The description of every type in the catalog: what a writer declares.</summary>
        public static byte[] Schema
        {
            get
            {
                Ensure();
                return s_schema ??= RegionSchema.Build(TypeKeys.Declared);
            }
        }

        private static Dictionary<ulong, byte[]> s_writerSchemas;

        /// <summary>
        /// What a typed writer of the type declares (step196): the type and
        /// every type its messages can hold — its fields' types, theirs, the
        /// enums named. A field that may hold a type not known in advance
        /// (object, a class with subclasses in the catalog, a type outside it)
        /// makes it the whole catalog, as before. A reader parses only this.
        /// </summary>
        public static byte[] WriterSchema(ulong key)
        {
            Ensure();
            lock (s_closureLock)
            {
                s_writerSchemas ??= new Dictionary<ulong, byte[]>();
                if (s_writerSchemas.TryGetValue(key, out byte[] known)) return known;
                List<TypeKeys.Description> closure = Closure(key);
                byte[] schema = closure == null ? Schema : RegionSchema.Build(closure);
                s_writerSchemas[key] = schema;
                return schema;
            }
        }

        private static readonly object s_closureLock = new object();

        // Null when the closure is open: the whole catalog then.
        private static List<TypeKeys.Description> Closure(ulong key)
        {
            TypeKeys.Description root = TypeKeys.DescriptionOf(key);
            if (root == null) return null;
            var byName = new Dictionary<string, TypeKeys.Description>();
            foreach (TypeKeys.Description d in TypeKeys.Declared) byName[d.Name] = d;

            var result = new List<TypeKeys.Description>();
            var seen = new Dictionary<string, bool>();
            var queue = new List<TypeKeys.Description> { root };
            seen[root.Name] = true;
            for (int q = 0; q < queue.Count; q++)
            {
                TypeKeys.Description d = queue[q];
                if (!d.IsValueType && HasSubclass(d)) return null;
                result.Add(d);
                foreach (TypeKeys.Field f in d.Fields)
                {
                    if (f.Enum != null && !seen.ContainsKey(f.Enum) && byName.TryGetValue(f.Enum, out TypeKeys.Description e))
                    {
                        seen[f.Enum] = true;
                        queue.Add(e);
                    }
                    if (IsPrimitive(f.Type) || seen.ContainsKey(f.Type)) continue;
                    if (f.Type == "System.Object" || !byName.TryGetValue(f.Type, out TypeKeys.Description t)) return null;
                    seen[f.Type] = true;
                    queue.Add(t);
                }
            }
            return result;
        }

        private static bool IsPrimitive(string type)
            => type == "System.Boolean" || type == "System.Char" || type == "System.SByte" || type == "System.Byte"
               || type == "System.Int16" || type == "System.UInt16" || type == "System.Int32" || type == "System.UInt32"
               || type == "System.Int64" || type == "System.UInt64" || type == "System.Single" || type == "System.Double";

        // A class another catalog type derives from: a field of it may hold either.
        private static bool HasSubclass(TypeKeys.Description d)
        {
            if (!TypeKeys.TryTable(d.Key, out ulong table)) return true;
            foreach (TypeKeys.Description other in TypeKeys.Declared)
            {
                if (other == d || other.IsValueType || !TypeKeys.TryTable(other.Key, out ulong t)) continue;
                var mt = (SharpOS.Std.NoRuntime.GcMethodTable*)t;
                if (mt->IsArray) continue;
                for (SharpOS.Std.NoRuntime.GcMethodTable* b = mt->GetBaseType(); b != null; b = b->GetBaseType())
                    if ((ulong)b == table) return true;
            }
            return false;
        }

        /// <summary>The description of one type: what a typed reader declares.</summary>
        public static byte[] SchemaOf(ulong key)
        {
            Ensure();
            TypeKeys.Description d = TypeKeys.DescriptionOf(key);
            if (d == null) return null;
            var one = new List<TypeKeys.Description>();
            one.Add(d);
            return RegionSchema.Build(one);
        }

        public static ulong TableOf(Type type) => (ulong)type._handle;

        /// <summary>
        /// Index of the first declared description whose object no longer has
        /// the Description table, or of its field array; -1 when all are whole.
        /// A detector for a collector that swept the catalog.
        /// </summary>
        public static int Verify(out ulong address, out ulong word)
        {
            address = word = 0;
            if (!s_ready) return -1;
            ulong descriptionTable = TableOf(typeof(TypeKeys.Description));
            ulong fieldsTable = TableOf(typeof(TypeKeys.Field[]));
            var all = TypeKeys.Declared;
            for (int i = 0; i < all.Count; i++)
            {
                object d = all[i];
                address = Unsafe.As<object, ulong>(ref d);
                word = *(ulong*)address & ~1UL;
                if (word != descriptionTable) return i;
                object f = all[i].Fields;
                address = Unsafe.As<object, ulong>(ref f);
                word = *(ulong*)address & ~1UL;
                if (word != fieldsTable) return 1000 + i;
            }
            return -1;
        }

        /// <summary>The key of a type; 0 when it is not in the catalog.</summary>
        public static ulong KeyOf(Type type)
        {
            Ensure();
            return TypeKeys.TryKey(TableOf(type), out ulong key) ? key : 0;
        }

        public static bool Contains(ulong table)
        {
            Ensure();
            return TypeKeys.TryKey(table, out _);
        }

        /// <summary>An instance of a class with no constructor run: what field offsets are measured on.</summary>
        public static object Witness(Type type)
        {
            var mt = (SharpOS.Std.NoRuntime.GcMethodTable*)TableOf(type);
            void* raw = SharpOS.Std.NoRuntime.GcHeap.AllocateObject(mt->BaseSize, mt);
            if (raw == null) throw new OutOfMemoryException();
            nint address = (nint)raw;
            return Unsafe.As<nint, object>(ref address);
        }

        public static TypeKeys.Field Field<T>(string name, string type, object owner, ref T field)
            => new TypeKeys.Field(name, type, TypeKeys.Offset(owner, ref field));

        /// <summary>The same, for a field declared as an enum: <paramref name="type"/> is its underlying number.</summary>
        public static TypeKeys.Field Field<T>(string name, string type, object owner, ref T field, string enumType)
            => new TypeKeys.Field(name, type, TypeKeys.Offset(owner, ref field), enumType);

        /// <summary>An enum, by a boxed value: its number's layout, and its members' names in the description.</summary>
        public static void RegisterEnum(string fullName, object box, string underlying, string[] names, long[] values)
        {
            if (TypeKeys.Declare(fullName, box, new[] { new TypeKeys.Field("value", underlying, 8) }) is ulong key && key != 0)
                TypeKeys.DescribeEnum(key, names, values);
            else
                Problems.Add(fullName + ": key or table already taken");
            s_schema = null;
        }

        public static void Register(string fullName, object witness, TypeKeys.Field[] fields)
        {
            if (TypeKeys.Declare(fullName, witness, fields) == 0)
                Problems.Add(fullName + ": key or table already taken");
            s_schema = null;
        }

        private static void RegisterBuiltins()
        {
            string text = "w";
            Register("System.String", text, new[]
            {
                new TypeKeys.Field("Length", "System.Int32", 8),
                Field("[]", "System.Char", text, ref text.GetPinnableReference()),
            });

            Box((object)0, "System.Int32");
            Box((object)0L, "System.Int64");
            Box((object)0u, "System.UInt32");
            Box((object)0UL, "System.UInt64");
            Box((object)(short)0, "System.Int16");
            Box((object)(ushort)0, "System.UInt16");
            Box((object)(byte)0, "System.Byte");
            Box((object)(sbyte)0, "System.SByte");
            Box((object)false, "System.Boolean");
            Box((object)'c', "System.Char");
            Box((object)0.0, "System.Double");
            Box((object)0.0f, "System.Single");

            RegisterArray(new int[1], "System.Int32");
            RegisterArray(new long[1], "System.Int64");
            RegisterArray(new uint[1], "System.UInt32");
            RegisterArray(new ulong[1], "System.UInt64");
            RegisterArray(new short[1], "System.Int16");
            RegisterArray(new ushort[1], "System.UInt16");
            RegisterArray(new sbyte[1], "System.SByte");
            RegisterArray(new byte[1], "System.Byte");
            RegisterArray(new char[1], "System.Char");
            RegisterArray(new bool[1], "System.Boolean");
            RegisterArray(new double[1], "System.Double");
            RegisterArray(new float[1], "System.Single");
            RegisterArray(new string[1], "System.String");
            RegisterArray(new object[1], "System.Object");

            // Rank 2: method table, length, two bounds and two lower bounds, then data.
            Register("System.Int32[,]", new int[1, 1], new[] { new TypeKeys.Field("[]", "System.Int32", 32) });
        }

        private static void Box(object box, string name)
            => Register(name, box, new[] { new TypeKeys.Field("value", name, 8) });

        /// <summary>Registers an array type by an instance, element type named.</summary>
        public static void RegisterArray<T>(T[] witness, string elementName)
            => RegisterArray(witness, elementName, elementName);

        /// <summary>
        /// The same, when the array's name and its elements' type differ: an
        /// array of an enum is named by the enum and holds its underlying number.
        /// </summary>
        public static void RegisterArray<T>(T[] witness, string elementName, string elementType)
            => Register(elementName + "[]", witness,
                        new[] { Field("[]", elementType, witness, ref MemoryMarshal.GetArrayDataReference(witness),
                                      elementType == elementName ? null : elementName) });
    }
}
