// Ported from dotnet/runtime v8.0.27 (same servicing as ilcompiler 8.0.27):
//   src/coreclr/nativeaot/System.Private.CoreLib/src/System/MulticastDelegate.cs (MIT)
//
// This is almost entirely pure managed code: Combine/Remove/GetInvocationList/
// Equals/GetHashCode/operator ==. The invocation list is a Delegate[] stored in
// m_helperObject with the live count in m_extraFunctionPointerOrData.
//
// Cuts vs original:
//   - ISerializable / GetObjectData — serialization dropped.
//   - GetMethodImpl (returns MethodInfo — no reflection MethodInfo type here).
//   - Unsafe.As<MulticastDelegate>(obj) -> plain cast (types already verified
//     equal by InternalEqualTypes; our castclass walks the base chain).

using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using Internal.Runtime.CompilerServices;

namespace System
{
    public abstract class MulticastDelegate : Delegate
    {
        protected MulticastDelegate(object target, string method) : base(target, method)
        {
        }

        protected MulticastDelegate(Type target, string method) : base(target, method)
        {
        }

        private bool InvocationListEquals(MulticastDelegate d)
        {
            Delegate[] invocationList = (Delegate[])m_helperObject;
            if (d.m_extraFunctionPointerOrData != m_extraFunctionPointerOrData)
                return false;

            int invocationCount = (int)m_extraFunctionPointerOrData;
            for (int i = 0; i < invocationCount; i++)
            {
                Delegate dd = invocationList[i];
                Delegate[] dInvocationList = (Delegate[])d.m_helperObject;
                if (!dd.Equals(dInvocationList[i]))
                    return false;
            }
            return true;
        }

        public sealed override bool Equals(object obj)
        {
            if (obj == null)
                return false;
            if (object.ReferenceEquals(this, obj))
                return true;
            if (!InternalEqualTypes(this, obj))
                return false;

            // Same type, so obj is a MulticastDelegate too.
            Debug.Assert(obj is MulticastDelegate, "types already checked equal");
            var d = (MulticastDelegate)obj;

            // 1- Multicast (m_helperObject is Delegate[]); 2- single-cast
            // (structural comparison).
            IntPtr multicastThunk = GetThunk(MulticastThunk);
            if (m_functionPointer == multicastThunk)
            {
                return d.m_functionPointer == multicastThunk && InvocationListEquals(d);
            }
            else
            {
                if (!object.ReferenceEquals(m_helperObject, d.m_helperObject) ||
                    (!FunctionPointerOps.Compare(m_extraFunctionPointerOrData, d.m_extraFunctionPointerOrData)) ||
                    (!FunctionPointerOps.Compare(m_functionPointer, d.m_functionPointer)))
                {
                    return false;
                }

                // Thunk-based delegates put themselves in m_firstParameter, so
                // don't blindly compare that field.
                if (object.ReferenceEquals(m_firstParameter, this))
                {
                    return object.ReferenceEquals(d.m_firstParameter, d);
                }

                return object.ReferenceEquals(m_firstParameter, d.m_firstParameter);
            }
        }

        public sealed override int GetHashCode()
        {
            Delegate[] invocationList = m_helperObject as Delegate[];
            if (invocationList == null)
            {
                return base.GetHashCode();
            }
            else
            {
                int hash = 0;
                for (int i = 0; i < (int)m_extraFunctionPointerOrData; i++)
                {
                    hash = hash * 33 + invocationList[i].GetHashCode();
                }

                return hash;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(MulticastDelegate d1, MulticastDelegate d2)
        {
            if (d2 is null)
            {
                return d1 is null;
            }

            return ReferenceEquals(d2, d1) ? true : d2.Equals((object)d1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(MulticastDelegate d1, MulticastDelegate d2)
        {
            if (d2 is null)
            {
                return d1 is not null;
            }

            return ReferenceEquals(d2, d1) ? false : !d2.Equals(d1);
        }

        private MulticastDelegate NewMulticastDelegate(Delegate[] invocationList, int invocationCount, bool thisIsMultiCastAlready = false)
        {
            // Allocate a new multicast delegate of the same concrete type as this.
            MulticastDelegate result = (MulticastDelegate)RuntimeImports.RhNewObject(this.GetEETypePtr());

            if (thisIsMultiCastAlready)
            {
                result.m_functionPointer = this.m_functionPointer;
            }
            else
            {
                result.m_functionPointer = GetThunk(MulticastThunk);
            }
            result.m_firstParameter = result;
            result.m_helperObject = invocationList;
            result.m_extraFunctionPointerOrData = (IntPtr)invocationCount;

            return result;
        }

        private static bool TrySetSlot(Delegate[] a, int index, Delegate o)
        {
            if (a[index] == null && System.Threading.Interlocked.CompareExchange<Delegate>(ref a[index], o, null) == null)
                return true;

            // Slot may already be set because we added+removed the same method.
            if (a[index] != null)
            {
                MulticastDelegate d = (MulticastDelegate)o;
                MulticastDelegate dd = (MulticastDelegate)a[index];

                if (object.ReferenceEquals(dd.m_firstParameter, d.m_firstParameter) &&
                    object.ReferenceEquals(dd.m_helperObject, d.m_helperObject) &&
                    dd.m_extraFunctionPointerOrData == d.m_extraFunctionPointerOrData &&
                    dd.m_functionPointer == d.m_functionPointer)
                {
                    return true;
                }
            }
            return false;
        }

        protected sealed override Delegate CombineImpl(Delegate follow)
        {
            if (follow is null)
                return this;

            if (!InternalEqualTypes(this, follow))
                throw new ArgumentException("Delegate types do not match.");

            if (IsDynamicDelegate() && follow.IsDynamicDelegate())
            {
                throw new InvalidOperationException();
            }

            MulticastDelegate dFollow = (MulticastDelegate)follow;
            Delegate[] resultList;
            int followCount = 1;
            Delegate[] followList = dFollow.m_helperObject as Delegate[];
            if (followList != null)
                followCount = (int)dFollow.m_extraFunctionPointerOrData;

            int resultCount;
            Delegate[] invocationList = m_helperObject as Delegate[];
            if (invocationList == null)
            {
                resultCount = 1 + followCount;
                resultList = new Delegate[resultCount];
                resultList[0] = this;
                if (followList == null)
                {
                    resultList[1] = dFollow;
                }
                else
                {
                    for (int i = 0; i < followCount; i++)
                        resultList[1 + i] = followList[i];
                }
                return NewMulticastDelegate(resultList, resultCount);
            }
            else
            {
                int invocationCount = (int)m_extraFunctionPointerOrData;
                resultCount = invocationCount + followCount;
                resultList = null;
                if (resultCount <= invocationList.Length)
                {
                    resultList = invocationList;
                    if (followList == null)
                    {
                        if (!TrySetSlot(resultList, invocationCount, dFollow))
                            resultList = null;
                    }
                    else
                    {
                        for (int i = 0; i < followCount; i++)
                        {
                            if (!TrySetSlot(resultList, invocationCount + i, followList[i]))
                            {
                                resultList = null;
                                break;
                            }
                        }
                    }
                }

                if (resultList == null)
                {
                    int allocCount = invocationList.Length;
                    while (allocCount < resultCount)
                        allocCount *= 2;

                    resultList = new Delegate[allocCount];

                    for (int i = 0; i < invocationCount; i++)
                        resultList[i] = invocationList[i];

                    if (followList == null)
                    {
                        resultList[invocationCount] = dFollow;
                    }
                    else
                    {
                        for (int i = 0; i < followCount; i++)
                            resultList[invocationCount + i] = followList[i];
                    }
                }
                return NewMulticastDelegate(resultList, resultCount, true);
            }
        }

        private Delegate[] DeleteFromInvocationList(Delegate[] invocationList, int invocationCount, int deleteIndex, int deleteCount)
        {
            Delegate[] thisInvocationList = (Delegate[])m_helperObject;
            int allocCount = thisInvocationList.Length;
            while (allocCount / 2 >= invocationCount - deleteCount)
                allocCount /= 2;

            Delegate[] newInvocationList = new Delegate[allocCount];

            for (int i = 0; i < deleteIndex; i++)
                newInvocationList[i] = invocationList[i];

            for (int i = deleteIndex + deleteCount; i < invocationCount; i++)
                newInvocationList[i - deleteCount] = invocationList[i];

            return newInvocationList;
        }

        private static bool EqualInvocationLists(Delegate[] a, Delegate[] b, int start, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (!(a[start + i].Equals(b[i])))
                    return false;
            }
            return true;
        }

        // Looks backward on the invocation list for an element with delegate
        // equality to value; removes it, returns a new delegate. Not found -> a
        // copy of the current list.
        protected sealed override Delegate RemoveImpl(Delegate value)
        {
            MulticastDelegate v = value as MulticastDelegate;

            if (v is null)
                return this;
            if (v.m_helperObject as Delegate[] == null)
            {
                Delegate[] invocationList = m_helperObject as Delegate[];
                if (invocationList == null)
                {
                    // Both not real multicast.
                    if (this.Equals(v))
                        return null;
                }
                else
                {
                    int invocationCount = (int)m_extraFunctionPointerOrData;
                    for (int i = invocationCount; --i >= 0;)
                    {
                        if (v.Equals(invocationList[i]))
                        {
                            if (invocationCount == 2)
                            {
                                // Only one value left.
                                return invocationList[1 - i];
                            }
                            else
                            {
                                Delegate[] list = DeleteFromInvocationList(invocationList, invocationCount, i, 1);
                                return NewMulticastDelegate(list, invocationCount - 1, true);
                            }
                        }
                    }
                }
            }
            else
            {
                Delegate[] invocationList = m_helperObject as Delegate[];
                if (invocationList != null)
                {
                    int invocationCount = (int)m_extraFunctionPointerOrData;
                    int vInvocationCount = (int)v.m_extraFunctionPointerOrData;
                    for (int i = invocationCount - vInvocationCount; i >= 0; i--)
                    {
                        if (EqualInvocationLists(invocationList, v.m_helperObject as Delegate[], i, vInvocationCount))
                        {
                            if (invocationCount - vInvocationCount == 0)
                            {
                                return null;
                            }
                            else if (invocationCount - vInvocationCount == 1)
                            {
                                return invocationList[i != 0 ? 0 : invocationCount - 1];
                            }
                            else
                            {
                                Delegate[] list = DeleteFromInvocationList(invocationList, invocationCount, i, vInvocationCount);
                                return NewMulticastDelegate(list, invocationCount - vInvocationCount, true);
                            }
                        }
                    }
                }
            }

            return this;
        }

        public sealed override Delegate[] GetInvocationList()
        {
            Delegate[] del;
            Delegate[] invocationList = m_helperObject as Delegate[];
            if (invocationList == null)
            {
                del = new Delegate[1];
                del[0] = this;
            }
            else
            {
                int invocationCount = (int)m_extraFunctionPointerOrData;
                del = new Delegate[invocationCount];

                for (int i = 0; i < del.Length; i++)
                    del[i] = invocationList[i];
            }
            return del;
        }
    }
}
