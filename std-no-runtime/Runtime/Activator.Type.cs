// Activator.CreateInstance(Type, ...) — BCL's overloads (step224, System.Text
// .Json's converter factories). A value type is created as BCL creates it
// without a constructor to call: a zeroed box (RuntimeImports.RhNewObject).
// A reference type needs its constructor found by reflection metadata, which
// the image does not carry — that is MissingMethodException-shaped, so it is
// NotSupported, naming the generic CreateInstance<T>() that does work.

using System.Globalization;
using System.Reflection;

namespace System
{
    public static partial class Activator
    {
        public static object? CreateInstance(Type type) => CreateInstance(type, nonPublic: false);

        public static unsafe object? CreateInstance(Type type, bool nonPublic)
        {
            ArgumentNullException.ThrowIfNull(type);
            _ = nonPublic;
            if (type.IsValueType && !type.IsGenericTypeDefinition)
                return System.Runtime.RuntimeImports.RhNewObject(new EETypePtr((Internal.Runtime.MethodTable*)type._handle));
            throw ConstructorUnavailable(type);
        }

        public static object? CreateInstance(Type type, params object?[]? args) =>
            args is null || args.Length == 0 ? CreateInstance(type) : throw ConstructorUnavailable(type);

        public static object? CreateInstance(Type type, BindingFlags bindingAttr, Binder? binder, object?[]? args, CultureInfo? culture) =>
            args is null || args.Length == 0 ? CreateInstance(type, nonPublic: (bindingAttr & BindingFlags.NonPublic) != 0) : throw ConstructorUnavailable(type);

        private static NotSupportedException ConstructorUnavailable(Type type) =>
            new NotSupportedException(
                "Activator.CreateInstance(Type) cannot find a constructor of '" + type.ToString() +
                "': there is no reflection metadata on SharpOS. Activator.CreateInstance<T>() works.");
    }
}
