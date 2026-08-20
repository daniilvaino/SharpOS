// The library's user-visible strings, without the resource machinery.
//
// Upstream generates Strings.Designer.cs from Strings.resx and reads the values
// back through a ResourceManager: embedded resources, reflection over the
// assembly and a culture lookup — three subsystems we do not have, to serve
// strings that are compiled into the binary either way.
//
// So the values are inlined here, taken verbatim from Strings.resx. The cost is
// real and worth stating plainly: translations are gone. With one culture in
// the system (std Globalization.cs) nothing was being selected between anyway,
// and when that changes this file is what has to grow a lookup.
//
// Properties rather than static fields: lazily initialised static reference
// fields are the one construct this environment cannot run (limits §1).

namespace Terminal.Gui.Resources
{
    internal static class Strings
    {
        public static string ctxSelectAll => "_Select All";
        public static string ctxDeleteAll => "_Delete All";
        public static string ctxCopy => "_Copy";
        public static string ctxCut => "Cu_t";
        public static string ctxPaste => "_Paste";
        public static string ctxUndo => "_Undo";
        public static string ctxRedo => "_Redo";
        public static string fdDirectory => "Directory";
        public static string fdFile => "File";
        public static string fdSave => "Save";
        public static string fdSaveAs => "Save as";
        public static string fdOpen => "Open";
        public static string fdSelectFolder => "Select folder";
        public static string fdSelectMixed => "Select Mixed";
        public static string wzBack => "_Back";
        public static string wzFinish => "Fi_nish";
        public static string wzNext => "_Next...";
    }
}
