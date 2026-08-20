// Type identity without reflection.
//
// Terminal.Gui asks about types in two very different ways, and only one of
// them actually needs metadata:
//
//   1. "are these the same type?" / "is this exactly a View?" — identity, which
//      is a pointer comparison on the method table and needs nothing else;
//   2. "does this subclass override MouseEvent?" / "is its name ContentView?" —
//      reflection standing in for a fact the type system could have carried.
//
// The first kind lives here. The second was replaced at each site: the name
// check became a marker interface, and the method-override probe became a
// constant (see Responder.IsOverridden), because there is no metadata to search
// and inventing an answer per call would be worse than one stated in one place.

namespace Terminal.Gui
{
    internal static class RuntimeTypeId
    {
        /// <summary>Same runtime type, by method-table identity.</summary>
        public static bool SameType(object a, object b)
        {
            if (a == null || b == null) return ReferenceEquals(a, b);
            return a.GetEETypePtr() == b.GetEETypePtr();
        }

        /// <summary>
        /// Exactly <typeparamref name="T"/> — not a subclass. `is` would answer
        /// a different question, and the callers here mean this one.
        /// </summary>
        public static bool Is<T>(object o)
            => o != null && o.GetEETypePtr() == System.EETypePtr.EETypePtrOf<T>();
    }

    /// <summary>
    /// The inner view a Window, FrameView or ScrollView puts its children in.
    /// </summary>
    /// <remarks>
    /// Upstream finds these by asking whether the runtime type is *named*
    /// "ContentView" — three unrelated private classes, matched by a string.
    /// The marker says the same thing to the type system, which can be checked
    /// without metadata and cannot be broken by a rename.
    /// </remarks>
    internal interface IContentView
    {
    }
}
