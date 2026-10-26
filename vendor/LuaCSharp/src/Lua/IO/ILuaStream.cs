namespace Lua.IO;

// SharpOS cut: no default interface members (CS8701) — the target runtime
// has none. The bodies live in LuaStreamDefaults; an implementer derives from
// it and lists ILuaStream again, so its own members win the mapping.
public interface ILuaStream : IDisposable
{
    bool IsOpen { get; }
    LuaFileOpenMode Mode { get; }

    ValueTask<string> ReadAllAsync(CancellationToken cancellationToken);

    ValueTask<double?> ReadNumberAsync(CancellationToken cancellationToken);

    ValueTask<string?> ReadLineAsync(bool keepEol, CancellationToken cancellationToken);

    ValueTask<string?> ReadAsync(int count, CancellationToken cancellationToken);

    ValueTask WriteAsync(ReadOnlyMemory<char> content, CancellationToken cancellationToken);

    ValueTask WriteAsync(string content, CancellationToken cancellationToken);

    ValueTask FlushAsync(CancellationToken cancellationToken);

    void SetVBuf(LuaFileBufferingMode mode, int size);

    long Seek(SeekOrigin origin, long offset);

    ValueTask CloseAsync(CancellationToken cancellationToken);
}

// SharpOS cut: the default bodies of ILuaStream and its static factories.
public abstract class LuaStreamDefaults : ILuaStream
{
    // Always the implementer's own (it lists ILuaStream again); these only
    // complete the base's mapping.
    public virtual bool IsOpen => throw new NotImplementedException();
    public virtual LuaFileOpenMode Mode => throw new NotImplementedException();
    public virtual void Dispose() => throw new NotImplementedException();

    public virtual ValueTask<string> ReadAllAsync(CancellationToken cancellationToken)
    {
        Mode.ThrowIfNotReadable();

        // Default implementation using ReadStringAsync
        throw new NotImplementedException($"ReadAllAsync must be implemented by {GetType().Name}");
    }

    public virtual ValueTask<double?> ReadNumberAsync(CancellationToken cancellationToken)
    {
        Mode.ThrowIfNotReadable();

        // Default implementation using ReadStringAsync
        throw new NotImplementedException(
            $"ReadNumberAsync must be implemented by {GetType().Name}"
        );
    }

    public virtual ValueTask<string?> ReadLineAsync(bool keepEol, CancellationToken cancellationToken)
    {
        Mode.ThrowIfNotReadable();

        // Default implementation using ReadStringAsync
        throw new NotImplementedException($"ReadLineAsync must be implemented by {GetType().Name}");
    }

    public virtual ValueTask<string?> ReadAsync(int count, CancellationToken cancellationToken)
    {
        Mode.ThrowIfNotReadable();

        // Default implementation using ReadAllAsync
        throw new NotImplementedException(
            $"ReadStringAsync must be implemented by {GetType().Name}"
        );
    }

    public virtual ValueTask WriteAsync(ReadOnlyMemory<char> content, CancellationToken cancellationToken)
    {
        Mode.ThrowIfNotWritable();

        throw new NotImplementedException($"WriteAsync must be implemented by {GetType().Name}");
    }

    public virtual ValueTask WriteAsync(string content, CancellationToken cancellationToken)
    {
        return ((ILuaStream)this).WriteAsync(content.AsMemory(), cancellationToken);
    }

    public virtual ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        // Default implementation does nothing (no buffering)
        return default;
    }

    public virtual void SetVBuf(LuaFileBufferingMode mode, int size)
    {
        // Default implementation does nothing (no configurable buffering)
    }

    public virtual long Seek(SeekOrigin origin, long offset)
    {
        throw new NotSupportedException($"Seek is not supported by {GetType().Name}");
    }

    public static ILuaStream CreateFromStream(Stream stream, LuaFileOpenMode openMode)
    {
        return new LuaStream(openMode, stream);
    }

    public static ILuaStream CreateFromStream(
        Stream stream,
        LuaFileOpenMode openMode,
        LuaStringMode stringMode
    )
    {
        return new LuaStream(openMode, stream, stringMode);
    }

    public static ILuaStream CreateFromString(string content)
    {
        return new StringStream(content);
    }

    public static ILuaStream CreateFromMemory(ReadOnlyMemory<char> content)
    {
        return new CharMemoryStream(content);
    }

    public virtual ValueTask CloseAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return default;
    }
}
