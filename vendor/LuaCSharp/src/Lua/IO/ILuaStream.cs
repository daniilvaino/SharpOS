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
    // Explicit implementations, not overridable members: the implementer's
    // own public members take the mapping (it lists ILuaStream again)
    // without hiding anything here (CS0114), and every default reaches the
    // implementer through the interface — a plain Mode or Dispose() here
    // would be this class's, which throws NotImplementedException.
    private ILuaStream Self => this;

    bool ILuaStream.IsOpen => throw new NotImplementedException();
    LuaFileOpenMode ILuaStream.Mode => throw new NotImplementedException();
    void IDisposable.Dispose() => throw new NotImplementedException();

    ValueTask<string> ILuaStream.ReadAllAsync(CancellationToken cancellationToken)
    {
        Self.Mode.ThrowIfNotReadable();

        // Default implementation using ReadStringAsync
        throw new NotImplementedException($"ReadAllAsync must be implemented by {GetType().Name}");
    }

    ValueTask<double?> ILuaStream.ReadNumberAsync(CancellationToken cancellationToken)
    {
        Self.Mode.ThrowIfNotReadable();

        // Default implementation using ReadStringAsync
        throw new NotImplementedException(
            $"ReadNumberAsync must be implemented by {GetType().Name}"
        );
    }

    ValueTask<string?> ILuaStream.ReadLineAsync(bool keepEol, CancellationToken cancellationToken)
    {
        Self.Mode.ThrowIfNotReadable();

        // Default implementation using ReadStringAsync
        throw new NotImplementedException($"ReadLineAsync must be implemented by {GetType().Name}");
    }

    ValueTask<string?> ILuaStream.ReadAsync(int count, CancellationToken cancellationToken)
    {
        Self.Mode.ThrowIfNotReadable();

        // Default implementation using ReadAllAsync
        throw new NotImplementedException(
            $"ReadStringAsync must be implemented by {GetType().Name}"
        );
    }

    ValueTask ILuaStream.WriteAsync(ReadOnlyMemory<char> content, CancellationToken cancellationToken)
    {
        Self.Mode.ThrowIfNotWritable();

        throw new NotImplementedException($"WriteAsync must be implemented by {GetType().Name}");
    }

    ValueTask ILuaStream.WriteAsync(string content, CancellationToken cancellationToken)
    {
        return Self.WriteAsync(content.AsMemory(), cancellationToken);
    }

    ValueTask ILuaStream.FlushAsync(CancellationToken cancellationToken)
    {
        // Default implementation does nothing (no buffering)
        return default;
    }

    void ILuaStream.SetVBuf(LuaFileBufferingMode mode, int size)
    {
        // Default implementation does nothing (no configurable buffering)
    }

    long ILuaStream.Seek(SeekOrigin origin, long offset)
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

    ValueTask ILuaStream.CloseAsync(CancellationToken cancellationToken)
    {
        Self.Dispose();
        return default;
    }
}
