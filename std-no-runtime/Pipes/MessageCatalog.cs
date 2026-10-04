using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpOS.Std.Exchange;

namespace SharpOS.Std.Pipes
{
    /// <summary>Marks a type that may travel through a native pipe (pipe spec Р16).</summary>
    /// <remarks>
    /// SharpOS.Generators registers every marked type in this image's catalog:
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
            => Register(elementName + "[]", witness,
                        new[] { Field("[]", elementName, witness, ref MemoryMarshal.GetArrayDataReference(witness)) });
    }
}
