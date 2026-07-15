// Ported from dotnet/runtime v8.0.27 (same servicing as ilcompiler 8.0.27):
//   src/coreclr/nativeaot/System.Private.CoreLib/src/System/Delegate.cs  (MIT)
//
// The field layout and Initialize* / GetThunk contract are dictated by ILC:
// the compiler synthesises each delegate type's ctor, Invoke and GetThunk
// override, and its generated code calls these Initialize* methods BY NAME.
// Do not rename fields or methods, and keep the four fields in this exact
// order (m_firstParameter / m_helperObject / m_extraFunctionPointerOrData /
// m_functionPointer) — see plan phase 1.
//
// Cuts vs original (reflection / interop surface -> throw or removed):
//   - GetMethodImpl / MethodInfo (no reflection MethodInfo type here).
//   - DynamicInvokeImpl, all public CreateDelegate overloads,
//     CreateObjectArrayDelegate, internal CreateDelegate(EETypePtr,...)
//     — reflection / expression-interpreter; removed.
//   - GetFunctionPointer (interop marshalling; OpenMethodResolver) — removed.
//   - InitializeClosedInstanceWithGVMResolution / ToInterface — throw
//     NotSupported (GVM + interface-method delegates not in scope yet;
//     drops TypeLoaderExports / RhpResolveInterfaceMethod deps).
//   - GetActualTargetFunctionPointer (open-instance invoke) — throw.
//   - ICloneable / ISerializable — dropped (no such interfaces here).
//   - GetHashCode: original returns GetType().GetHashCode(); we have no
//     reflection Type, so hash by MethodTable identity (same contract).

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Internal.Runtime.CompilerServices;

namespace System
{
    public abstract class Delegate
    {
        // V1 API stubs — never reached by app code (delegates are created by
        // the compiler-synthesised ctor + Initialize*, not by these). Kept for
        // API shape; they throw, exactly as the original does.
        protected Delegate(object target, string method)
        {
            throw new PlatformNotSupportedException();
        }

        protected Delegate(Type target, string method)
        {
            throw new PlatformNotSupportedException();
        }

        // New Delegate Implementation — layout is a contract with ILC.
        internal object m_firstParameter;
        internal object m_helperObject;
        internal nint m_extraFunctionPointerOrData;
        internal IntPtr m_functionPointer;

        // WARNING: These constants are also known to ILC and the calling
        // convention converter. Do not change their values.
        private protected const int MulticastThunk = 0;
        private protected const int ClosedStaticThunk = 1;
        private protected const int OpenStaticThunk = 2;
        private protected const int ClosedInstanceThunkOverGenericMethod = 3; // This may not exist
        private protected const int OpenInstanceThunk = 4;        // This may not exist
        private protected const int ObjectArrayThunk = 5;         // This may not exist

        // Overridden by each concrete delegate type (ILC-synthesised) to return
        // that type's thunk addresses. The base returns Zero (NativeAOT doesn't
        // use Universal Shared Code).
        private protected virtual IntPtr GetThunk(int whichThunk)
        {
            return IntPtr.Zero;
        }

        // This function is known to the IL Transformer.
        private void InitializeClosedInstance(object firstParameter, IntPtr functionPointer)
        {
            if (firstParameter is null)
                throw new ArgumentException("Delegate to an instance method cannot have null 'this'.");

            // Fat-pointer tripwire (plan phase 2 §7). m_functionPointer must be
            // a plain code entry on this fast path; a tagged generic-method
            // pointer belongs on the Slow path (-> m_extraFunctionPointerOrData).
            // A fat pointer reaching here means construction was mis-routed and
            // the invoke thunk would call a tagged address as code. Scoped to
            // m_functionPointer only — m_extraFunctionPointerOrData legitimately
            // holds fat pointers (see InitializeClosedInstanceSlow). Never
            // expected to fire; UNCONDITIONAL throw (Debug.Assert is
            // [Conditional("DEBUG")] and would compile out of the Release build)
            // — an uncaught throw halts with a stack trace to the mis-routed
            // call site, exactly the "stop and analyze" the plan wants.
            if (FunctionPointerOps.IsGenericMethodPointer(functionPointer))
                throw new InvalidOperationException("delegate fat-pointer tripwire: tagged fp on closed-instance fast path");

            m_functionPointer = functionPointer;
            m_firstParameter = firstParameter;
        }

        // This function is known to the IL Transformer.
        private void InitializeClosedInstanceSlow(object firstParameter, IntPtr functionPointer)
        {
            // Like InitializeClosedInstance but handles ALL cases, in particular
            // generic methods with fat function pointers.
            if (firstParameter is null)
                throw new ArgumentException("Delegate to an instance method cannot have null 'this'.");

            if (!FunctionPointerOps.IsGenericMethodPointer(functionPointer))
            {
                m_functionPointer = functionPointer;
                m_firstParameter = firstParameter;
            }
            else
            {
                m_firstParameter = this;
                m_functionPointer = GetThunk(ClosedInstanceThunkOverGenericMethod);
                m_extraFunctionPointerOrData = functionPointer;
                m_helperObject = firstParameter;
            }
        }

        // This function is known to the compiler.
        private void InitializeClosedInstanceWithGVMResolution(object firstParameter, RuntimeMethodHandle tokenOfGenericVirtualMethod)
        {
            // Generic-virtual-method delegates require TypeLoader GVM resolution
            // (not in scope yet). Kept by name for the ILC contract; throws.
            throw new NotSupportedException("generic virtual method delegates not supported");
        }

        private void InitializeClosedInstanceToInterface(object firstParameter, IntPtr dispatchCell)
        {
            // Delegate to an interface method requires RhpResolveInterfaceMethod
            // (not in scope yet). Kept by name for the ILC contract; throws.
            throw new NotSupportedException("delegate to interface method not supported");
        }

        // This is used to implement MethodInfo.CreateDelegate() in a desktop-
        // compatible way, and by the IL transformer for closed-instance.
        private void InitializeClosedInstanceWithoutNullCheck(object firstParameter, IntPtr functionPointer)
        {
            if (!FunctionPointerOps.IsGenericMethodPointer(functionPointer))
            {
                m_functionPointer = functionPointer;
                m_firstParameter = firstParameter;
            }
            else
            {
                m_firstParameter = this;
                m_functionPointer = GetThunk(ClosedInstanceThunkOverGenericMethod);
                m_extraFunctionPointerOrData = functionPointer;
                m_helperObject = firstParameter;
            }
        }

        // This function is known to the compiler backend.
        private void InitializeClosedStaticThunk(object firstParameter, IntPtr functionPointer, IntPtr functionPointerThunk)
        {
            m_extraFunctionPointerOrData = functionPointer;
            m_helperObject = firstParameter;
            m_functionPointer = functionPointerThunk;
            m_firstParameter = this;
        }

        // This function is known to the compiler backend.
        private void InitializeOpenStaticThunk(object _ /*firstParameter*/, IntPtr functionPointer, IntPtr functionPointerThunk)
        {
            // Invoked by calling the thunk with the delegate's args + a ref to
            // the delegate object itself.
            m_firstParameter = this;
            m_functionPointer = functionPointerThunk;
            m_extraFunctionPointerOrData = functionPointer;
        }

        private void InitializeOpenInstanceThunkDynamic(IntPtr functionPointer, IntPtr functionPointerThunk)
        {
            m_firstParameter = this;
            m_functionPointer = functionPointerThunk;
            m_extraFunctionPointerOrData = functionPointer;
        }

        internal void SetClosedStaticFirstParameter(object firstParameter)
        {
            // Closed static delegates place a value in m_helperObject that they
            // pass to the target method.
            Debug.Assert(m_functionPointer == GetThunk(ClosedStaticThunk));
            m_helperObject = firstParameter;
        }

        // Only ever called by the open-instance thunk (open-instance delegates
        // not in scope yet). Kept by name; throws.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private IntPtr GetActualTargetFunctionPointer(object thisObject)
        {
            throw new NotSupportedException("open-instance delegate invocation not supported");
        }

        public override int GetHashCode()
        {
            // Original: GetType().GetHashCode(). No reflection Type here; the
            // MethodTable pointer IS the type identity, same coarse contract
            // (all delegates of one type hash equal, which Equals requires).
            return GetEETypePtr().GetHashCode();
        }

        internal bool IsDynamicDelegate()
        {
            return this.GetThunk(MulticastThunk) == IntPtr.Zero;
        }

        public override bool Equals(object obj)
        {
            // Real uses hit MulticastDelegate.Equals; direct calls here are not
            // meaningful, so throw (matches original).
            throw new PlatformNotSupportedException();
        }

        public object Target
        {
            get
            {
                // Multicast delegates return the Target of the last delegate.
                if (m_functionPointer == GetThunk(MulticastThunk))
                {
                    Delegate[] invocationList = (Delegate[])m_helperObject;
                    int invocationCount = (int)m_extraFunctionPointerOrData;
                    return invocationList[invocationCount - 1].Target;
                }

                // Closed static delegates place a value in m_helperObject.
                if (m_functionPointer == GetThunk(ClosedStaticThunk) ||
                    m_functionPointer == GetThunk(ClosedInstanceThunkOverGenericMethod) ||
                    m_functionPointer == GetThunk(ObjectArrayThunk))
                    return m_helperObject;

                // Non-closed thunks identify themselves via m_firstParameter == this.
                if (object.ReferenceEquals(m_firstParameter, this))
                {
                    return null;
                }

                // Closed instance delegates place the target in m_firstParameter.
                return m_firstParameter;
            }
        }

        internal bool IsOpenStatic
        {
            get
            {
                return GetThunk(OpenStaticThunk) == m_functionPointer;
            }
        }

        internal static bool InternalEqualTypes(object a, object b)
        {
            return a.GetEETypePtr() == b.GetEETypePtr();
        }

        // ---- Shared surface (ported from libraries/System.Private.CoreLib
        // Delegate.cs). Combine/Remove + the CombineImpl/RemoveImpl/
        // GetInvocationList virtuals that MulticastDelegate overrides. Cut:
        // Clone/ICloneable, DynamicInvoke, Method/GetObjectData (reflection/
        // serialization). MulticastNotSupportedException -> InvalidOperationException.

        protected virtual Delegate CombineImpl(Delegate d) =>
            throw new InvalidOperationException("Multicast is not supported on this delegate.");

        protected virtual Delegate RemoveImpl(Delegate d) => d.Equals(this) ? null : this;

        public virtual Delegate[] GetInvocationList() => new Delegate[] { this };

        public static Delegate Combine(Delegate a, Delegate b)
        {
            if (a is null)
                return b;

            return a.CombineImpl(b);
        }

        public static Delegate Combine(params Delegate[] delegates)
        {
            if (delegates == null || delegates.Length == 0)
                return null;

            Delegate d = delegates[0];
            for (int i = 1; i < delegates.Length; i++)
                d = Combine(d, delegates[i]);

            return d;
        }

        public static Delegate Remove(Delegate source, Delegate value)
        {
            if (source == null)
                return null;

            if (value == null)
                return source;

            if (!InternalEqualTypes(source, value))
                throw new ArgumentException("Delegate types do not match.");

            return source.RemoveImpl(value);
        }

        public static Delegate RemoveAll(Delegate source, Delegate value)
        {
            Delegate newDelegate;

            do
            {
                newDelegate = source;
                source = Remove(source, value);
            }
            while (newDelegate != source);

            return newDelegate;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator ==(Delegate d1, Delegate d2)
        {
            if (d2 is null)
            {
                return d1 is null;
            }

            return ReferenceEquals(d2, d1) ? true : d2.Equals((object)d1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool operator !=(Delegate d1, Delegate d2)
        {
            if (d2 is null)
            {
                return d1 is not null;
            }

            return ReferenceEquals(d2, d1) ? false : !d2.Equals(d1);
        }
    }
}
