namespace Okojo.JavaScript.SourceMaps;

public sealed class SourceMapModuleSourceLoader(
    IModuleSourceLoader inner,
    SourceMapScriptLoader sourceMapLoader
) : IModuleSourceLoader
{
    public SourceMapModuleSourceLoader(IModuleSourceLoader inner, SourceMapRegistry registry)
        : this(inner, new SourceMapScriptLoader(registry)) { }

#if SHARPOS
    // SharpOS cut: IModuleSourceLoader's default, written out (no default interface members).
    public ModuleLoadResult LoadModule(string resolvedId) =>
        ModuleLoadResult.SourceText(LoadSource(resolvedId));
#endif

    public string ResolveSpecifier(string specifier, string? referrer)
    {
        return inner.ResolveSpecifier(specifier, referrer);
    }

    public string LoadSource(string resolvedId)
    {
        var source = inner.LoadSource(resolvedId);
        sourceMapLoader.TryRegister(resolvedId, source);
        return source;
    }
}
