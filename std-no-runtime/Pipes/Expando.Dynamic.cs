using System;
using System.Collections.Generic;
using SharpOS.Std.Dynamic;

namespace SharpOS.Std.Pipes
{
    // `dynamic` over an Expando: a name is an entry, as with ExpandoObject —
    // reading one there is not is a RuntimeBinderException, setting a new one
    // adds it. The Expando's own members (Count, ContainsKey, …) are not
    // reached through `dynamic`: cast to Expando for them. Nothing is cached:
    // entries change.
    public sealed partial class Expando : IDynamicObject
    {
        private static Microsoft.CSharp.RuntimeBinder.RuntimeBinderException NoEntry(string name)
            => new Microsoft.CSharp.RuntimeBinder.RuntimeBinderException(
                "'SharpOS.Std.Pipes.Expando' does not contain a definition for '" + name + "'");

        bool IDynamicObject.TryGetMember(string name, out object value)
        {
            if (!TryGetValue(name, out value)) throw NoEntry(name);
            return true;
        }

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

        // An entry holds only what travels through a pipe, never a delegate: there is nothing to call.
        bool IDynamicObject.TryInvokeMember(string name, object[] args, out object result)
        {
            result = null;
            if (!ContainsKey(name)) throw NoEntry(name);
            throw new Microsoft.CSharp.RuntimeBinder.RuntimeBinderException("Cannot invoke a non-delegate type");
        }

        bool IDynamicObject.TryConvert(Type type, bool isExplicit, out object result)
        {
            result = null;
            return false;
        }
    }
}
