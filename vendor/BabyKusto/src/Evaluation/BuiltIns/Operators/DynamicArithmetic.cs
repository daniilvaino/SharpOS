// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

// SharpOS cut: arithmetic with a dynamic operand — a file of SharpOS's.
// Kusto.Language types `Value.N * 2` by the other operand (long against a
// long or an int, real against a real) and two dynamics as dynamic; upstream
// BabyKusto had no implementation. The dynamic's value is taken out of its
// node (DynamicValues); against a typed operand it becomes that type — a real
// truncated to a long, as tolong() does — and two dynamics give a long when
// both are whole, a real otherwise. A value that is not a number is null, as
// is a division or a remainder by zero between longs.

using System;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Kusto.Language.Symbols;

namespace BabyKusto.Core.Evaluation.BuiltIns.Impl
{
    internal enum DynamicArithmetic
    {
        Add,
        Subtract,
        Multiply,
        Divide,
        Modulo,
    }

    internal sealed class DynamicArithmeticOperatorImpl : IScalarFunctionImpl
    {
        private readonly DynamicArithmetic _op;
        private readonly TypeSymbol _result;   // Long, Real or Dynamic

        public DynamicArithmeticOperatorImpl(DynamicArithmetic op, TypeSymbol result)
        {
            _op = op;
            _result = result;
        }

        public ScalarResult InvokeScalar(ScalarResult[] arguments)
        {
            Debug.Assert(arguments.Length == 2);
            return new ScalarResult(_result, Compute(arguments[0].Value, arguments[1].Value));
        }

        public ColumnarResult InvokeColumnar(ColumnarResult[] arguments)
        {
            Debug.Assert(arguments.Length == 2);
            Debug.Assert(arguments[0].Column.RowCount == arguments[1].Column.RowCount);
            Array left = arguments[0].Column.RawData;
            Array right = arguments[1].Column.RawData;
            int n = arguments[0].Column.RowCount;
            if (_result == ScalarTypes.Long)
            {
                var data = new long?[n];
                for (int i = 0; i < n; i++) data[i] = (long?)Compute(left.GetValue(i), right.GetValue(i));
                return new ColumnarResult(Column.Create(ScalarTypes.Long, data));
            }
            if (_result == ScalarTypes.Real)
            {
                var data = new double?[n];
                for (int i = 0; i < n; i++) data[i] = (double?)Compute(left.GetValue(i), right.GetValue(i));
                return new ColumnarResult(Column.Create(ScalarTypes.Real, data));
            }
            var nodes = new JsonNode?[n];
            for (int i = 0; i < n; i++) nodes[i] = (JsonNode?)Compute(left.GetValue(i), right.GetValue(i));
            return new ColumnarResult(Column.Create(ScalarTypes.Dynamic, nodes));
        }

        // The result as the result type holds it: a long, a double or a node; or null.
        private object? Compute(object? a, object? b)
        {
            a = DynamicValues.Unwrap(a);
            b = DynamicValues.Unwrap(b);
            if (!(a is long || a is double) || !(b is long || b is double))
            {
                return null;
            }

            if (_result == ScalarTypes.Long)
            {
                return Whole(AsLong(a), AsLong(b));
            }
            if (_result == ScalarTypes.Real)
            {
                return Real(AsDouble(a), AsDouble(b));
            }
            if (a is long la && b is long lb)
            {
                long? whole = Whole(la, lb);
                return whole.HasValue ? JsonValue.Create(whole.Value) : null;
            }
            double? real = Real(AsDouble(a), AsDouble(b));
            return real.HasValue ? JsonValue.Create(real.Value) : null;
        }

        private long? Whole(long x, long y)
        {
            switch (_op)
            {
                case DynamicArithmetic.Add: return x + y;
                case DynamicArithmetic.Subtract: return x - y;
                case DynamicArithmetic.Multiply: return x * y;
                case DynamicArithmetic.Divide: return y == 0 ? null : x / y;
                default: return y == 0 ? null : x % y;
            }
        }

        private double? Real(double x, double y)
        {
            switch (_op)
            {
                case DynamicArithmetic.Add: return x + y;
                case DynamicArithmetic.Subtract: return x - y;
                case DynamicArithmetic.Multiply: return x * y;
                case DynamicArithmetic.Divide: return x / y;
                default: return x % y;
            }
        }

        private static long AsLong(object value) => value is long l ? l : (long)(double)value;

        private static double AsDouble(object value) => value is long l ? l : (double)value;
    }
}
