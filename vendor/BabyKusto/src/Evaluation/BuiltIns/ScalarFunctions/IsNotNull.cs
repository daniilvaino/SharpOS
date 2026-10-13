// SharpOS cut (step205): isnotnull was missing from BabyKusto, and isnull had
// no dynamic overload. Over Expandos every name is a dynamic column, and
// "has this message the key" — isnotnull(Late) — is the first question asked.

using System.Diagnostics;
using System.Text.Json.Nodes;
using Kusto.Language.Symbols;

namespace BabyKusto.Core.Evaluation.BuiltIns.Impl
{
    internal class IsNullDynamicFunctionImpl : IScalarFunctionImpl
    {
        public ScalarResult InvokeScalar(ScalarResult[] arguments)
        {
            Debug.Assert(arguments.Length == 1);
            return new ScalarResult(ScalarTypes.Bool, arguments[0].Value is null);
        }

        public ColumnarResult InvokeColumnar(ColumnarResult[] arguments)
        {
            Debug.Assert(arguments.Length == 1);
            var valueCol = (Column<JsonNode?>)(arguments[0].Column);

            var data = new bool?[valueCol.RowCount];
            for (int i = 0; i < valueCol.RowCount; i++)
            {
                data[i] = valueCol[i] is null;
            }
            return new ColumnarResult(Column.Create(ScalarTypes.Bool, data));
        }
    }

    /// <summary>isnotnull(x): the matching isnull's answer, negated.</summary>
    internal class IsNotNullFunctionImpl : IScalarFunctionImpl
    {
        private readonly IScalarFunctionImpl _isNull;

        public IsNotNullFunctionImpl(IScalarFunctionImpl isNull)
        {
            _isNull = isNull;
        }

        public ScalarResult InvokeScalar(ScalarResult[] arguments)
        {
            ScalarResult r = _isNull.InvokeScalar(arguments);
            return new ScalarResult(ScalarTypes.Bool, r.Value is bool b ? !b : null);
        }

        public ColumnarResult InvokeColumnar(ColumnarResult[] arguments)
        {
            var isNull = (Column<bool?>)(_isNull.InvokeColumnar(arguments).Column);
            var data = new bool?[isNull.RowCount];
            for (int i = 0; i < isNull.RowCount; i++)
            {
                bool? v = isNull[i];
                data[i] = v.HasValue ? !v.Value : null;
            }
            return new ColumnarResult(Column.Create(ScalarTypes.Bool, data));
        }
    }
}
