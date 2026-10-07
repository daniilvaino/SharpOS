// true and false boxed once (step196): a `dynamic` comparison, a view's bool
// field read through `dynamic`, a copy into an Expando give one of these two
// objects instead of a new box each time. A box is never written to, so
// sharing is safe. Made on first use: no static initializer (cctor ordering).

namespace SharpOS.Std.NoRuntime
{
    internal static class BoolBox
    {
        private static object s_true, s_false;

        public static object Of(bool value) => value ? (s_true ??= (object)true) : (s_false ??= (object)false);
    }
}
