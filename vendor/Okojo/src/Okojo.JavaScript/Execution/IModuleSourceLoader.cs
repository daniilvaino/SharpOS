namespace Okojo.JavaScript.Execution;

public interface IModuleSourceLoader
{
    string ResolveSpecifier(string specifier, string? referrer);
#if SHARPOS
    // SharpOS cut: no default interface members; implementers write the
    // default (SourceText of LoadSource) out.
    ModuleLoadResult LoadModule(string resolvedId);
#else
    ModuleLoadResult LoadModule(string resolvedId) =>
        ModuleLoadResult.SourceText(LoadSource(resolvedId));
#endif
    string LoadSource(string resolvedId);
}
