// LUA [-e CHUNK | FILE | --smoke]: Lua-CSharp on SharpOS.
//
// With no arguments, a prompt. In a pipeline (an input handed over), the
// chunk returns a function, called with each message as a Lua value — an
// object a table of its fields, an array a table from 1 — and what it
// returns, unless nil, goes on: a table as an Expando (a sequence as an
// array), anything else as its text.
//
//     SYSINFO 3>&- | LUA -e "return strings" | CONVERT --to jsonl
//
// Every state runs \bin\lua\init.lua first (LUA_INIT overrides), which
// requires prelude.lua: walk(v, f), strings(v), keyed(v). require looks in
// the current folder and \bin\lua (LUA_PATH overrides).

using System.Runtime;
using Lua;
using Lua.Runtime;
using Lua.Standard;
using SharpOS.AppSdk;
using SharpOS.Std.Exchange;
using SharpOS.Std.Pipes;

internal static unsafe class AppEntry
{
    [RuntimeExport("SharpAppEntry")]
    private static int SharpAppEntry(ulong startupPointer)
    {
        AppRuntime.Initialize((AppStartupBlock*)startupPointer);
        int code = Run();
        FlushOutput();
        return AppRuntime.Finish(code);
    }

    // Lua's standard output is buffered, as C's stdio is, and lua.c gets it
    // flushed at exit by the C runtime. Nothing does that here: io.write
    // without io.flush was lost when the program ended (step214).
    private static Lua.IO.BufferedOutputStream s_output;

    private static void FlushOutput()
    {
        try { s_output?.FlushAsync().AsTask().GetAwaiter().GetResult(); }
        catch (SharpOS.Std.Pipes.PipeException) { }
    }

    [RuntimeExport("SharpAppBootstrap")]
    private static int SharpAppBootstrap(ulong startupPointer)
    {
        RuntimeImports.ManagedStartup();
        return SharpAppEntry(startupPointer);
    }

    private static int Main() => Run();

    private const string Smoke = """
        print("lua " .. _VERSION)
        print(1 + 1, 7 % 3, 2 ^ 10, 10 / 4, math.floor(7 / 2))
        local t = {}
        for i = 1, 5 do t[#t + 1] = i * i end
        print(table.concat(t, ","))
        print(string.format("%5.2f|%s|%d|%x", math.pi, "x", 42, 255))
        print(("hello world"):gsub("o", "0"))
        print(string.match("key=value", "(%w+)=(%w+)"))
        local function walk(v)
          if type(v) == "table" then
            for k, x in pairs(v) do v[k] = walk(x) end
            return v
          end
          if type(v) == "number" or type(v) == "boolean" then return tostring(v) end
          return v
        end
        local d = walk({ a = 1, b = { c = true, s = "s" } })
        print(d.a, type(d.a), d.b.c, type(d.b.c), d.b.s)
        print(pcall(function() error("boom") end))
        local co = coroutine.create(function(a) local b = coroutine.yield(a + 1) return b * 2 end)
        print(coroutine.resume(co, 1))
        print(coroutine.resume(co, 10))
        local s = 0
        for i = 1, 100000 do s = s + i end
        print("sum", s)
        print(os.time() > 0, os.clock() >= 0, os.clock(), os.date("!%Y-%m-%d"))
        return "done"
        """;

    private static int Run()
    {
        string[] args = AppHost.Arguments;
        if (args.Length == 1 && args[0] == "--clock")
        {
            // Diagnostic: the counter behind os.clock, read back to back.
            Console.WriteLine("frequency " + System.Diagnostics.Stopwatch.Frequency.ToString());
            long previous = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < 8; i++)
            {
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                Console.WriteLine(now.ToString() + " step " + (now - previous).ToString());
                previous = now;
            }
            return 0;
        }
        string chunk;
        string name;
        if (args.Length >= 2 && args[0] == "-e")
        {
            chunk = args[1];
            name = "=(command line)";
        }
        else if (args.Length == 1 && args[0] == "--smoke")
        {
            chunk = Smoke;
            name = "=smoke";
        }
        else if (args.Length >= 1)
        {
            name = args[0];
            chunk = System.IO.File.ReadAllText(Process.ResolvePath(args[0]));
        }
        else
        {
            return Interactive();
        }

        if (PipeTransport.StandardEnd(0) != 0) return Filter(NewState(FilterPlatform()), chunk, name);
        var state = NewState();
        try
        {
            LuaValue[] results = state.DoStringAsync(chunk, name).AsTask().GetAwaiter().GetResult();
            foreach (LuaValue result in results)
                Console.WriteLine(result.ToString());
            return 0;
        }
        catch (Exception e) // LuaRuntimeException, LuaParseException, LuaCompileException, host errors
        {
            Console.Error.WriteLine("lua: " + e.Message);
            return 1;
        }
    }

    // Where `require` looks, as lua.c has it: LUA_PATH when set, else the
    // current folder and \bin\lua, where the image keeps LUA's own files
    // (apps_native/LuaApp/esp/lua: init.lua, prelude.lua).
    private const string DefaultPath = "?.lua;\\bin\\lua\\?.lua";
    private const string DefaultInit = "\\bin\\lua\\init.lua";

    // In a pipeline the standard output carries the messages and the input
    // is the object pipe: print and io.write go to the error stream (on the
    // screen, out of the data), io.read sees an empty input.
    private static Lua.Platforms.LuaPlatform FilterPlatform() =>
        Lua.Platforms.LuaPlatform.Default with { StandardIO = new FilterStandardIO() };

    private sealed class FilterStandardIO : Lua.IO.ILuaStandardIO
    {
        private Lua.IO.ILuaStream _input, _output;

        public Lua.IO.ILuaStream Input => _input ??= new Lua.IO.StringStream("");

        public Lua.IO.ILuaStream Output =>
            _output ??= s_output = new Lua.IO.BufferedOutputStream(text => Console.Error.Write(text.ToString()));

        public Lua.IO.ILuaStream Error => Output;
    }

    // Outside a pipeline the standard output is text: io.write and print go
    // through Console as the printed results do. Lua's own console streams
    // would open it as bytes first (OpenStandardLibraries makes io.stdout at
    // once), and then the first printed result is refused — a pipe has one type.
    private static Lua.Platforms.LuaPlatform TerminalPlatform() =>
        Lua.Platforms.LuaPlatform.Default with { StandardIO = new TerminalStandardIO() };

    private sealed class TerminalStandardIO : Lua.IO.ILuaStandardIO
    {
        private Lua.IO.ILuaStream _input, _output, _error;

        public Lua.IO.ILuaStream Input => _input ??= Lua.IO.LuaStreamDefaults.CreateFromStream(
            Console.OpenStandardInput(), Lua.IO.LuaFileOpenMode.Read, Lua.LuaStringMode.Utf16);

        public Lua.IO.ILuaStream Output =>
            _output ??= s_output = new Lua.IO.BufferedOutputStream(text => Console.Write(text.ToString()));

        public Lua.IO.ILuaStream Error =>
            _error ??= new Lua.IO.BufferedOutputStream(text => Console.Error.Write(text.ToString()));
    }

    private static LuaState NewState(Lua.Platforms.LuaPlatform platform = null)
    {
        var state = LuaState.Create(platform ?? TerminalPlatform());
        state.OpenStandardLibraries();

        string path = Environment.GetEnvironmentVariable("LUA_PATH");
        if (string.IsNullOrEmpty(path)) path = DefaultPath;
        else path = path.Replace(";;", ";" + DefaultPath + ";");   // ";;" is the default, as in lua.c
        state.DoStringAsync("package.path = ...".Replace("...", Quote(path)), "=path").AsTask().GetAwaiter().GetResult();

        // LUA_INIT: "@file" runs the file, anything else is Lua code; without
        // it, \bin\lua\init.lua when the image has one. Its failure is said,
        // not fatal: the script still runs.
        string init = Environment.GetEnvironmentVariable("LUA_INIT");
        try
        {
            if (!string.IsNullOrEmpty(init))
            {
                if (init[0] == '@') RunFile(state, init.Substring(1));
                else state.DoStringAsync(init, "=LUA_INIT").AsTask().GetAwaiter().GetResult();
            }
            else if (System.IO.File.Exists(DefaultInit))
            {
                RunFile(state, DefaultInit);
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("lua: init: " + e.Message);
        }
        return state;
    }

    private static void RunFile(LuaState state, string file) =>
        state.DoStringAsync(System.IO.File.ReadAllText(Process.ResolvePath(file)), "@" + file).AsTask().GetAwaiter().GetResult();

    // A Lua string literal of any text: long brackets with enough '='.
    private static string Quote(string text)
    {
        string level = "";
        while (text.Contains("]" + level + "]")) level += "=";
        return "[" + level + "[" + text + "]" + level + "]";
    }

    // The chunk returns a function; each message through it; what comes back
    // out, unless nil. The output's type is the first result's: a table makes
    // a pipe of Expando, anything else a pipe of strings (its text).
    private static int Filter(LuaState state, string chunk, string name)
    {
        PipeWriter<Expando> objects = null;
        PipeWriter<string> texts = null;
        try
        {
            LuaValue[] made = state.DoStringAsync(chunk, name).AsTask().GetAwaiter().GetResult();
            if (made.Length == 0 || made[0].Type != LuaValueType.Function)
            {
                Console.Error.WriteLine("lua: in a pipeline the script returns a function, called with each message");
                return 2;
            }
            LuaValue function = made[0];
            foreach (View message in Pipe.Read())
            {
                LuaValue[] results = state.CallAsync(function, new[] { ToLua(message) }).AsTask().GetAwaiter().GetResult();
                if (results.Length == 0 || results[0].Type == LuaValueType.Nil) continue;
                LuaValue result = results[0];
                if (objects == null && texts == null)
                {
                    if (result.Type == LuaValueType.Table) objects = Pipe.Write<Expando>();
                    else texts = Pipe.Write<string>();
                }
                if (objects != null)
                {
                    object value = FromLua(result);
                    objects.Copy(value as Expando ?? new Expando { ["value"] = value });
                }
                else
                {
                    texts.Copy(result.Type == LuaValueType.String ? result.Read<string>() : result.ToString());
                }
            }
            return 0;
        }
        catch (PipeException e) when (e.IsStandardEndBroken)
        {
            return 141;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("lua: " + e.Message);
            return 1;
        }
        finally
        {
            objects?.Dispose();
            texts?.Dispose();
        }
    }

    // A message as Lua sees it: fields as a table, arrays from 1, numbers as
    // numbers; a struct of std (DateTime and the like) as its text.
    private static LuaValue ToLua(View v)
    {
        switch (v.Kind)
        {
            case ViewKind.Null: return LuaValue.Nil;
            case ViewKind.Bool: return (bool)v;
            case ViewKind.Integer: return (double)(long)v;
            case ViewKind.Float: return (double)v;
            case ViewKind.Char: return ((char)v).ToString();
            case ViewKind.String: return (string)v;
            case ViewKind.Array:
            {
                var a = new LuaTable();
                int i = 1;
                foreach (View item in v) a[i++] = ToLua(item);
                return a;
            }
            default:
            {
                TypeShape shape = v.Shape;
                if (shape != null && shape.IsValueType)
                {
                    object value = ViewCopy.ToHeapValue(v);
                    if (value is DateTime d) return d.ToString("o");
                    if (value is not Expando) return value?.ToString() ?? LuaValue.Nil;
                }
                var t = new LuaTable();
                foreach (ViewField field in v.Fields) t[field.Name] = ToLua(field.Value);
                return t;
            }
        }
    }

    // A Lua value as a message value: a table with keys 1..n only as an
    // array, any other as an Expando (keys as their text); a whole number as
    // long, others as double.
    private static object FromLua(LuaValue v)
    {
        switch (v.Type)
        {
            case LuaValueType.Nil: return null;
            case LuaValueType.Boolean: return v.Read<bool>();
            case LuaValueType.String: return v.Read<string>();
            case LuaValueType.Number:
            {
                double d = v.Read<double>();
                return d == Math.Floor(d) && Math.Abs(d) < 9.2e18 ? (object)(long)d : d;
            }
            case LuaValueType.Table:
            {
                LuaTable t = v.Read<LuaTable>();
                int n = t.ArrayLength;
                if (n > 0 && t.HashMapCount == 0)
                {
                    var items = new object[n];
                    for (int i = 0; i < n; i++) items[i] = FromLua(t[i + 1]);
                    return items;
                }
                var e = new Expando();
                foreach (KeyValuePair<LuaValue, LuaValue> p in t)
                    e[p.Key.Type == LuaValueType.String ? p.Key.Read<string>() : p.Key.ToString()] = FromLua(p.Value);
                return e;
            }
            default: return v.ToString();
        }
    }

    // A line at a time, as lua.c does it: first as an expression
    // ("return " + line, its values printed), then as a statement. One state
    // for the whole session; Ctrl+D on an empty line or "exit" leaves.
    private static int Interactive()
    {
        var state = NewState();
        AppHost.Terminal.Write("Lua 5.2 (Lua-CSharp) - an expression prints its value; exit or Ctrl+D leaves\n");
        while (true)
        {
            string line = ReadLine("> ");
            if (line == null || line.Trim() == "exit") return 0;
            if (line.Trim().Length == 0) continue;
            try
            {
                // Compiled first, run after: a line that is not an expression
                // falls back to a statement before anything has run.
                LuaClosure chunk;
                try
                {
                    chunk = state.Load(("return " + line).AsSpan(), "=stdin");
                }
                catch (Exception e) when (e is LuaParseException || e is LuaCompileException)
                {
                    chunk = state.Load(line.AsSpan(), "=stdin");
                }
                LuaValue[] results = state.CallAsync(chunk, ReadOnlySpan<LuaValue>.Empty).AsTask().GetAwaiter().GetResult();
                if (results.Length > 0)
                {
                    var text = new System.Text.StringBuilder();
                    for (int i = 0; i < results.Length; i++)
                    {
                        if (i > 0) text.Append('\t');
                        text.Append(results[i].ToString());
                    }
                    Console.WriteLine(text.ToString());
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e.Message);
            }
        }
    }

    // The line being typed, echoed to the terminal: printable keys,
    // Backspace, Enter; Ctrl+D on an empty line is the end of input.
    private static string ReadLine(string prompt)
    {
        AppHost.Terminal.Write(prompt);
        var line = new System.Text.StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                AppHost.Terminal.Write("\n");
                return line.ToString();
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (line.Length > 0)
                {
                    line.Length--;
                    AppHost.Terminal.Write("\b \b");
                }
                continue;
            }
            if ((key.Modifiers & ConsoleModifiers.Control) != 0 && key.Key == ConsoleKey.D && line.Length == 0)
            {
                AppHost.Terminal.Write("\n");
                return null;
            }
            if (key.KeyChar >= ' ')
            {
                line.Append(key.KeyChar);
                AppHost.Terminal.WriteChar(key.KeyChar);
            }
        }
    }
}
