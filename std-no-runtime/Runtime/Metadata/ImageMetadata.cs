// The image's reflection metadata, found by a MethodTable — what stock does in
// TypeLoaderEnvironment.TryGetMetadataForNamedType (System.Private.TypeLoader,
// release/8.0) over ExternalReferencesTable (Common/src/Internal/Runtime/
// TypeLoader), cut down to one module: the kernel and every app are one image.
//
//   TypeMap (ReadyToRun blob 301)          NativeHashtable by the MethodTable's
//                                          hash code: (fixup index, metadata handle)
//   CommonFixupsTable (308)                fixup index -> MethodTable, a 4-byte
//                                          self-relative pointer each (RTR 9)
//   EmbeddedMetadata (313)                 the metadata the handle points into
//
// ILC writes these because CoreLib is a real System.Private.CoreLib (step251).
// The ReadyToRun header comes from the start: NativeAotModuleInit in the kernel,
// AppTypeManagerInit in an app.

using System;
using Internal.Metadata.NativeFormat;
using Internal.NativeFormat;

namespace SharpOS.Std.NoRuntime
{
    public static unsafe class ImageMetadata
    {
        private const int TypeMapBlob = 300 + 1;          // ReflectionMapBlob.TypeMap
        private const int CommonFixupsTableBlob = 300 + 8; // ReflectionMapBlob.CommonFixupsTable
        private const int EmbeddedMetadataBlob = 300 + 13; // ReflectionMapBlob.EmbeddedMetadata

        /// <summary>This image's ReadyToRun header; written once at start.</summary>
        public static byte* ReadyToRunHeader;

        private static MetadataReader s_reader;
        private static NativeReader s_typeMap;
        private static int* s_fixups;
        private static uint s_fixupCount;

        /// <summary>The image's metadata; null when the image has none.</summary>
        public static MetadataReader Reader => Ensure() ? s_reader : null;

        /// <summary>
        /// The type definition of <paramref name="methodTable"/>, when the image
        /// has metadata for it.
        /// </summary>
        public static bool TryGetTypeDefinition(void* methodTable, out TypeDefinitionHandle handle)
        {
            handle = default;
            if (methodTable == null || !Ensure()) return false;

            int hashCode = (int)((GcMethodTable*)methodTable)->HashCode;
            var hashtable = new NativeHashtable(new NativeParser(s_typeMap, 0));
            var lookup = hashtable.Lookup(hashCode);
            NativeParser entry;
            while (!(entry = lookup.GetNext()).IsNull)
            {
                if (FixupAddress(entry.GetUnsigned()) != methodTable) continue;
                Handle found = AsHandle(entry.GetUnsigned());
                if (found.HandleType != HandleType.TypeDefinition) continue;
                handle = found.ToTypeDefinitionHandle(s_reader);
                return true;
            }
            return false;
        }

        /// <summary>Whether a custom attribute is of the type ns.name (stock MetadataReaderExtensions.IsCustomAttributeOfType).</summary>
        public static bool IsCustomAttributeOfType(CustomAttributeHandle attribute, MetadataReader reader, string ns, string name)
        {
            string[] namespaceParts = ns.Split('.');
            Handle typeHandle = GetAttributeTypeHandle(attribute.GetCustomAttribute(reader), reader);
            HandleType handleType = typeHandle.HandleType;
            if (handleType == HandleType.TypeDefinition)
            {
                TypeDefinition typeDefinition = typeHandle.ToTypeDefinitionHandle(reader).GetTypeDefinition(reader);
                if (!typeDefinition.Name.StringEquals(name, reader))
                    return false;
                NamespaceDefinitionHandle nsHandle = typeDefinition.NamespaceDefinition;
                int idx = namespaceParts.Length;
                while (idx-- != 0)
                {
                    NamespaceDefinition namespaceDefinition = nsHandle.GetNamespaceDefinition(reader);
                    if (!StringOrNullEquals(namespaceDefinition.Name, namespaceParts[idx], reader))
                        return false;
                    if (!IsNamespaceDefinitionHandle(namespaceDefinition.ParentScopeOrNamespace))
                        return false;
                    nsHandle = namespaceDefinition.ParentScopeOrNamespace.ToNamespaceDefinitionHandle(reader);
                }
                return StringOrNullEquals(nsHandle.GetNamespaceDefinition(reader).Name, null, reader);
            }
            if (handleType == HandleType.TypeReference)
            {
                TypeReference typeReference = typeHandle.ToTypeReferenceHandle(reader).GetTypeReference(reader);
                if (!typeReference.TypeName.StringEquals(name, reader))
                    return false;
                if (!IsNamespaceReferenceHandle(typeReference.ParentNamespaceOrType))
                    return false;
                NamespaceReferenceHandle nsHandle = typeReference.ParentNamespaceOrType.ToNamespaceReferenceHandle(reader);
                int idx = namespaceParts.Length;
                while (idx-- != 0)
                {
                    NamespaceReference namespaceReference = nsHandle.GetNamespaceReference(reader);
                    if (!StringOrNullEquals(namespaceReference.Name, namespaceParts[idx], reader))
                        return false;
                    if (!IsNamespaceReferenceHandle(namespaceReference.ParentScopeOrNamespace))
                        return false;
                    nsHandle = namespaceReference.ParentScopeOrNamespace.ToNamespaceReferenceHandle(reader);
                }
                return StringOrNullEquals(nsHandle.GetNamespaceReference(reader).Name, null, reader);
            }
            return false;
        }

        // Stock MetadataReaderExtensions.NativeFormat: GetAttributeTypeHandle,
        // IsNamespaceDefinitionHandle, IsNamespaceReferenceHandle.
        private static Handle GetAttributeTypeHandle(CustomAttribute customAttribute, MetadataReader reader)
        {
            HandleType constructorHandleType = customAttribute.Constructor.HandleType;
            if (constructorHandleType == HandleType.QualifiedMethod)
                return customAttribute.Constructor.ToQualifiedMethodHandle(reader).GetQualifiedMethod(reader).EnclosingType;
            else if (constructorHandleType == HandleType.MemberReference)
                return customAttribute.Constructor.ToMemberReferenceHandle(reader).GetMemberReference(reader).Parent;
            else
                throw new BadImageFormatException();
        }

        private static bool IsNamespaceDefinitionHandle(Handle handle) => handle.HandleType == HandleType.NamespaceDefinition;

        private static bool IsNamespaceReferenceHandle(Handle handle) => handle.HandleType == HandleType.NamespaceReference;

        private static bool StringOrNullEquals(ConstantStringValueHandle handle, string valueOrNull, MetadataReader reader)
        {
            if (valueOrNull == null)
                return handle.IsNull(reader);
            if (handle.IsNull(reader))
                return false;
            return handle.StringEquals(valueOrNull, reader);
        }

        // Stock MetadataReaderExtensions.AsHandle: a raw token is a handle.
        private static Handle AsHandle(uint token) => *(Handle*)&token;

        private static void* FixupAddress(uint index)
        {
            if (index >= s_fixupCount) return null;
            int* relative = s_fixups + index;
            return (byte*)relative + *relative;
        }

        // Set up on first use; the values are the same whoever gets there first.
        private static bool Ensure()
        {
            if (s_reader != null) return true;
            byte* rtr = ReadyToRunHeader;
            if (rtr == null) return false;

            byte* typeMap = Section(rtr, TypeMapBlob, out int typeMapLength);
            byte* fixups = Section(rtr, CommonFixupsTableBlob, out int fixupsLength);
            byte* metadata = Section(rtr, EmbeddedMetadataBlob, out int metadataLength);
            if (typeMap == null || fixups == null || metadata == null || metadataLength == 0) return false;

            s_typeMap = new NativeReader(typeMap, (uint)typeMapLength);
            s_fixups = (int*)fixups;
            s_fixupCount = (uint)(fixupsLength / sizeof(int));
            s_reader = new MetadataReader((IntPtr)metadata, metadataLength);
            return true;
        }

        // A section of the ReadyToRun header: rows of {int id, int flags, void* start, void* end}.
        private static byte* Section(byte* rtr, int id, out int length)
        {
            length = 0;
            ushort count = *(ushort*)(rtr + 12);
            byte* rows = rtr + 16;
            for (int i = 0; i < count; i++)
            {
                byte* row = rows + i * 24;
                if (*(int*)row != id) continue;
                byte* start = *(byte**)(row + 8);
                byte* end = *(byte**)(row + 16);
                length = (*(int*)(row + 4) & 1) != 0 ? (int)(end - start) : sizeof(nint);
                return start;
            }
            return null;
        }
    }
}
