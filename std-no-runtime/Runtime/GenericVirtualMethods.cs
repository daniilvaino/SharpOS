// Generic virtual methods (step198): what `obj.Visit<T>(...)` calls when Visit
// is virtual, abstract or an interface method with its own type parameters.
//
// A vtable cannot hold one entry per instantiation, so ILC compiles such a call
// as TypeLoaderExports.GVMLookupForSlot(obj, slot) and leaves the answer to the
// runtime's type loader. Ours resolves over the tables ILC already writes into
// the image (RTR sections 300 + ReflectionMapBlob id) and nothing else:
//
//   18 GenericVirtualMethodTable — (declaring type, overriding type, method)
//      triples over typical types; walking the object's base chain finds the
//      override.
//   19 InterfaceGenericVirtualMethodTable — the same for interface methods,
//      with the interface signatures each implementing type maps to.
//   36 ExactMethodInstantiationsHashtable — unshared code: the answer is a
//      plain code pointer.
//   22 GenericMethodsTemplateMap — shared code over __Canon and the layout of
//      the dictionary it expects; 35 GenericMethodsHashtable — dictionaries
//      ILC built ahead of time. A missing dictionary is built here from the
//      layout cells: type handles, interface dispatch cells, other method
//      dictionaries, statics bases, method handles.
//   The answer for shared code is a fat pointer: &{code, dictionary} + 2.
//
// The algorithm follows the NativeAOT type loader, release/8.0 (MIT):
// TypeLoaderEnvironment.GVMResolution.cs (GVMLookupForSlotWorker,
// ResolveGenericVirtualMethodTarget, ResolveInterfaceGenericVirtualMethodSlot,
// FindMatchingInterfaceSlot), ConstructedGenericMethodsLookup.cs
// (TryGetGenericVirtualMethodPointer) and GenericDictionaryCell.cs. What it
// does not have is the type loader's type system: no type is built at run
// time, every type a cell names must already exist in the image (GenericsHashtable
// 32 or ArrayMap 2), and a cell whose type does not exist fails with the
// signature it was asked for. Not done: variant interface dispatch (an
// IFoo<object> call reaching an IFoo<string> implementation), unboxing stubs
// for value-type receivers, default constructors, thread statics and the
// rarer cell kinds — each fails loudly when met.
//
// Static method handles are ILC's (RuntimeMethodHandleInfo → NativeLayout
// signature); the ones the resolver hands out for MethodLdToken cells are
// tagged with the low bit and index s_handles, as the type loader tags its
// dynamic ones.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Internal.NativeFormat;
using Internal.Runtime.CompilerServices;

namespace SharpOS.Std.NoRuntime
{
    internal static unsafe class GenericVirtualMethods
    {
        /// <summary>The image's ReadyToRun header; null in the kernel, which has no GVM calls.</summary>
        public static byte* Rtr;

        /// <summary>RhpInitialDynamicInterfaceDispatch of this image, for the dispatch cells built here.</summary>
        public static void* InterfaceDispatchStub;

        private const int ArrayMap = 2, CommonFixups = 8, GvmTable = 18, InterfaceGvmTable = 19,
            Templates = 22, NativeLayoutInfo = 30, NativeReferences = 31, GenericsHashtable = 32,
            NativeStatics = 33, StaticsInfo = 34, Dictionaries = 35, ExactInstantiations = 36;

        private const uint Diamond = 0xFFFFFFFF, Reabstraction = 0xFFFFFFFE;

        private sealed class Method
        {
            public nint Owner;          // MethodTable of the declaring type (may be a definition while resolving)
            public uint Name;           // NativeLayout offset of the name
            public uint Sig;            // NativeLayout offset of the signature
            public nint[] Args;         // method instantiation
        }

        private struct SlotKey : IEquatable<SlotKey>
        {
            public nint Type, Slot;
            public bool Equals(SlotKey other) => Type == other.Type && Slot == other.Slot;
            public override bool Equals(object obj) => obj is SlotKey k && Equals(k);
            public override int GetHashCode() => (int)Type ^ (int)(Type >> 32) ^ ((int)Slot * 31);
        }

        private static readonly object s_lock = new object();
        private static Dictionary<SlotKey, nint> s_resolved;
        private static List<Method> s_handles;
        private static List<Method> s_dictionaryOwners;
        private static List<nint> s_dictionaries;
        private static List<object> s_keepAlive;            // dictionaries, descriptors, cells: the GC does not move, roots are enough
        private static Dictionary<nint, List<nint>> s_instantiations;
        private static NativeReader s_layout;

        public static nint Lookup(object obj, nint slot)
        {
            nint type = *(nint*)Unsafe.AsPointer(ref obj);
            type = *(nint*)type;
            var key = new SlotKey { Type = type, Slot = slot };
            lock (s_lock)
            {
                s_resolved ??= new Dictionary<SlotKey, nint>();
                if (s_resolved.TryGetValue(key, out nint known))
                    return known;

                if (Rtr == null)
                    throw new NotSupportedException("generic virtual method call: no type tables in this image");

                Method slotMethod = MethodFromHandle(slot);
                Method target = Resolve(type, slotMethod);
                nint result = EntryPoint(target);
                s_resolved.Add(key, result);
                return result;
            }
        }

        // ---- resolution: which method overrides the slot ----

        private static Method Resolve(nint type, Method slot)
        {
            bool viaInterface = Mt(slot.Owner)->IsInterface;
            for (int pass = 0; pass < (viaInterface ? 2 : 1); pass++)
            {
                bool defaults = pass == 1;
                for (nint current = type; current != 0; current = (nint)Mt(current)->GetBaseType())
                {
                    if (viaInterface)
                    {
                        Method found = ResolveInterfaceSlot(current, slot, defaults);
                        if (found == null) continue;
                        if (Mt(found.Owner)->IsInterface) return found;     // default implementation
                        return Resolve(current, found);
                    }

                    Method impl = ResolveClassSlot(current, slot);
                    if (impl != null) return impl;
                }
            }

            throw new NotSupportedException("generic virtual method: no implementation of " + Describe(slot)
                + " on type 0x" + ((ulong)type).ToString("x"));
        }

        private static Method ResolveClassSlot(nint current, Method slot)
        {
            nint callingType = Typical(slot.Owner), targetType = Typical(current);
            var all = new NativeHashtable(new NativeParser(Blob(GvmTable), 0)).EnumerateAllEntries();
            for (NativeParser e = all.GetNext(); !e.IsNull; e = all.GetNext())
            {
                if (ExtRef(CommonFixups, e.GetUnsigned()) != callingType) continue;
                if (ExtRef(CommonFixups, e.GetUnsigned()) != targetType) continue;
                if (!NameAndSigMatch(e.GetUnsigned(), slot)) continue;
                return FromNameAndSig(current, e.GetUnsigned(), slot.Args);
            }
            return null;
        }

        private static Method ResolveInterfaceSlot(nint current, Method slot, bool defaults)
        {
            NativeReader table = Blob(InterfaceGvmTable);
            if (table == null) return null;
            nint interfaceType = Typical(slot.Owner);
            var all = new NativeHashtable(new NativeParser(table, 0)).EnumerateAllEntries();
            for (NativeParser e = all.GetNext(); !e.IsNull; e = all.GetNext())
            {
                if (ExtRef(CommonFixups, e.GetUnsigned()) != interfaceType) continue;
                if (!NameAndSigMatch(e.GetUnsigned(), slot)) continue;
                // SharpOS cut: the variant pass (an IFoo<object> slot reaching an
                // IFoo<string> implementation) is not done; exact interfaces only.
                return FindMatchingInterfaceSlot(ref e, slot, current, defaults);
            }
            return null;
        }

        private static Method FindMatchingInterfaceSlot(ref NativeParser e, Method slot, nint current, bool defaults)
        {
            nint[] typeArgs = TypeArguments(current);
            uint targets = e.GetUnsigned();
            for (uint j = 0; j < targets; j++)
            {
                uint nameAndSig = e.GetUnsigned();
                nint targetType = 0;
                bool isDefault = true;
                if (nameAndSig != Diamond && nameAndSig != Reabstraction)
                {
                    targetType = ExtRef(CommonFixups, e.GetUnsigned());
                    isDefault = Mt(targetType)->IsInterface;
                }

                uint implementingTypes = e.GetUnsigned();
                for (uint k = 0; k < implementingTypes; k++)
                {
                    nint implementingType = ExtRef(CommonFixups, e.GetUnsigned());
                    uint signatures = e.GetUnsigned();
                    if (Typical(current) != implementingType || defaults != isDefault)
                    {
                        for (uint l = 0; l < signatures; l++) e.GetUnsigned();
                        continue;
                    }

                    for (uint l = 0; l < signatures; l++)
                    {
                        var sig = new NativeParser(Layout, e.GetUnsigned());
                        nint implemented = ResolveType(ref sig, typeArgs, null, required: false);
                        if (implemented != slot.Owner) continue;

                        if (nameAndSig == Diamond)
                            throw new NotSupportedException("generic virtual method: ambiguous default implementation of " + Describe(slot));
                        if (nameAndSig == Reabstraction)
                            throw new NotSupportedException("generic virtual method: " + Describe(slot) + " is reabstracted");

                        nint owner = targetType;
                        if (Mt(targetType)->IsInterface && Mt(targetType)->IsGenericTypeDefinition)
                        {
                            if (Typical(implemented) != targetType)
                                throw new NotSupportedException("generic virtual method: default implementation on another generic interface, " + Describe(slot));
                            owner = implemented;
                        }
                        return FromNameAndSig(owner, nameAndSig, slot.Args);
                    }
                }
            }
            return null;
        }

        // ---- the entry point of a resolved method ----

        private static nint EntryPoint(Method method)
        {
            nint code = ExactCode(method);
            if (code != 0) return code;

            if (!FindTemplate(method, out code, out uint layout))
                throw new NotSupportedException("generic virtual method: no code compiled for " + Describe(method));

            nint dictionary = Dictionary(method, layout);
            long[] descriptor = new long[2];
            Keep(descriptor);
            descriptor[0] = code;
            descriptor[1] = dictionary;
            return (nint)Unsafe.AsPointer(ref descriptor[0]) + 2;      // FunctionPointerOps.FatFunctionPointerOffset
        }

        private static nint ExactCode(Method method)
        {
            var all = new NativeHashtable(new NativeParser(Blob(ExactInstantiations), 0)).EnumerateAllEntries();
            for (NativeParser e = all.GetNext(); !e.IsNull; e = all.GetNext())
            {
                if (ExtRef(NativeReferences, e.GetUnsigned()) != method.Owner) continue;
                if (!NameAndSigMatch(e.GetUnsigned(), method)) continue;
                if (!ExactArgumentsMatch(ref e, method.Args)) continue;
                return ExtRef(NativeReferences, e.GetUnsigned());
            }
            return 0;
        }

        private static bool FindTemplate(Method method, out nint code, out uint layout)
        {
            var all = new NativeHashtable(new NativeParser(Blob(Templates), 0)).EnumerateAllEntries();
            for (NativeParser e = all.GetNext(); !e.IsNull; e = all.GetNext())
            {
                uint entry = e.GetUnsigned();
                layout = e.GetUnsigned();

                var m = new NativeParser(Layout, entry);
                uint flags = m.GetUnsigned();
                code = (flags & (uint)MethodFlags.HasFunctionPointer) != 0 ? ExtRef(NativeReferences, m.GetUnsigned()) : 0;
                if (!CanonicalMatch(ref m, method.Owner, owner: true)) continue;
                uint name = m.Offset;
                m.SkipString();
                uint sig = m.GetRelativeOffset();
                if (sig != method.Sig || !NameEquals(name, method.Name)) continue;
                if ((flags & (uint)MethodFlags.HasInstantiation) == 0)
                {
                    if (method.Args.Length != 0) continue;
                }
                else
                {
                    uint count = m.GetSequenceCount();
                    if (count != method.Args.Length) continue;
                    bool same = true;
                    for (int i = 0; i < count && same; i++)
                        same = CanonicalMatch(ref m, method.Args[i]);
                    if (!same) continue;
                }
                if (code != 0) return true;
            }
            code = 0;
            layout = 0;
            return false;
        }

        // Whether a template's type signature is the canonical form of a type:
        // a reference type stands for every reference type (__Canon is the only
        // reference type a canonical signature holds), value types match
        // exactly, generic value types argument by argument. The owning type is
        // not an argument: it is itself, or an instantiation over canonical ones.
        private static bool CanonicalMatch(ref NativeParser p, nint type, bool owner = false)
        {
            TypeSignatureKind kind = p.GetTypeSignatureKind(out uint data);
            switch (kind)
            {
                case TypeSignatureKind.Lookback:
                {
                    NativeParser back = p.GetLookbackParser(data);
                    return CanonicalMatch(ref back, type, owner);
                }
                case TypeSignatureKind.External:
                    return CanonicalTypeMatch(ExtRef(NativeReferences, data), type, owner);
                case TypeSignatureKind.Instantiation:
                {
                    nint definition = ResolveType(ref p, null, null, required: true);
                    if (!Mt(type)->IsGeneric || (nint)Mt(type)->GetGenericDefinition() != definition) return false;
                    for (int i = 0; i < data; i++)
                        if (!CanonicalMatch(ref p, (nint)Mt(type)->GetGenericArgument(i, (int)data))) return false;
                    return true;
                }
                default:
                    return false;
            }
        }

        // The same over a MethodTable: a template's owning type is usually the
        // canonical type itself (Parser<__Canon, __Canon>), not a signature.
        private static bool CanonicalTypeMatch(nint pattern, nint type, bool owner)
        {
            if (pattern == type) return true;
            GcMethodTable* p = Mt(pattern), t = Mt(type);
            if (!owner && !p->IsValueType && !t->IsValueType) return true;
            if (!p->IsGeneric || !t->IsGeneric) return false;
            GcMethodTable* definition = p->GetGenericDefinition();
            if (definition != t->GetGenericDefinition()) return false;
            int arity = definition->GenericParameterCount;
            for (int i = 0; i < arity; i++)
                if (!CanonicalTypeMatch((nint)p->GetGenericArgument(i, arity), (nint)t->GetGenericArgument(i, arity), false)) return false;
            return true;
        }

        // ---- dictionaries ----

        private static nint Dictionary(Method method, uint layout)
        {
            if (s_dictionaryOwners != null)
                for (int i = 0; i < s_dictionaryOwners.Count; i++)
                    if (SameMethod(s_dictionaryOwners[i], method)) return s_dictionaries[i];

            nint built = PrecompiledDictionary(method);
            if (built != 0) return Remember(method, built);

            var bag = new NativeParser(Layout, layout);
            NativeParser cells = bag.GetParserForBagElementKind(BagElementKind.DictionaryLayout);
            uint count = cells.IsNull ? 0 : cells.GetSequenceCount();

            // The hash-code header ILC puts before a method dictionary, then the
            // cells; one spare element so an empty dictionary still has an address.
            long[] storage = new long[count + 2];
            Keep(storage);
            nint dictionary = (nint)Unsafe.AsPointer(ref storage[1]);
            Remember(method, dictionary);                       // before the cells: one may name this very method

            nint[] typeArgs = TypeArguments(method.Owner);
            for (int i = 0; i < count; i++)
                storage[i + 1] = Cell(ref cells, typeArgs, method.Args);
            return dictionary;
        }

        private static nint Remember(Method method, nint dictionary)
        {
            s_dictionaryOwners ??= new List<Method>();
            s_dictionaries ??= new List<nint>();
            s_dictionaryOwners.Add(method);
            s_dictionaries.Add(dictionary);
            return dictionary;
        }

        private static nint PrecompiledDictionary(Method method)
        {
            var all = new NativeHashtable(new NativeParser(Blob(Dictionaries), 0)).EnumerateAllEntries();
            for (NativeParser e = all.GetNext(); !e.IsNull; e = all.GetNext())
            {
                nint dictionary = ExtRef(NativeReferences, e.GetUnsigned());
                if (ExtRef(NativeReferences, e.GetUnsigned()) != method.Owner) continue;
                if (!NameAndSigMatch(e.GetUnsigned(), method)) continue;
                if (!ExactArgumentsMatch(ref e, method.Args)) continue;
                return dictionary;
            }
            return 0;
        }

        private static nint MethodDictionary(Method method)
        {
            if (!FindTemplate(method, out _, out uint layout))
            {
                nint ready = PrecompiledDictionary(method);
                if (ready != 0) return ready;
                throw new NotSupportedException("generic dictionary: no template for " + Describe(method));
            }
            return Dictionary(method, layout);
        }

        // One dictionary cell, as GenericDictionaryCell.ParseAndCreateCell reads it.
        private static nint Cell(ref NativeParser p, nint[] typeArgs, nint[] methodArgs)
        {
            var kind = (FixupSignatureKind)p.GetUnsigned();
            switch (kind)
            {
                case FixupSignatureKind.TypeHandle:
                    return ResolveType(ref p, typeArgs, methodArgs, required: true);

                case FixupSignatureKind.InterfaceCall:
                {
                    nint interfaceType = ResolveType(ref p, typeArgs, methodArgs, required: true);
                    return NewInterfaceDispatchCell(interfaceType, p.GetUnsigned());
                }

                case FixupSignatureKind.MethodDictionary:
                    return MethodDictionary(ParseMethod(ref p, typeArgs, methodArgs));

                case FixupSignatureKind.StaticData:
                {
                    nint type = ResolveType(ref p, typeArgs, methodArgs, required: true);
                    return StaticBase(type, (StaticDataKind)p.GetUnsigned());
                }

                case FixupSignatureKind.UnwrapNullableType:
                {
                    nint nullable = ResolveType(ref p, typeArgs, methodArgs, required: true);
                    return (nint)Mt(nullable)->GetGenericArgument(0, 1);
                }

                case FixupSignatureKind.MethodLdToken:
                {
                    var sig = new NativeParser(Layout, p.GetRelativeOffset());
                    return NewMethodHandle(ParseMethod(ref sig, typeArgs, methodArgs));
                }

                case FixupSignatureKind.AllocateObject:
                    ResolveType(ref p, typeArgs, methodArgs, required: true);
                    return (nint)(delegate*<GcMethodTable*, void*>)&GcRuntimeExports.RhpNewFast;

                case FixupSignatureKind.DefaultConstructor:
                    // SharpOS cut: finding the default constructor needs the
                    // reflection map; the cell calls a stub that says so.
                    ResolveType(ref p, typeArgs, methodArgs, required: true);
                    return (nint)(delegate*<object, void>)&NoDefaultConstructor;

                default:
                    throw new NotSupportedException("generic dictionary: cell kind 0x" + ((uint)kind).ToString("x") + " is not supported");
            }
        }

        private static void NoDefaultConstructor(object obj) =>
            throw new NotSupportedException("default constructor through a generic dictionary (Activator.CreateInstance<T> from shared code)");

        // RhNewInterfaceDispatchCell: a two-cell block, 16-byte aligned — the
        // interface (tagged 1, "interface pointer") and a terminator holding the slot.
        private static nint NewInterfaceDispatchCell(nint interfaceType, uint slot)
        {
            long[] block = new long[6];
            Keep(block);
            nint cell = ((nint)Unsafe.AsPointer(ref block[0]) + 15) & ~(nint)15;
            ((nint*)cell)[0] = (nint)InterfaceDispatchStub;
            ((nint*)cell)[1] = interfaceType | 1;
            ((nint*)cell)[2] = 0;
            ((nint*)cell)[3] = (nint)slot;
            return cell;
        }

        private static nint StaticBase(nint type, StaticDataKind kind)
        {
            var all = new NativeHashtable(new NativeParser(Blob(StaticsInfo), 0)).EnumerateAllEntries();
            for (NativeParser e = all.GetNext(); !e.IsNull; e = all.GetNext())
            {
                if (ExtRef(NativeReferences, e.GetUnsigned()) != type) continue;
                uint? index = e.GetUnsignedForBagElementKind(kind == StaticDataKind.Gc ? BagElementKind.GcStaticData : BagElementKind.NonGcStaticData);
                if (index.HasValue) return ExtRef(NativeStatics, index.Value);
                break;
            }
            throw new NotSupportedException("generic dictionary: no statics of type 0x" + ((ulong)type).ToString("x"));
        }

        // ---- method handles ----

        private static Method MethodFromHandle(nint handle)
        {
            if ((handle & 1) != 0)
                return s_handles[(int)(handle >> 1)];

            // RuntimeMethodHandleInfo { NativeLayoutInfoSignature } → { TypeManager cell, offset }.
            nint* signature = *(nint**)handle;
            var p = new NativeParser(Layout, (uint)signature[1]);
            return ParseMethod(ref p, null, null);
        }

        private static nint NewMethodHandle(Method method)
        {
            s_handles ??= new List<Method>();
            for (int i = 0; i < s_handles.Count; i++)
                if (SameMethod(s_handles[i], method)) return ((nint)i << 1) | 1;
            s_handles.Add(method);
            return ((nint)(s_handles.Count - 1) << 1) | 1;
        }

        // A method signature: flags, [code], owning type, name, signature, [instantiation].
        private static Method ParseMethod(ref NativeParser p, nint[] typeArgs, nint[] methodArgs)
        {
            uint flags = p.GetUnsigned();
            if ((flags & (uint)MethodFlags.HasFunctionPointer) != 0) p.GetUnsigned();
            var method = new Method();
            method.Owner = ResolveType(ref p, typeArgs, methodArgs, required: true);
            method.Name = p.Offset;
            p.SkipString();
            method.Sig = p.GetRelativeOffset();
            if ((flags & (uint)MethodFlags.HasInstantiation) != 0)
            {
                uint count = p.GetSequenceCount();
                method.Args = new nint[count];
                for (int i = 0; i < count; i++)
                    method.Args[i] = ResolveType(ref p, typeArgs, methodArgs, required: true);
            }
            else
            {
                method.Args = new nint[0];
            }
            return method;
        }

        private static Method FromNameAndSig(nint owner, uint nameAndSig, nint[] args)
        {
            var p = new NativeParser(Layout, nameAndSig);
            p.SkipString();
            return new Method { Owner = owner, Name = nameAndSig, Sig = p.GetRelativeOffset(), Args = args };
        }

        private static bool NameAndSigMatch(uint nameAndSig, Method method)
        {
            var p = new NativeParser(Layout, nameAndSig);
            p.SkipString();
            return p.GetRelativeOffset() == method.Sig && NameEquals(nameAndSig, method.Name);
        }

        private static bool ExactArgumentsMatch(ref NativeParser e, nint[] args)
        {
            uint count = e.GetSequenceCount();
            if (count != args.Length) return false;
            for (int i = 0; i < count; i++)
                if (ExtRef(NativeReferences, e.GetUnsigned()) != args[i]) return false;
            return true;
        }

        private static bool SameMethod(Method a, Method b)
        {
            if (a.Owner != b.Owner || a.Sig != b.Sig || a.Args.Length != b.Args.Length) return false;
            for (int i = 0; i < a.Args.Length; i++)
                if (a.Args[i] != b.Args[i]) return false;
            return NameEquals(a.Name, b.Name);
        }

        // Names are inlined in every signature that carries one: compare the encoded bytes.
        private static bool NameEquals(uint a, uint b)
        {
            if (a == b) return true;
            NativeReader layout = Layout;
            uint at = layout.DecodeUnsigned(a, out uint lengthA);
            uint bt = layout.DecodeUnsigned(b, out uint lengthB);
            if (lengthA != lengthB) return false;
            for (uint i = 0; i < lengthA; i++)
                if (layout.ReadUInt8(at + i) != layout.ReadUInt8(bt + i)) return false;
            return true;
        }

        // ---- types ----

        // A type signature to an existing MethodTable; 0 when it does not exist
        // and the caller only compares (required: false).
        private static nint ResolveType(ref NativeParser p, nint[] typeArgs, nint[] methodArgs, bool required)
        {
            TypeSignatureKind kind = p.GetTypeSignatureKind(out uint data);
            switch (kind)
            {
                case TypeSignatureKind.Lookback:
                {
                    NativeParser back = p.GetLookbackParser(data);
                    return ResolveType(ref back, typeArgs, methodArgs, required);
                }

                case TypeSignatureKind.External:
                    return ExtRef(NativeReferences, data);

                case TypeSignatureKind.Variable:
                {
                    nint[] args = (data & 1) != 0 ? methodArgs : typeArgs;
                    int index = (int)(data >> 1);
                    if (args == null || index >= args.Length)
                        throw new NotSupportedException("generic signature: variable " + index + " has no argument");
                    return args[index];
                }

                case TypeSignatureKind.Instantiation:
                {
                    nint definition = ResolveType(ref p, typeArgs, methodArgs, required);
                    var args = new nint[data];
                    for (int i = 0; i < data; i++)
                        args[i] = ResolveType(ref p, typeArgs, methodArgs, required);
                    nint found = FindInstantiation(definition, args);
                    if (found == 0 && required)
                        throw new NotSupportedException("generic type not in the image: definition 0x" + ((ulong)definition).ToString("x")
                            + " over 0x" + ((ulong)args[0]).ToString("x") + (data > 1 ? ", ..." : ""));
                    return found;
                }

                case TypeSignatureKind.Modifier:
                {
                    nint element = ResolveType(ref p, typeArgs, methodArgs, required);
                    if ((TypeModifierKind)data != TypeModifierKind.Array)
                        throw new NotSupportedException("generic signature: byref or pointer type");
                    nint array = FindArray(element);
                    if (array == 0 && required)
                        throw new NotSupportedException("array type not in the image: element 0x" + ((ulong)element).ToString("x"));
                    return array;
                }

                default:
                    throw new NotSupportedException("generic signature: type kind " + (uint)kind + " is not supported");
            }
        }

        private static nint FindInstantiation(nint definition, nint[] args)
        {
            if (definition == 0) return 0;
            if (s_instantiations == null)
            {
                s_instantiations = new Dictionary<nint, List<nint>>();
                var all = new NativeHashtable(new NativeParser(Blob(GenericsHashtable), 0)).EnumerateAllEntries();
                for (NativeParser e = all.GetNext(); !e.IsNull; e = all.GetNext())
                {
                    nint type = ExtRef(NativeReferences, e.GetUnsigned());
                    nint def = (nint)Mt(type)->GetGenericDefinition();
                    if (!s_instantiations.TryGetValue(def, out List<nint> list))
                        s_instantiations.Add(def, list = new List<nint>());
                    list.Add(type);
                }
            }

            if (!s_instantiations.TryGetValue(definition, out List<nint> candidates)) return 0;
            foreach (nint candidate in candidates)
            {
                bool same = true;
                for (int i = 0; i < args.Length && same; i++)
                    same = (nint)Mt(candidate)->GetGenericArgument(i, args.Length) == args[i];
                if (same) return candidate;
            }
            return 0;
        }

        private static nint FindArray(nint element)
        {
            NativeReader map = Blob(ArrayMap);
            if (map == null || element == 0) return 0;
            var all = new NativeHashtable(new NativeParser(map, 0)).EnumerateAllEntries();
            for (NativeParser e = all.GetNext(); !e.IsNull; e = all.GetNext())
            {
                nint array = ExtRef(CommonFixups, e.GetUnsigned());
                if (Mt(array)->IsSzArray && (nint)Mt(array)->RelatedType == element) return array;
            }
            return 0;
        }

        private static nint[] TypeArguments(nint type)
        {
            GcMethodTable* mt = Mt(type);
            if (!mt->IsGeneric) return null;
            int arity = mt->GetGenericDefinition()->GenericParameterCount;
            var args = new nint[arity];
            for (int i = 0; i < arity; i++)
                args[i] = (nint)mt->GetGenericArgument(i, arity);
            return args;
        }

        private static nint Typical(nint type) =>
            Mt(type)->IsGeneric ? (nint)Mt(type)->GetGenericDefinition() : type;

        private static GcMethodTable* Mt(nint type) => (GcMethodTable*)type;

        // ---- image ----

        private static NativeReader Layout => s_layout ??= Blob(NativeLayoutInfo);

        private static NativeReader Blob(int id)
        {
            byte* start = Section(300 + id, out uint length);
            return start == null ? null : new NativeReader(start, length);
        }

        // An external reference: a 32-bit offset from the entry to its target.
        private static nint ExtRef(int table, uint index)
        {
            byte* entry = Section(300 + table, out _) + index * 4;
            return (nint)(entry + *(int*)entry);
        }

        private static byte* Section(int id, out uint length)
        {
            length = 0;
            ushort count = *(ushort*)(Rtr + 12);
            for (int i = 0; i < count; i++)
            {
                byte* row = Rtr + 16 + i * 24;
                if (*(int*)row != id) continue;
                byte* start = *(byte**)(row + 8);
                length = (uint)(*(byte**)(row + 16) - start);
                return start;
            }
            return null;
        }

        private static void Keep(object obj) => (s_keepAlive ??= new List<object>()).Add(obj);

        private static string Describe(Method method) =>
            "method '" + Layout.ReadString(method.Name) + "' of type 0x" + ((ulong)method.Owner).ToString("x")
            + " with " + method.Args.Length + " type argument(s)";
    }
}
