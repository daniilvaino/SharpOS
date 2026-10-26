// System.Reflection.Assembly — the image's manifest resources, and nothing
// else of reflection (std has no metadata).
//
// ILC copies every manifest resource of the compiled assembly into the image:
// ReadyToRun section 300+25 (ReflectionMapBlob.BlobIdResourceData) holds the
// bytes, 300+24 (BlobIdResourceIndex) a NativeHashtable of
// (assembly name, resource name, offset, length). The lookups follow NativeAOT's
// reader, release/8.0 (MIT): System.Private.Reflection.Execution,
// ExecutionEnvironmentImplementation.ManifestResources.cs.
//
// SharpOS: an image is one assembly here (IlcSystemModule is the app), so
// every type answers the same Assembly and the index is read whole instead of
// by assembly-name hash. The stream is a read-only MemoryStream over a copy of
// the bytes rather than an UnmanagedMemoryStream over the image, which std
// does not have. The kernel publishes no ReadyToRun header to std
// (GenericVirtualMethods.Rtr is null there) and has no resources.

using System.Collections.Generic;
using System.IO;
using Internal.NativeFormat;
using SharpOS.Std.NoRuntime;

namespace System.Reflection
{
    public abstract class Assembly
    {
        public virtual string? FullName => null;

        public virtual string[] GetManifestResourceNames() => new string[0];

        public virtual Stream? GetManifestResourceStream(string name) => null;

        public virtual Stream? GetManifestResourceStream(Type type, string name)
        {
            if (type == null && name == null) throw new ArgumentNullException(nameof(type));
            string? ns = type?.Namespace;
            return GetManifestResourceStream(ns != null && name != null ? ns + "." + name : name!);
        }

        public static Assembly GetExecutingAssembly() => ImageAssembly.Instance;

        public static Assembly? GetEntryAssembly() => ImageAssembly.Instance;

        public override string ToString() => FullName ?? base.ToString();
    }

    internal sealed unsafe class ImageAssembly : Assembly
    {
        private const int ResourceIndex = 300 + 24, ResourceData = 300 + 25;

        private static ImageAssembly? s_instance;

        public static ImageAssembly Instance => s_instance ??= new ImageAssembly();

        public override string[] GetManifestResourceNames()
        {
            var names = new List<string>();
            foreach (Entry entry in Entries())
                names.Add(entry.Name);
            return names.ToArray();
        }

        public override Stream? GetManifestResourceStream(string name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (name.Length == 0) throw new ArgumentException("String cannot be of zero length.", nameof(name));

            foreach (Entry entry in Entries())
            {
                if (entry.Name != name) continue;

                byte* data = Section(GenericVirtualMethods.Rtr, ResourceData, out int dataLength);
                if (data == null || (uint)entry.Offset + (uint)entry.Length > (uint)dataLength)
                    throw new BadImageFormatException();

                var bytes = new byte[entry.Length];
                for (int i = 0; i < bytes.Length; i++)
                    bytes[i] = data[entry.Offset + i];
                return new MemoryStream(bytes, writable: false);
            }
            return null;
        }

        private struct Entry
        {
            public string Name;
            public int Offset;
            public int Length;
        }

        // A ReadyToRun section by id: count at +12, 24-byte rows of
        // { id, flags, start, end } from +16 (GenericVirtualMethods.Section).
        private static byte* Section(byte* rtr, int id, out int length)
        {
            length = 0;
            if (rtr == null) return null;
            ushort count = *(ushort*)(rtr + 12);
            for (int i = 0; i < count; i++)
            {
                byte* row = rtr + 16 + i * 24;
                if (*(int*)row != id) continue;
                byte* start = *(byte**)(row + 8);
                length = (int)(*(byte**)(row + 16) - start);
                return start;
            }
            return null;
        }

        private static List<Entry> Entries()
        {
            var entries = new List<Entry>();
            byte* rtr = GenericVirtualMethods.Rtr;
            if (rtr == null) return entries;

            byte* index = Section(rtr, ResourceIndex, out int indexLength);
            if (index == null) return entries;

            var reader = new NativeReader(index, (uint)indexLength);
            var all = new NativeHashtable(new NativeParser(reader, 0)).EnumerateAllEntries();
            for (NativeParser e = all.GetNext(); !e.IsNull; e = all.GetNext())
            {
                e.SkipString();                                   // assembly name
                string name = e.GetString();
                int offset = (int)e.GetUnsigned();
                int length = (int)e.GetUnsigned();
                entries.Add(new Entry { Name = name, Offset = offset, Length = length });
            }
            return entries;
        }
    }
}
