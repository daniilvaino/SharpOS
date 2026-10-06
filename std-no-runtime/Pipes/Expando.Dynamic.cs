using System;
using System.Collections.Generic;
using SharpOS.Std.Dynamic;

namespace SharpOS.Std.Pipes
{
    // `dynamic` over an Expando: a name reads and writes the entry, as with
    // ExpandoObject; setting a new name adds it. A name with no entry falls
    // through to the Expando's own members (Count, ContainsKey, …), then fails
    // as a missing member. Nothing is cached: entries change.
    public sealed partial class Expando : IDynamicObject
    {
        bool IDynamicObject.TryGetMember(string name, out object value) => TryGetValue(name, out value);

        bool IDynamicObject.TrySetMember(string name, object value)
        {
            Set(name, value);
            return true;
        }

        bool IDynamicObject.TryGetIndex(object[] indexes, out object value)
        {
            value = null;
            if (indexes.Length != 1 || !(indexes[0] is string name)) return false;
            value = this[name];
            return true;
        }

        bool IDynamicObject.TrySetIndex(object[] indexes, object value)
        {
            if (indexes.Length != 1 || !(indexes[0] is string name)) return false;
            Set(name, value);
            return true;
        }

        bool IDynamicObject.TryInvokeMember(string name, object[] args, out object result)
        {
            result = null;
            return false;
        }

        bool IDynamicObject.TryConvert(Type type, bool isExplicit, out object result)
        {
            result = null;
            return false;
        }
    }
}
