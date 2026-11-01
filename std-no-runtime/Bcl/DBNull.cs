// System.DBNull — from dotnet/runtime v8.0.27 System.Private.CoreLib/src/
// System/DBNull.cs (MIT): the single Value, its ToString and type code.
// SharpOS cut: ISerializable (UnitySerializationHolder) and IConvertible —
// std has neither interface; every IConvertible member of the original
// throws InvalidCastException anyway. First caller: System.Text.Json's
// ReflectionExtensions (a parameter default of DBNull means "none").

namespace System
{
    public sealed class DBNull
    {
        private DBNull() { }

        public static readonly DBNull Value = new DBNull();

        public override string ToString() => string.Empty;

        public string ToString(IFormatProvider? provider) => string.Empty;

        public TypeCode GetTypeCode() => TypeCode.DBNull;
    }
}
