// System.ComponentModel, the sliver a UI library actually touches.
//
// Terminal.Gui is written for a world with a designer in it: properties carry
// [Browsable] so a form editor can decide what to show, and views implement
// ISupportInitializeNotification so a designer can batch changes between
// BeginInit and EndInit. None of that machinery exists here and none of it needs
// to — the attribute is read by tooling, never at runtime, and the interface is
// implemented by views whose Begin/EndInit we simply call in order.
//
// So this is the declarations, faithfully shaped, and nothing behind them.
// Ported from dotnet/runtime v8.0 (MIT), System.ComponentModel.Primitives.

namespace System.ComponentModel
{
    /// <summary>
    /// Whether a property or event shows up in a designer. Inert here: kept so
    /// annotated source compiles unchanged.
    /// </summary>
    [AttributeUsage(AttributeTargets.All)]
    public sealed class BrowsableAttribute : Attribute
    {
        public static BrowsableAttribute Yes => new BrowsableAttribute(true);
        public static BrowsableAttribute No => new BrowsableAttribute(false);
        public static BrowsableAttribute Default => new BrowsableAttribute(true);

        public BrowsableAttribute(bool browsable) { Browsable = browsable; }

        public bool Browsable { get; }

        public override bool Equals(object? obj)
            => obj is BrowsableAttribute other && other.Browsable == Browsable;

        public override int GetHashCode() => Browsable ? 1 : 0;
    }

    /// <summary>
    /// Batched initialisation: everything between BeginInit and EndInit is one
    /// change as far as the object is concerned.
    /// </summary>
    public interface ISupportInitialize
    {
        void BeginInit();
        void EndInit();
    }

    public interface ISupportInitializeNotification : ISupportInitialize
    {
        bool IsInitialized { get; }
        event EventHandler Initialized;
    }
}
