namespace Okojo.JavaScript.Embedding;

public interface IWorkerScriptSourceLoader
{
#if SHARPOS
    // SharpOS cut: no default interface members; both implementers have their own.
    string ResolveScript(string path, string? referrer = null);
#else
    string ResolveScript(string path, string? referrer = null)
    {
        return path;
    }
#endif

    string LoadScript(string path, string? referrer = null);
}
