// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// SharpOS cut: comparisons with a dynamic operand — a file of SharpOS's.
// Kusto.Language binds `Value.Level >= 3`, `Value.Kind == "odd"` and two
// dynamics against each other; upstream BabyKusto has no implementation for
// those signatures (the operator was "not implemented for argument types").
// The value is taken out of its node and compared by kind: numbers as numbers
// (as longs when both are whole), strings ordinally, bools by == and !=,
// objects and arrays by == and != on their JSON text. Against null the result
// is null; kinds that differ are unequal and have no order (null).

using System;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Kusto.Language.Symbols;

namespace BabyKusto.Core.Evaluation.BuiltIns.Impl
{
    internal enum DynamicComparison
    {
        Equal,
        NotEqual,
        LessThan,
        LessThanOrEqual,
        GreaterThan,
        GreaterThanOrEqual,
    }

    internal sealed class DynamicComparisonOperatorImpl : IScalarFunctionImpl
    {
        private readonly DynamicComparison _op;

        public DynamicComparisonOperatorImpl(DynamicComparison op)
        {
            _op = op;
        }

        public ScalarResult InvokeScalar(ScalarResult[] arguments)
        {
            Debug.Assert(arguments.Length == 2);
            return new ScalarResult(ScalarTypes.Bool, Compare(arguments[0].Value, arguments[1].Value));
        }

        public ColumnarResult InvokeColumnar(ColumnarResult[] arguments)
        {
            Debug.Assert(arguments.Length == 2);
            Debug.Assert(arguments[0].Column.RowCount == arguments[1].Column.RowCount);
            Array left = arguments[0].Column.RawData;
            Array right = arguments[1].Column.RawData;
            var data = new bool?[arguments[0].Column.RowCount];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = Compare(left.GetValue(i), right.GetValue(i));
            }
            return new ColumnarResult(Column.Create(ScalarTypes.Bool, data));
        }

        private bool? Compare(object? a, object? b)
        {
            a = DynamicValues.Unwrap(a);
            b = DynamicValues.Unwrap(b);
            if (a == null || b == null)
            {
                return null;
            }

            bool equality = _op == DynamicComparison.Equal || _op == DynamicComparison.NotEqual;
            int sign;
            if ((a is long || a is double) && (b is long || b is double))
            {
                sign = a is long la && b is long lb ? la.CompareTo(lb) : AsDouble(a).CompareTo(AsDouble(b));
            }
            else if (a is string sa && b is string sb)
            {
                sign = string.CompareOrdinal(sa, sb);
            }
            else if (equality && a is bool ba && b is bool bb)
            {
                sign = ba == bb ? 0 : 1;
            }
            else if (equality && a is JsonNode na && b is JsonNode nb)
            {
                sign = string.CompareOrdinal(na.ToJsonString(), nb.ToJsonString()) == 0 ? 0 : 1;
            }
            else
            {
                return equality ? _op == DynamicComparison.NotEqual : null;
            }

            switch (_op)
            {
                case DynamicComparison.Equal: return sign == 0;
                case DynamicComparison.NotEqual: return sign != 0;
                case DynamicComparison.LessThan: return sign < 0;
                case DynamicComparison.LessThanOrEqual: return sign <= 0;
                case DynamicComparison.GreaterThan: return sign > 0;
                default: return sign >= 0;
            }
        }

        private static double AsDouble(object value) => value is long l ? l : (double)value;
    }
}
