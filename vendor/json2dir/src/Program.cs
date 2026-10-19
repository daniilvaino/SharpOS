// json2dir: create the directory tree a JSON document describes, in the current directory.
// Implements RFC J2D-1 (https://github.com/kitsunoff/awesome-json2dir/blob/main/spec/rfc-json2dir.md).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

// POSIX only: file modes and symlinks follow §4.3–§4.4.
[assembly: System.Runtime.Versioning.UnsupportedOSPlatform("windows")]

sealed class Json2dirException : Exception
{
    public Json2dirException(string message) : base(message) { }
}

static class Program
{
    static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

    static void Fail(string message) => throw new Json2dirException(message);

    static object Parse(byte[] bytes)
    {
        // §3: a byte order mark at the start may be ignored.
        int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        string text;
        try { text = StrictUtf8.GetString(bytes, start, bytes.Length - start); }
        catch (DecoderFallbackException) { throw new Json2dirException("input is not valid UTF-8"); }
        return JsonParser.Parse(text);
    }

    static string Show(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
            else if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    // §4, §6: validate the whole document before touching the file system.
    static void Validate(object value, string where)
    {
        switch (value)
        {
            case string:
                return;
            case List<object> a:
                if (a.Count != 2 || a[0] is not string kind || a[1] is not string payload)
                {
                    Fail($"{where}: an array must be [\"link\", target] or [\"script\", content]");
                    return;
                }
                if (kind != "link" && kind != "script") Fail($"{where}: unknown array kind {Show(kind)}");
                // SharpOS cut: FAT has no symbolic links. Refused here, while the
                // whole document is checked — before anything is written.
                if (kind == "link") Fail($"{where}: symbolic links are not supported on FAT");
                // §10: a link target with NUL cannot be created, so it is rejected before anything is written.
                if (kind == "link" && payload.Contains('\0')) Fail($"{where}: a link target cannot contain NUL");
                return;
            case JsonObject o:
                foreach (var (name, child) in o)
                {
                    string path = where == "." ? name : where + "/" + name;
                    // §4.2.1: names are used exactly; trailing "/" or "/." forms are rejected, not trimmed.
                    if (name.Length == 0 || name == "." || name == ".." || name.Contains('/') || name.Contains('\0'))
                        Fail($"{path}: invalid name {Show(name)}");
                    Validate(child, path);
                }
                return;
            case JsonOther other:
                Fail($"{where}: {other.Kind} values are not allowed");
                return;
        }
    }

    // Members in ascending order of the UTF-8 bytes of their names, like the reference.
    static int CompareUtf8(string a, string b) =>
        ((ReadOnlySpan<byte>)Encoding.UTF8.GetBytes(a)).SequenceCompareTo(Encoding.UTF8.GetBytes(b));

    // lstat-like: .NET reports a symlink itself (ReparsePoint), never following it. Null when nothing is there.
    static FileAttributes? Lstat(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    static bool IsRealDirectory(FileAttributes? a) =>
        a is FileAttributes f && (f & FileAttributes.Directory) != 0 && (f & FileAttributes.ReparsePoint) == 0;

    // §5.2, §5.3: an existing non-directory is removed (a symlink itself, never its target);
    // §5.4: a directory in the way of a non-object is an error.
    static void Clear(string path, FileAttributes? existing)
    {
        if (existing == null) return;
        if (IsRealDirectory(existing)) Fail($"{path}: a directory is in the way");
        File.Delete(path);
    }

    static void WriteFile(string path, string content, bool executable)
    {
        // CreateNew = O_CREAT | O_EXCL with mode 0666 & ~umask: never writes through an entry that appeared after Clear().
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            fs.Write(Encoding.UTF8.GetBytes(content));
        // SharpOS cut: FAT has no mode bits — a script is written as an
        // ordinary file, with no execute bit to set.
        _ = executable;
    }

    static void Apply(string dir, JsonObject tree)
    {
        var names = new List<string>(tree.Keys);
        names.Sort(CompareUtf8);
        foreach (string name in names)
        {
            string path = dir == "." ? name : dir + "/" + name;
            object value = tree[name];
            var existing = Lstat(path);
            if (value is string content)
            {
                Clear(path, existing);
                WriteFile(path, content, false);
            }
            else if (value is List<object> a)
            {
                Clear(path, existing);
                // SharpOS cut: no File.CreateSymbolicLink — Validate refused every link.
                WriteFile(path, (string)a[1], true);
            }
            else
            {
                if (!IsRealDirectory(existing))
                {
                    if (existing != null) File.Delete(path);
                    Directory.CreateDirectory(path);
                }
                Apply(path, (JsonObject)value);
            }
        }
    }

    static int Run(string[] args)
    {
        if (args.Length > 0)
        {
            Console.Error.WriteLine("usage: json2dir < document.json");
            return 2;
        }
        try
        {
            var input = new MemoryStream();
            using (var stdin = Console.OpenStandardInput()) stdin.CopyTo(input);
            object document = Parse(input.ToArray());
            if (document is not JsonObject root)
            {
                Fail("the root of the document must be an object");
                return 1;
            }
            Validate(root, ".");
            Apply(".", root);
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"json2dir: {e.Message}");
            return 1;
        }
    }

    // SharpOS cut: internal, for the app's entry (apps_native/Json2Dir/Entry.cs).
    internal static int Main(string[] args)
    {
        // SharpOS cut: no thread with a 256 MiB stack — threads are started
        // through Task.Run here, and their stacks are the tier's own size.
        // Run on the program's own thread; a document nested deeper than its
        // stack allows fails.
        return Run(args);
    }
}
