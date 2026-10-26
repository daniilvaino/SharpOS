// JS — JavaScript on Okojo (MIT, akeit0/okojo; vendor/Okojo).
//
//   JS -e CODE     runs CODE and prints its value unless it is undefined
//   JS FILE        runs a script file
//   JS             a prompt: each line runs in one realm, its value printed
//
// In a pipeline (the input is an object pipe) the code's value is a function,
// called with each message as an object:
//   pipegen 3 | js -e "m => ({...m, twice: m.Level * 2})" | convert --to jsonl
// What it returns goes out unless null or undefined: an object makes a pipe of
// Expando, anything else a pipe of strings. console.log there goes to the
// error stream — the standard output carries the messages.
//
// console.log prints its arguments separated by spaces. Errors go to the
// error stream and the exit code is 1.

using System.Runtime;
using Okojo.JavaScript;
using Okojo.JavaScript.Embedding;
using Okojo.JavaScript.Objects;
using SharpOS.AppSdk;
using SharpOS.Std.Exchange;
using SharpOS.Std.Pipes;

internal static unsafe class AppEntry
{
    [RuntimeExport("SharpAppEntry")]
    private static int SharpAppEntry(ulong startupPointer)
    {
        AppRuntime.Initialize((AppStartupBlock*)startupPointer);
        return Run();
    }

    [RuntimeExport("SharpAppBootstrap")]
    private static int SharpAppBootstrap(ulong startupPointer)
    {
        RuntimeImports.ManagedStartup();
        return SharpAppEntry(startupPointer);
    }

    private static int Main() => Run();

    private static int Run()
    {
        string[] args = AppHost.Arguments;
        if (args.Length == 0) return Interactive();

        string source;
        bool print;
        if (args.Length >= 2 && args[0] == "-e")
        {
            source = args[1];
            print = true;
        }
        else
        {
            source = System.IO.File.ReadAllText(Process.ResolvePath(args[0]));
            print = false;
        }

        if (PipeTransport.StandardEnd(0) != 0) return Filter(source);

        using var runtime = NewRuntime();
        try
        {
            JsValue result = runtime.MainRealm.Eval(source);
            if (print && !result.IsUndefined) Console.WriteLine(result.ToString());
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("js: " + e.Message);
            return 1;
        }
    }

    private static JsRuntime NewRuntime(bool filter = false) =>
        JsRuntime.CreateBuilder()
            .UseRealmSetup(filter ? InstallFilterConsole : InstallConsole)
            .Build();

    // console.log / console.error: the arguments as text, spaces between.
    private static void InstallConsole(JsRealm realm) => InstallConsole(realm, toErrorStream: false);

    // In a pipeline console.log goes to the error stream too.
    private static void InstallFilterConsole(JsRealm realm) => InstallConsole(realm, toErrorStream: true);

    private static void InstallConsole(JsRealm realm, bool toErrorStream)
    {
        var console = new JsPlainObject(realm);
        console.SetProperty("log", JsValue.FromObject(toErrorStream
            ? new JsHostFunction(realm, static (in info) =>
            {
                Console.Error.WriteLine(Join(info.Arguments));
                return JsValue.Undefined;
            }, "log", 1)
            : new JsHostFunction(realm, static (in info) =>
            {
                Console.WriteLine(Join(info.Arguments));
                return JsValue.Undefined;
            }, "log", 1)));
        console.SetProperty("error", JsValue.FromObject(new JsHostFunction(realm, static (in info) =>
        {
            Console.Error.WriteLine(Join(info.Arguments));
            return JsValue.Undefined;
        }, "error", 1)));
        realm.Global["console"] = JsValue.FromObject(console);
    }

    private static string Join(ReadOnlySpan<JsValue> args)
    {
        var text = new System.Text.StringBuilder();
        for (int i = 0; i < args.Length; i++)
        {
            if (i > 0) text.Append(' ');
            text.Append(args[i].ToString());
        }
        return text.ToString();
    }

    // The code's value is a function; each message through it; what comes back
    // goes out unless null or undefined. The output's type is the first
    // result's: an object makes a pipe of Expando, anything else of strings.
    private static int Filter(string source)
    {
        PipeWriter<Expando> objects = null;
        PipeWriter<string> texts = null;
        using var runtime = NewRuntime(filter: true);
        JsRealm realm = runtime.MainRealm;
        try
        {
            JsValue made = realm.Eval(source);
            if (!made.TryGetObject(out JsObject made_) || made_ is not JsFunction function)
            {
                Console.Error.WriteLine("js: in a pipeline the code's value is a function, called with each message");
                return 2;
            }
            realm.Eval("Object.keys").TryGetObject(out JsObject keysObject);
            var keys = (JsFunction)keysObject;

            var argument = new JsValue[1];
            foreach (View message in Pipe.Read())
            {
                argument[0] = ToJs(message, realm);
                JsValue result = function.Call(realm, JsValue.Undefined, argument);
                runtime.MainRealm.PumpJobs();
                if (result.IsNullOrUndefined) continue;
                if (objects == null && texts == null)
                {
                    if (result.IsObject) objects = Pipe.Write<Expando>();
                    else texts = Pipe.Write<string>();
                }
                if (objects != null)
                {
                    object value = FromJs(result, realm, keys);
                    objects.Copy(value as Expando ?? new Expando { ["value"] = value });
                }
                else
                {
                    texts.Copy(result.IsString ? result.AsString() : result.ToString());
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
            Console.Error.WriteLine("js: " + e.Message);
            return 1;
        }
        finally
        {
            objects?.Dispose();
            texts?.Dispose();
        }
    }

    // A message as JavaScript sees it: fields as an object's properties,
    // arrays as arrays, numbers as numbers; a struct of std (DateTime and the
    // like) as its text — a DateTime in the round-trip form.
    private static JsValue ToJs(View v, JsRealm realm)
    {
        switch (v.Kind)
        {
            case ViewKind.Null: return JsValue.Null;
            case ViewKind.Bool: return (bool)v ? JsValue.True : JsValue.False;
            case ViewKind.Integer:
            {
                long n = (long)v;
                return n >= int.MinValue && n <= int.MaxValue ? JsValue.FromInt32((int)n) : JsValue.FromFloat64(n);
            }
            case ViewKind.Float: return JsValue.FromFloat64((double)v);
            case ViewKind.Char: return JsValue.FromString(((char)v).ToString());
            case ViewKind.String: return JsValue.FromString((string)v);
            case ViewKind.Array:
            {
                var a = new JsArray(realm);
                uint i = 0;
                foreach (View item in v) a.SetElement(i++, ToJs(item, realm));
                return JsValue.FromObject(a);
            }
            default:
            {
                TypeShape shape = v.Shape;
                if (shape != null && shape.IsValueType)
                {
                    object value = ViewCopy.ToHeapValue(v);
                    if (value is DateTime d) return JsValue.FromString(d.ToString("o"));
                    if (value is not Expando) return value == null ? JsValue.Null : JsValue.FromString(value.ToString());
                }
                var o = new JsPlainObject(realm);
                foreach (ViewField field in v.Fields) o.SetProperty(new string(field.Name.AsSpan()), ToJs(field.Value, realm)); // the name is in the region: a copy
                return JsValue.FromObject(o);
            }
        }
    }

    // A JavaScript value as a message value: an array as object[], any other
    // object as an Expando of its own enumerable properties (Object.keys); a
    // whole number as long, others as double.
    private static object FromJs(JsValue v, JsRealm realm, JsFunction keys)
    {
        if (v.IsNullOrUndefined) return null;
        if (v.IsBool) return v.IsTrue;
        if (v.IsString) return v.AsString();
        if (v.IsNumber)
        {
            double d = v.NumberValue;
            return d == Math.Floor(d) && Math.Abs(d) < 9.2e18 ? (object)(long)d : d;
        }
        if (v.TryGetObject(out JsObject obj))
        {
            if (obj is JsFunction) return v.ToString();
            if (obj is JsArray array)
            {
                var items = new object[array.Length];
                for (uint i = 0; i < array.Length; i++)
                    items[i] = array.TryGetElement(i, out JsValue item) ? FromJs(item, realm, keys) : null;
                return items;
            }
            var e = new Expando();
            JsValue names = keys.Call(realm, JsValue.Undefined, new[] { v });
            if (names.TryGetObject(out JsObject list) && list is JsArray nameArray)
            {
                for (uint i = 0; i < nameArray.Length; i++)
                {
                    if (!nameArray.TryGetElement(i, out JsValue name)) continue;
                    string key = name.IsString ? name.AsString() : name.ToString();
                    obj.TryGetProperty(key, out JsValue field);
                    e[key] = FromJs(field, realm, keys);
                }
            }
            return e;
        }
        return v.ToString();
    }

    // One realm for the session; a line's value is printed unless undefined.
    // Ctrl+D on an empty line or "exit" leaves.
    private static int Interactive()
    {
        using var runtime = NewRuntime();
        AppHost.WriteString("JavaScript (Okojo) - a line's value prints; exit or Ctrl+D leaves\n");
        while (true)
        {
            string line = ReadLine("> ");
            if (line == null || line.Trim() == "exit") return 0;
            if (line.Trim().Length == 0) continue;
            try
            {
                JsValue result = runtime.MainRealm.Eval(line);
                if (!result.IsUndefined) Console.WriteLine(result.ToString());
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
        AppHost.WriteString(prompt);
        var line = new System.Text.StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                AppHost.WriteString("\n");
                return line.ToString();
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (line.Length > 0)
                {
                    line.Length--;
                    AppHost.WriteString("\b \b");
                }
                continue;
            }
            if ((key.Modifiers & ConsoleModifiers.Control) != 0 && key.Key == ConsoleKey.D && line.Length == 0)
            {
                AppHost.WriteString("\n");
                return null;
            }
            if (key.KeyChar >= ' ')
            {
                line.Append(key.KeyChar);
                AppHost.WriteChar(key.KeyChar);
            }
        }
    }
}
