// System.Reflection's member types — the shapes of BCL's MemberInfo,
// MethodBase, MethodInfo, ConstructorInfo, PropertyInfo, FieldInfo,
// ParameterInfo, ICustomAttributeProvider, BindingFlags, MemberTypes,
// Binder, ParameterModifier (dotnet/runtime v8.0, System.Private.CoreLib,
// MIT), member names and values as BCL's.
//
// Partial by necessity: the image carries no reflection metadata, so there is
// nothing to enumerate and no instance of these types is ever made here.
// Type.GetProperty/GetField/GetConstructor/GetMethod answer null — what BCL
// answers for a member that does not exist — and the Get*s plurals empty
// arrays. They exist so BCL code that names them compiles: System.Text.Json's
// metadata API (JsonPropertyInfo.AttributeProvider) and the source
// generator's output (AttributeProviderFactory = () => typeof(T).GetProperty(...)),
// which is only ever asked for attributes, and gets null.
//
// Recorded in docs/nativeaot-nostd-kernel-limits.md (reflection).

using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace System.Reflection
{
    public interface ICustomAttributeProvider
    {
        object[] GetCustomAttributes(bool inherit);
        object[] GetCustomAttributes(Type attributeType, bool inherit);
        bool IsDefined(Type attributeType, bool inherit);
    }

    [Flags]
    public enum BindingFlags
    {
        Default = 0x00,
        IgnoreCase = 0x01,
        DeclaredOnly = 0x02,
        Instance = 0x04,
        Static = 0x08,
        Public = 0x10,
        NonPublic = 0x20,
        FlattenHierarchy = 0x40,
        InvokeMethod = 0x0100,
        CreateInstance = 0x0200,
        GetField = 0x0400,
        SetField = 0x0800,
        GetProperty = 0x1000,
        SetProperty = 0x2000,
        PutDispProperty = 0x4000,
        PutRefDispProperty = 0x8000,
        ExactBinding = 0x010000,
        SuppressChangeType = 0x020000,
        OptionalParamBinding = 0x040000,
        IgnoreReturn = 0x01000000,
        DoNotWrapExceptions = 0x02000000,
    }

    [Flags]
    public enum MemberTypes
    {
        Constructor = 0x01,
        Event = 0x02,
        Field = 0x04,
        Method = 0x08,
        Property = 0x10,
        TypeInfo = 0x20,
        Custom = 0x40,
        NestedType = 0x80,
        All = Constructor | Event | Field | Method | Property | TypeInfo | NestedType,
    }

    public abstract class MemberInfo : ICustomAttributeProvider
    {
        protected MemberInfo() { }

        public abstract MemberTypes MemberType { get; }
        public abstract string Name { get; }
        public abstract Type? DeclaringType { get; }
        public abstract Type? ReflectedType { get; }

        public abstract object[] GetCustomAttributes(bool inherit);
        public abstract object[] GetCustomAttributes(Type attributeType, bool inherit);
        public abstract bool IsDefined(Type attributeType, bool inherit);

        public virtual int MetadataToken => throw new InvalidOperationException();
    }

    public abstract class MethodBase : MemberInfo
    {
        protected MethodBase() { }

        public abstract ParameterInfo[] GetParameters();

        public virtual bool IsPublic => false;
        public virtual bool IsPrivate => false;
        public virtual bool IsFamily => false;
        public virtual bool IsAssembly => false;
        public virtual bool IsFamilyOrAssembly => false;
        public virtual bool IsStatic => false;
        public virtual bool IsAbstract => false;
        public virtual bool IsVirtual => false;
        public virtual bool IsFinal => false;
        public virtual bool IsGenericMethod => false;
        public virtual bool IsGenericMethodDefinition => false;
        public virtual bool IsConstructor => false;

        public virtual Type[] GetGenericArguments() => throw new NotSupportedException();

        public object? Invoke(object? obj, object?[]? parameters) => Invoke(obj, BindingFlags.Default, null, parameters, null);

        public abstract object? Invoke(object? obj, BindingFlags invokeAttr, Binder? binder, object?[]? parameters, CultureInfo? culture);
    }

    public abstract class MethodInfo : MethodBase
    {
        protected MethodInfo() { }

        public override MemberTypes MemberType => MemberTypes.Method;
        public virtual Type ReturnType => throw new NotSupportedException();
        public virtual ParameterInfo ReturnParameter => throw new NotSupportedException();
        public virtual MethodInfo GetGenericMethodDefinition() => throw new NotSupportedException();
        public virtual MethodInfo MakeGenericMethod(params Type[] typeArguments) => throw new NotSupportedException();
        public abstract MethodInfo GetBaseDefinition();
        public virtual Delegate CreateDelegate(Type delegateType) => throw new NotSupportedException();
    }

    public abstract class ConstructorInfo : MethodBase
    {
        public static readonly string ConstructorName = ".ctor";
        public static readonly string TypeConstructorName = ".cctor";

        protected ConstructorInfo() { }

        public override MemberTypes MemberType => MemberTypes.Constructor;
        public object Invoke(object?[]? parameters) => Invoke(BindingFlags.Default, null, parameters, null);
        public abstract object Invoke(BindingFlags invokeAttr, Binder? binder, object?[]? parameters, CultureInfo? culture);
    }

    public abstract class PropertyInfo : MemberInfo
    {
        protected PropertyInfo() { }

        public override MemberTypes MemberType => MemberTypes.Property;
        public abstract Type PropertyType { get; }
        public abstract bool CanRead { get; }
        public abstract bool CanWrite { get; }
        public MethodInfo? GetMethod => GetGetMethod(nonPublic: true);
        public MethodInfo? SetMethod => GetSetMethod(nonPublic: true);
        public abstract MethodInfo? GetGetMethod(bool nonPublic);
        public abstract MethodInfo? GetSetMethod(bool nonPublic);
        public abstract ParameterInfo[] GetIndexParameters();
        public object? GetValue(object? obj) => GetValue(obj, null);
        public virtual object? GetValue(object? obj, object?[]? index) => throw new NotSupportedException();
        public void SetValue(object? obj, object? value) => SetValue(obj, value, null);
        public virtual void SetValue(object? obj, object? value, object?[]? index) => throw new NotSupportedException();
    }

    public abstract class FieldInfo : MemberInfo
    {
        protected FieldInfo() { }

        public override MemberTypes MemberType => MemberTypes.Field;
        public abstract Type FieldType { get; }
        public virtual bool IsPublic => false;
        public virtual bool IsPrivate => false;
        public virtual bool IsFamily => false;
        public virtual bool IsAssembly => false;
        public virtual bool IsStatic => false;
        public virtual bool IsInitOnly => false;
        public virtual bool IsLiteral => false;
        public abstract object? GetValue(object? obj);
        public void SetValue(object? obj, object? value) => SetValue(obj, value, BindingFlags.Default, null, null);
        public abstract void SetValue(object? obj, object? value, BindingFlags invokeAttr, Binder? binder, CultureInfo? culture);
    }

    public class ParameterInfo : ICustomAttributeProvider
    {
        protected ParameterInfo() { }

        public virtual string? Name => null;
        public virtual Type ParameterType => throw new NotSupportedException();
        public virtual int Position => -1;
        public virtual MemberInfo Member => throw new NotSupportedException();
        public virtual bool HasDefaultValue => false;
        public virtual object? DefaultValue => null;
        public virtual bool IsOptional => false;
        public virtual bool IsIn => false;
        public virtual bool IsOut => false;

        public virtual object[] GetCustomAttributes(bool inherit) => Array.Empty<object>();
        public virtual object[] GetCustomAttributes(Type attributeType, bool inherit) => Array.Empty<object>();
        public virtual bool IsDefined(Type attributeType, bool inherit) => false;
    }

    public abstract class Binder
    {
        protected Binder() { }
    }

    public readonly struct ParameterModifier
    {
        private readonly bool[] _byRef;

        public ParameterModifier(int parameterCount)
        {
            if (parameterCount <= 0) throw new ArgumentException("Must specify one or more parameters.");
            _byRef = new bool[parameterCount];
        }

        public bool this[int index]
        {
            get => _byRef[index];
            set => _byRef[index] = value;
        }
    }

    public static class CustomAttributeExtensions
    {
        public static T? GetCustomAttribute<T>(this MemberInfo element) where T : Attribute => GetCustomAttribute<T>(element, inherit: true);

        public static T? GetCustomAttribute<T>(this MemberInfo element, bool inherit) where T : Attribute
        {
            foreach (object attribute in element.GetCustomAttributes(typeof(T), inherit))
                if (attribute is T typed) return typed;
            return null;
        }

        public static T? GetCustomAttribute<T>(this ParameterInfo element) where T : Attribute
        {
            foreach (object attribute in element.GetCustomAttributes(typeof(T), true))
                if (attribute is T typed) return typed;
            return null;
        }

        public static bool IsDefined(this MemberInfo element, Type attributeType) => element.IsDefined(attributeType, inherit: true);

        public static bool IsDefined(this MemberInfo element, Type attributeType, bool inherit) => element.IsDefined(attributeType, inherit);

        public static System.Collections.Generic.IEnumerable<T> GetCustomAttributes<T>(this MemberInfo element, bool inherit) where T : Attribute
        {
            foreach (object attribute in element.GetCustomAttributes(typeof(T), inherit))
                if (attribute is T typed) yield return typed;
        }
    }
}

namespace System
{
    public abstract partial class Type
    {
        // No reflection metadata in the image: no member is ever found
        // (Runtime/Reflection.Members.cs).
        public System.Reflection.PropertyInfo? GetProperty(string name) => GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);

        public System.Reflection.PropertyInfo? GetProperty(string name, System.Reflection.BindingFlags bindingAttr)
        {
            ArgumentNullException.ThrowIfNull(name);
            return null;
        }

        public System.Reflection.PropertyInfo? GetProperty(string name, System.Reflection.BindingFlags bindingAttr, System.Reflection.Binder? binder, Type? returnType, Type[] types, System.Reflection.ParameterModifier[]? modifiers)
        {
            ArgumentNullException.ThrowIfNull(name);
            ArgumentNullException.ThrowIfNull(types);
            return null;
        }

        public System.Reflection.FieldInfo? GetField(string name) => GetField(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);

        public System.Reflection.FieldInfo? GetField(string name, System.Reflection.BindingFlags bindingAttr)
        {
            ArgumentNullException.ThrowIfNull(name);
            return null;
        }

        public System.Reflection.ConstructorInfo? GetConstructor(Type[] types) => GetConstructor(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance, null, types, null);

        public System.Reflection.ConstructorInfo? GetConstructor(System.Reflection.BindingFlags bindingAttr, System.Reflection.Binder? binder, Type[] types, System.Reflection.ParameterModifier[]? modifiers)
        {
            ArgumentNullException.ThrowIfNull(types);
            return null;
        }

        public System.Reflection.MethodInfo? GetMethod(string name) => GetMethod(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static);

        public System.Reflection.MethodInfo? GetMethod(string name, System.Reflection.BindingFlags bindingAttr)
        {
            ArgumentNullException.ThrowIfNull(name);
            return null;
        }

        public System.Reflection.PropertyInfo[] GetProperties() => Array.Empty<System.Reflection.PropertyInfo>();

        public System.Reflection.PropertyInfo[] GetProperties(System.Reflection.BindingFlags bindingAttr) => Array.Empty<System.Reflection.PropertyInfo>();

        public System.Reflection.FieldInfo[] GetFields() => Array.Empty<System.Reflection.FieldInfo>();

        public System.Reflection.FieldInfo[] GetFields(System.Reflection.BindingFlags bindingAttr) => Array.Empty<System.Reflection.FieldInfo>();

        public System.Reflection.ConstructorInfo[] GetConstructors() => Array.Empty<System.Reflection.ConstructorInfo>();

        public System.Reflection.ConstructorInfo[] GetConstructors(System.Reflection.BindingFlags bindingAttr) => Array.Empty<System.Reflection.ConstructorInfo>();
    }
}
