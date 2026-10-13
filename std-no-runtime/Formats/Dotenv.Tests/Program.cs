// compose-go's tests for dotenv and template (MIT / Apache-2.0, see
// ../Dotenv/LICENSE-*), carried over to SharpOS.Std.Dotenv; fixtures/ are theirs.
// Each block names the Go test it comes from. Left out: what tests the parts
// not here — Load/os.Setenv (the process environment), GetEnvFromFile and
// RegisterFormat (files, formats), the logrus warning counts, TestMemoryExplosion
// (Go's allocator).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SharpOS.Std.Dotenv;

internal static class Program
{
    private static int s_passed, s_failed;

    private static void Check(string name, bool ok, string detail = null)
    {
        if (ok) s_passed++;
        else
        {
            s_failed++;
            Console.WriteLine("FAIL " + name + (detail != null ? ": " + detail : ""));
        }
    }

    private static int Main()
    {
        Dotenv();
        VarExpansion();
        ParserTests();
        Template();
        Console.WriteLine("passed " + s_passed + ", failed " + s_failed);
        return s_failed == 0 ? 0 : 1;
    }

    // ---- helpers ----

    private static Dictionary<string, string> Parse(string text, LookupFn lookup = null) => DotenvParser.UnmarshalWithLookup(text, lookup);

    // As bytes: ReadAllText would take the BOM away before the parser sees it.
    private static string Fixture(string name) => System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", name)));

    // Read/ReadWithLookup: a file's variables, keys starting with a digit
    // left out (startsWithDigitRegex in godotenv.go).
    private static Dictionary<string, string> Read(string fixture, LookupFn lookup = null)
    {
        var all = Parse(Fixture(fixture), lookup);
        return all.Where(kv => !(kv.Key.TrimStart().Length > 0 && char.IsDigit(kv.Key.TrimStart()[0])))
                  .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private static string Show(IDictionary<string, string> m) => "{" + string.Join(", ", m.Select(kv => kv.Key + "=" + Escape(kv.Value))) + "}";

    private static string Escape(string s) => s == null ? "null" : "\"" + s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t") + "\"";

    private static void Values(string name, IDictionary<string, string> got, Dictionary<string, string> expected, bool exactSize = false)
    {
        bool ok = !exactSize || got.Count == expected.Count;
        foreach (var kv in expected)
        {
            string actual = got.TryGetValue(kv.Key, out var v) ? v.Replace("\r\n", "\n") : null;
            if (actual != kv.Value) ok = false;
        }
        Check(name, ok, ok ? null : "got " + Show(got) + ", expected " + Show(expected));
    }

    private static void ParseAndCompare(string raw, string key, string value)
    {
        Dictionary<string, string> env;
        try { env = Parse(raw); }
        catch (Exception e) { Check("parse " + raw, false, e.Message); return; }
        bool found = env.TryGetValue(key, out var actual);
        Check("parse " + raw, found && actual == value, found ? "got " + Escape(actual) + ", expected " + Escape(value) : "key not found in " + Show(env));
    }

    private static Exception Error(Action a)
    {
        try { a(); return null; }
        catch (Exception e) { return e; }
    }

    private static LookupFn FromMap(Dictionary<string, string> m) => (string k, out string v) => m.TryGetValue(k, out v);

    // ---- godotenv_test.go ----

    private static void Dotenv()
    {
        // TestReadPlainEnv
        Values("TestReadPlainEnv", Read("plain.env"), new()
        {
            ["OPTION_A"] = "1", ["OPTION_B"] = "2", ["OPTION_C"] = "3", ["OPTION_D"] = "4",
            ["OPTION_E"] = "5", ["OPTION_F"] = "", ["OPTION_G"] = "", ["OPTION_H"] = "my string",
        }, exactSize: true);

        // TestParse
        Values("TestParse", Parse("ONE=1\nTWO='2'\nTHREE = \"3\""), new() { ["ONE"] = "1", ["TWO"] = "2", ["THREE"] = "3" });

        // TestLoadExportedEnv, TestLoadEqualsEnv
        Values("TestLoadExportedEnv", Parse(Fixture("exported.env")), new() { ["OPTION_A"] = "2", ["OPTION_B"] = "\\n" });
        Values("TestLoadEqualsEnv", Parse(Fixture("equals.env")), new() { ["OPTION_A"] = "postgres://localhost:5432/database?sslmode=disable" });

        // TestLoadQuotedEnv
        Values("TestLoadQuotedEnv", Parse(Fixture("quoted.env")), new()
        {
            ["OPTION_A"] = "1", ["OPTION_B"] = "2", ["OPTION_C"] = "", ["OPTION_D"] = "\\n",
            ["OPTION_E"] = "1", ["OPTION_F"] = "2", ["OPTION_G"] = "", ["OPTION_H"] = "\n",
            ["OPTION_I"] = "echo 'asd'",
            ["OPTION_J"] = "first line\nsecond line\nthird line\nand so on",
            ["OPTION_K"] = "Let's go!", ["OPTION_Z"] = "last value",
        });

        // TestLoadUnquotedEnv
        Values("TestLoadUnquotedEnv", Parse(Fixture("unquoted.env")), new()
        {
            ["OPTION_A"] = "some quoted phrase",
            ["OPTION_B"] = "first one with an unquoted phrase",
            ["OPTION_C"] = "then another one with an unquoted phrase",
            ["OPTION_D"] = "then another one with an unquoted phrase special è char",
            ["OPTION_E"] = "then another one quoted phrase",
        });

        // TestSubstitutions
        Values("TestSubstitutions", Parse(Fixture("substitutions.env")), new()
        {
            ["OPTION_A"] = "1", ["OPTION_B"] = "1", ["OPTION_C"] = "1", ["OPTION_D"] = "1_1", ["OPTION_E"] = "",
        });

        // TestExpanding
        Values("expands variables found in values", Parse("FOO=test\nBAR=$FOO"), new() { ["FOO"] = "test", ["BAR"] = "test" });
        Values("parses variables wrapped in brackets", Parse("FOO=test\nBAR=${FOO}bar"), new() { ["FOO"] = "test", ["BAR"] = "testbar" });
        Values("expands undefined variables to an empty string", Parse("BAR=$FOO"), new() { ["BAR"] = "" });
        Values("expands variables in double quoted strings", Parse("FOO=test\nBAR=\"quote $FOO\""), new() { ["FOO"] = "test", ["BAR"] = "quote test" });
        Values("does not expand variables in single quoted strings", Parse("BAR='quote $FOO'"), new() { ["BAR"] = "quote $FOO" });
        Values("does not expand escaped variables 1", Parse("""FOO="foo\$BAR" """.TrimEnd()), new() { ["FOO"] = "foo$BAR" });
        Values("does not expand escaped variables 2", Parse("""FOO="foo\${BAR}" """.TrimEnd()), new() { ["FOO"] = "foo${BAR}" });
        Values("does not expand escaped variables 3", Parse("FOO=test\nBAR=\"foo\\${FOO} ${FOO}\""), new() { ["FOO"] = "test", ["BAR"] = "foo${FOO} test" });

        // TestVariableStringValueSeparator
        Values("TestVariableStringValueSeparator",
               Parse("TEST_URLS=\"stratum+tcp://stratum.antpool.com:3333\nstratum+tcp://stratum.antpool.com:443\""),
               new() { ["TEST_URLS"] = "stratum+tcp://stratum.antpool.com:3333\nstratum+tcp://stratum.antpool.com:443" }, exactSize: true);

        // TestParsing
        ParseAndCompare("FOO=bar", "FOO", "bar");
        ParseAndCompare("FOO =bar", "FOO", "bar");
        ParseAndCompare("FOO= bar", "FOO", "bar");
        ParseAndCompare("""FOO="bar" """.TrimEnd(), "FOO", "bar");
        ParseAndCompare("FOO='bar'", "FOO", "bar");
        ParseAndCompare("""FOO="escaped\"bar" """.TrimEnd(), "FOO", "escaped\"bar");
        ParseAndCompare("""FOO="\"quoted\"" """.TrimEnd(), "FOO", "\"quoted\"");
        ParseAndCompare("""FOO="'d'" """.TrimEnd(), "FOO", "'d'");
        ParseAndCompare("OPTION_A: 1", "OPTION_A", "1");
        ParseAndCompare("OPTION_A: Foo=bar", "OPTION_A", "Foo=bar");
        ParseAndCompare("OPTION_A=1:B", "OPTION_A", "1:B");
        ParseAndCompare("export OPTION_A=2", "OPTION_A", "2");
        ParseAndCompare("""export OPTION_B='\n'""", "OPTION_B", "\\n");
        ParseAndCompare("export exportFoo=2", "exportFoo", "2");
        ParseAndCompare("exportFOO=2", "exportFOO", "2");
        ParseAndCompare("export_FOO =2", "export_FOO", "2");
        ParseAndCompare("export.FOO= 2", "export.FOO", "2");
        ParseAndCompare("export\tOPTION_A=2", "OPTION_A", "2");
        ParseAndCompare("  export OPTION_A=2", "OPTION_A", "2");
        ParseAndCompare("\texport OPTION_A=2", "OPTION_A", "2");
        ParseAndCompare("""export OPTION_A="export A" """.TrimEnd(), "OPTION_A", "export A");
        ParseAndCompare("""FOO="bar\nbaz" """.TrimEnd(), "FOO", "bar\nbaz");
        ParseAndCompare("""FOO=a\tb""", "FOO", "a\\tb");
        ParseAndCompare("""FOO="a\tb" """.TrimEnd(), "FOO", "a\tb");
        ParseAndCompare("""KEY="Z\aZ\bZ\fZ\nZ\rZ\tZ\vZ\\Z\0123Z" """.TrimEnd(), "KEY", "Z\aZ\bZ\fZ\nZ\rZ\tZ\vZ\\ZSZ");
        ParseAndCompare("FOO.BAR=foobar", "FOO.BAR", "foobar");
        ParseAndCompare("FOO=foobar=", "FOO", "foobar=");
        ParseAndCompare("FOO=bar ", "FOO", "bar");
        ParseAndCompare("FOO=bar # this is foo", "FOO", "bar");
        ParseAndCompare("FOO=bar #this is foo", "FOO", "bar");
        ParseAndCompare("FOO=bar #", "FOO", "bar");
        ParseAndCompare("FOO=123#not-an-inline-comment", "FOO", "123#not-an-inline-comment");
        ParseAndCompare("""FOO="bar#baz" """.TrimEnd(), "FOO", "bar#baz");
        ParseAndCompare("""FOO="bar#baz"#""", "FOO", "bar#baz");
        ParseAndCompare("""FOO="bar#baz" # comment""", "FOO", "bar#baz");
        ParseAndCompare("FOO='bar#baz' # comment", "FOO", "bar#baz");
        ParseAndCompare("""FOO="bar#baz#bang" # comment""", "FOO", "bar#baz#bang");
        ParseAndCompare("""FOO="ba#r" """.TrimEnd(), "FOO", "ba#r");
        ParseAndCompare("FOO='ba#r'", "FOO", "ba#r");
        ParseAndCompare("""FOO="bar\n\ b\az" """.TrimEnd(), "FOO", "bar\n\\ b\az");
        ParseAndCompare("""FOO="bar\\\n\ b\az" """.TrimEnd(), "FOO", "bar\\\n\\ b\az");
        ParseAndCompare("""FOO="bar\\r\ b\az" """.TrimEnd(), "FOO", "bar\\r\\ b\az");
        ParseAndCompare("""FOO="bar\nbaz\\" """.TrimEnd(), "FOO", "bar\nbaz\\");
        ParseAndCompare("""="value" """.TrimEnd(), "", "value");
        ParseAndCompare(" KEY =value", "KEY", "value");
        ParseAndCompare("   KEY=value", "KEY", "value");
        ParseAndCompare("\tKEY=value", "KEY", "value");
        ParseAndCompare("""KEY="\0123" """.TrimEnd(), "KEY", "S");
        ParseAndCompare("""KEY="\x07" """.TrimEnd(), "KEY", """\x07""");
        ParseAndCompare("""KEY="\u12e4" """.TrimEnd(), "KEY", """\u12e4""");
        ParseAndCompare("""KEY="\U00101234" """.TrimEnd(), "KEY", """\U00101234""");
        Check("badly formatted line: lol$wut", Error(() => Parse("lol$wut")) != null);

        // TestUnterminatedQuotes
        foreach (string tc in new[] { "KEY=\"", "KEY=\"value", "KEY=\"value\\\"", "KEY=\"value'", "KEY='", "KEY='value", "KEY='value\\'", "KEY='value\"" })
        {
            Exception e = Error(() => Parse(tc));
            Check("unterminated: " + tc, e != null && e.Message.Contains("unterminated quoted value"), e?.Message);
        }

        // TestLinesToIgnore
        foreach (var (input, want) in new[] { ("\n", ""), ("\r\n", ""), ("\t\t ", ""), ("# Comment", ""), ("\t # comment", ""), ("export OPTION_B='\\n'", "export OPTION_B='\\n'") })
        {
            string got = new Parser().GetStatementStart(input);
            Check("lines to ignore: " + Escape(input), got == want, Escape(got));
        }

        // TestErrorParsing
        Exception invalid = Error(() => Read("invalid1.env"));
        Check("TestErrorParsing", invalid != null && invalid.Message.Contains("line 7: key cannot contain a space"), invalid?.Message);

        // TestInheritedEnvVariableSameSize, SingleVar, NotFound, NotFoundWithLookup
        const string envKey = "VAR_TO_BE_LOADED_FROM_OS_ENV", envVal = "SOME_RANDOM_VALUE";
        var osEnv = new Dictionary<string, string> { [envKey] = envVal };
        Values("TestInheritedEnvVariableSameSize", Read("inherited-multi-var.env", FromMap(osEnv)),
               new() { [envKey] = envVal, ["foo"] = "bar", ["bar"] = "baz" }, exactSize: true);
        Values("TestInheritedEnvVariableSingleVar", Read("inherited-single-var.env", FromMap(osEnv)),
               new() { [envKey] = envVal }, exactSize: true);
        var notFound = Read("inherited-not-found.env");
        Check("TestInheritedEnvVariableNotFound", !notFound.ContainsKey("VARIABLE_NOT_FOUND"));
        var asked = new HashSet<string>();
        var notFound2 = Read("inherited-not-found.env", (string k, out string v) => { v = ""; asked.Add(k); return false; });
        Check("TestInheritedEnvVariableNotFoundWithLookup", !notFound2.ContainsKey("VARIABLE_NOT_FOUND") && asked.Contains("VARIABLE_NOT_FOUND"));

        // TestExpandingEnvironmentWithLookup
        var env = Parse("TEST=$ME", (string s, out string v) => { v = s == "ME" ? "YES" : "NO"; return s == "ME"; });
        Check("TestExpandingEnvironmentWithLookup", env["TEST"] == "YES", env["TEST"]);

        // TestSubstitutionsWithEnvFilePrecedence, ...DefaultValuePrecedence, ...UnsetVar...
        var optionA = new Dictionary<string, string> { ["OPTION_A"] = "5" };
        Values("TestSubstitutionsWithEnvFilePrecedence", Read("substitutions.env", FromMap(optionA)),
               new() { ["OPTION_A"] = "1", ["OPTION_B"] = "5", ["OPTION_C"] = "5", ["OPTION_D"] = "5_5", ["OPTION_E"] = "" }, exactSize: true);
        Values("TestSubstitutionsWithEnvFileDefaultValuePrecedence", Read("substitutions-default.env", FromMap(optionA)),
               new() { ["OPTION_A"] = "5", ["OPTION_B"] = "5", ["OPTION_C"] = "5", ["OPTION_D"] = "5_5", ["OPTION_E"] = "" }, exactSize: true);
        Values("TestSubstitutionsWithUnsetVarEnvFileDefaultValuePrecedence", Read("substitutions-default.env", FromMap(new())),
               new() { ["OPTION_A"] = "1", ["OPTION_B"] = "1", ["OPTION_C"] = "1", ["OPTION_D"] = "1_1", ["OPTION_E"] = "" }, exactSize: true);

        // TestUTF8BOM
        string bom = Fixture("utf8-bom.env");
        Check("TestUTF8BOM fixture has its BOM", bom.Length > 0 && bom[0] == '\uFEFF');
        Values("TestUTF8BOM", Parse(bom), new() { ["OPTION_A"] = "1", ["OPTION_B"] = "2", ["OPTION_C"] = "3", ["OPTION_D"] = "4", ["OPTION_E"] = "5" });

        // TestDash
        Values("TestDash", Parse(Fixture("special.env")), new() { ["VAR-WITH-DASHES"] = "dashes", ["VAR.WITH.DOTS"] = "dots", ["VAR_WITH_UNDERSCORES"] = "underscores" });

        // TestMultipleFiles (GetEnvFromFile: each file's lookup sees the variables so far)
        string baseEnv = "\nENV_HOSTNAME=localhost\nENV_MY_URL=\"http://${ENV_HOSTNAME}\"\n";
        string overrideEnv = "\nENV_HOSTNAME=dev.my-company.com\nENV_MY_URL=\"http://${ENV_HOSTNAME}\"\n";
        Values("TestMultipleFiles", FromFiles(null, baseEnv, overrideEnv),
               new() { ["ENV_HOSTNAME"] = "dev.my-company.com", ["ENV_MY_URL"] = "http://dev.my-company.com" }, exactSize: true);
        Values("TestMultipleFiles with an OS environment", FromFiles(new() { ["ENV_HOSTNAME"] = "host.local" }, baseEnv, overrideEnv),
               new() { ["ENV_HOSTNAME"] = "dev.my-company.com", ["ENV_MY_URL"] = "http://host.local" }, exactSize: true);
    }

    // GetEnvFromFile's loop (env.go), files given as text.
    private static Dictionary<string, string> FromFiles(Dictionary<string, string> currentEnv, params string[] files)
    {
        var envMap = new Dictionary<string, string>();
        foreach (string file in files)
        {
            DotenvParser.ParseWithLookup(file, envMap, (string k, out string v) =>
            {
                if (currentEnv != null && currentEnv.TryGetValue(k, out v)) return true;
                return envMap.TryGetValue(k, out v);
            });
        }
        return envMap;
    }

    // ---- godotenv_var_expansion_test.go ----

    private static void VarExpansion()
    {
        var envMap = new Dictionary<string, string> { ["TEST_VAR"] = "Test Value", ["EMPTY_VAR"] = "" };
        LookupFn notFoundLookup = (string k, out string v) => { v = ""; return false; };

        void Expect(string name, string input, string result, string variable = null, string reason = null)
        {
            string got = DotenvParser.ExpandVariables(input, envMap, notFoundLookup, out TemplateException err);
            bool ok = got == result;
            if (variable == null) ok &= err == null;
            else ok &= err is MissingRequiredException m && m.Variable == variable && m.Reason == reason;
            Check(name, ok, "got " + Escape(got) + " " + (err?.Message ?? "no error"));
        }

        // TestExpandIfEmptyOrUnset
        Expect("Expand if empty or unset: UNSET_VAR", "RESULT=${UNSET_VAR:-Default Value}", "RESULT=Default Value");
        Expect("Expand if empty or unset: EMPTY_VAR", "RESULT=${EMPTY_VAR:-Default Value}", "RESULT=Default Value");
        Expect("Expand if empty or unset: TEST_VAR", "RESULT=${TEST_VAR:-Default Value}", "RESULT=Test Value");
        // TestExpandIfUnset
        Expect("Expand if unset: UNSET_VAR", "RESULT=${UNSET_VAR-Default Value}", "RESULT=Default Value");
        Expect("Expand if unset: EMPTY_VAR", "RESULT=${EMPTY_VAR-Default Value}", "RESULT=");
        Expect("Expand if unset: TEST_VAR", "RESULT=${TEST_VAR-Default Value}", "RESULT=Test Value");
        // TestErrorIfEmptyOrUnset
        Expect("Error empty or unset: UNSET_VAR", "RESULT=${UNSET_VAR:?Test error}", "RESULT=${UNSET_VAR:?Test error}", "UNSET_VAR", "Test error");
        Expect("Error empty or unset: EMPTY_VAR", "RESULT=${EMPTY_VAR:?Test error}", "RESULT=${EMPTY_VAR:?Test error}", "EMPTY_VAR", "Test error");
        Expect("Error empty or unset: TEST_VAR", "RESULT=${TEST_VAR:?Default Value}", "RESULT=Test Value");
        // TestErrorIfUnset
        Expect("Error on unset: UNSET_VAR", "RESULT=${UNSET_VAR?Test error}", "RESULT=${UNSET_VAR?Test error}", "UNSET_VAR", "Test error");
        Expect("Error on unset: EMPTY_VAR", "RESULT=${EMPTY_VAR?Test error}", "RESULT=");
        Expect("Error on unset: TEST_VAR", "RESULT=${TEST_VAR?Default Value}", "RESULT=Test Value");
    }

    // ---- parser_test.go ----

    private static void ParserTests()
    {
        // TestParseBytes
        string testInput = "\na=b\na[1]=c\na.propertyKey=d\nárvíztűrő-TÜKÖRFÚRÓGÉP=ÁRVÍZTŰRŐ-tükörfúrógép\n";
        Values("TestParseBytes", Parse(testInput), new()
        {
            ["a"] = "b", ["a[1]"] = "c", ["a.propertyKey"] = "d", ["árvíztűrő-TÜKÖRFÚRÓGÉP"] = "ÁRVÍZTŰRŐ-tükörfúrógép",
        }, exactSize: true);

        // TestParseVariable
        Exception e = Error(() => Parse("%!(EXTRA string)=foo"));
        Check("TestParseVariable", e?.Message == "line 1: unexpected character \"%\" in variable name \"%!(EXTRA string)=foo\"", e?.Message);

        // TestMemoryExplosion: the size half (Go's allocator is not ours).
        var lines = Enumerable.Range(0, 1000).Select(i => "KEY" + i + "=VALUE" + i);
        Check("TestMemoryExplosion: 1000 keys", Parse(string.Join("\n", lines)).Count == 1000);
    }

    // ---- template_test.go ----

    private static readonly Dictionary<string, string> Defaults = new() { ["FOO"] = "first", ["BAR"] = "", ["JSON"] = """{"json":2}""" };

    private static bool DefaultMapping(string name, out string value) => Defaults.TryGetValue(name, out value);

    private static void Sub(string name, string template, string expected)
    {
        string got = TemplateSubstitution.Substitute(template, DefaultMapping, out TemplateException err);
        Check(name + ": " + template, err == null && got == expected, "got " + Escape(got) + " " + (err?.Message ?? ""));
    }

    private static void SubError(string name, string template, string contains, Type type = null)
    {
        TemplateSubstitution.Substitute(template, DefaultMapping, out TemplateException err);
        Check(name + ": " + template, err != null && err.Message.Contains(contains) && (type == null || err.GetType() == type), err?.Message ?? "no error");
    }

    private static void Template()
    {
        Sub("TestEscaped", "$${foo}", "${foo}");
        Sub("TestSubstituteNoMatch", "foo", "foo");
        foreach (string t in new[] { "a $ string", "^REGEX$", "$}", "$" }) Sub("TestUnescaped", t, t);
        foreach (string t in new[] { "${", "${}", "${ }", "${ foo}", "${foo }", "${foo!}" }) SubError("TestInvalid", t, "Invalid template");
        Sub("TestNonBraced", "$FOO-bar", "first-bar");
        foreach (string t in new[] { "This ${missing} var", "This ${BAR} var" }) Sub("TestNoValueNoDefault", t, "This  var");
        foreach (string t in new[] { "This $FOO var", "This ${FOO} var" }) Sub("TestValueNoDefault", t, "This first var");
        foreach (string t in new[] { "ok ${missing:-def}", "ok ${missing-def}" }) Sub("TestNoValueWithDefault", t, "ok def");
        Sub("TestLazyDefaultDoesNotWarn (value)", "${A:+${A},}B", "B");
        Sub("TestLazyDefaultWarnsWhenUsed (value)", "${A:-${B}}", "");
        {
            string got = TemplateSubstitution.Substitute("${A?${B}}", (string k, out string v) => { v = k == "A" ? "value" : ""; return k == "A"; }, out var err);
            Check("TestLazyRequiredMessageDoesNotWarn (value)", err == null && got == "value", got);
        }
        Sub("TestEmptyValueWithSoftDefault", "ok ${BAR:-def}", "ok def");
        Sub("TestValueWithSoftDefault", "ok ${FOO:-def}", "ok first");
        Sub("TestEmptyValueWithHardDefault", "ok ${BAR-def}", "ok ");
        Sub("TestPresentValueWithUnset", "ok ${UNSET_VAR:+presence_value}", "ok ");
        Sub("TestPresentValueWithUnset2", "ok ${UNSET_VAR+presence_value}", "ok ");
        Sub("TestPresentValueWithNonEmpty", "ok ${FOO:+presence_value}", "ok presence_value");
        Sub("TestPresentValueAndNonEmptyWithNonEmpty", "ok ${FOO+presence_value}", "ok presence_value");
        Sub("TestPresentValueWithSet", "ok ${BAR+presence_value}", "ok presence_value");
        Sub("TestPresentValueAndNotEmptyWithSet", "ok ${BAR:+presence_value}", "ok ");
        Sub("TestNonAlphanumericDefault", "ok ${BAR:-/non:-alphanumeric}", "ok /non:-alphanumeric");

        // TestInterpolationExternalInterference
        foreach (var (t, e) in new[]
        {
            ("-ok ${BAR:-defaultValue}", "-ok defaultValue"), ("+ok ${UNSET:-${BAR-defaultValue}}", "+ok "),
            ("-ok ${FOO:-defaultValue}", "-ok first"), (":-ok ${UNSET-defaultValue}", ":-ok defaultValue"),
            (":-ok ${BAR-defaultValue}", ":-ok "), (":?ok ${BAR-defaultValue}", ":?ok "),
            (":?ok ${BAR:-defaultValue}", ":?ok defaultValue"), (":+ok ${BAR:-defaultValue}", ":+ok defaultValue"),
            ("+ok ${BAR-defaultValue}", "+ok "), ("?ok ${BAR:-defaultValue}", "?ok defaultValue"),
        }) Sub("TestInterpolationExternalInterference", t, e);

        // TestDefaultsWithNestedExpansion
        foreach (var (t, e) in new[]
        {
            ("ok ${UNSET_VAR-$FOO}", "ok first"), ("ok ${UNSET_VAR-${FOO}}", "ok first"),
            ("ok ${UNSET_VAR-${FOO} ${FOO}}", "ok first first"), ("ok ${BAR:-$FOO}", "ok first"),
            ("ok ${BAR:-${FOO}}", "ok first"), ("ok ${BAR:-${FOO} ${FOO}}", "ok first first"),
            ("ok ${BAR+$FOO}", "ok first"), ("ok ${BAR+$FOO ${FOO:+second}}", "ok first second"),
        }) Sub("TestDefaultsWithNestedExpansion", t, e);

        // TestMandatoryVariableErrors, ...WithNestedExpansion
        foreach (var (t, e) in new[]
        {
            ("not ok ${UNSET_VAR:?Mandatory Variable Unset}", "required variable UNSET_VAR is missing a value: Mandatory Variable Unset"),
            ("not ok ${BAR:?Mandatory Variable Empty}", "required variable BAR is missing a value: Mandatory Variable Empty"),
            ("not ok ${UNSET_VAR:?}", "required variable UNSET_VAR is missing a value"),
            ("not ok ${UNSET_VAR?Mandatory Variable Unset}", "required variable UNSET_VAR is missing a value: Mandatory Variable Unset"),
            ("not ok ${UNSET_VAR?}", "required variable UNSET_VAR is missing a value"),
            ("not ok ${UNSET_VAR:?Mandatory Variable ${FOO}}", "required variable UNSET_VAR is missing a value: Mandatory Variable first"),
            ("not ok ${UNSET_VAR?Mandatory Variable ${FOO}}", "required variable UNSET_VAR is missing a value: Mandatory Variable first"),
        }) SubError("TestMandatoryVariableErrors", t, e, typeof(MissingRequiredException));

        // TestDefaultsForMandatoryVariables
        Sub("TestDefaultsForMandatoryVariables", "ok ${FOO:?err}", "ok first");
        Sub("TestDefaultsForMandatoryVariables", "ok ${FOO?err}", "ok first");
        Sub("TestDefaultsForMandatoryVariables", "ok ${BAR?err}", "ok ");

        // TestSubstituteWithCustomFunc
        SubstituteFunc errIsMissing = (string substitution, Mapping mapping, out bool applied, out TemplateException error) =>
        {
            applied = true;
            error = null;
            if (!mapping(substitution, out string value))
            {
                error = new InvalidTemplateException("required variable " + substitution + " is missing a value");
                return "";
            }
            return value;
        };
        {
            string r1 = TemplateSubstitution.SubstituteWith("ok ${FOO}", DefaultMapping, errIsMissing, out var e1);
            string r2 = TemplateSubstitution.SubstituteWith("ok ${BAR}", DefaultMapping, errIsMissing, out var e2);
            TemplateSubstitution.SubstituteWith("ok ${NOTHERE}", DefaultMapping, errIsMissing, out var e3);
            Check("TestSubstituteWithCustomFunc", e1 == null && r1 == "ok first" && e2 == null && r2 == "ok " && e3 != null && e3.Message.Contains("required variable"),
                  r1 + "|" + r2 + "|" + e3?.Message);
        }

        // TestSubstituteWithReplacementFunc
        ReplacementFunc replacement = (string s, Mapping m, Config c, out TemplateException error) =>
        {
            error = null;
            if (s == "${NOTHERE}") { error = new InvalidTemplateException("bad choice: \"" + s + "\""); return ""; }
            string r = TemplateSubstitution.DefaultReplacementFunc(s, m, c, out TemplateException err);
            if (err == null && r != "") return r;
            return "foobar";
        };
        {
            string a = TemplateSubstitution.SubstituteWithReplacement("ok ${FOO}", DefaultMapping, replacement, out var ea);
            string b = TemplateSubstitution.SubstituteWithReplacement("ok ${BAR}", DefaultMapping, replacement, out var eb);
            string c = TemplateSubstitution.SubstituteWithReplacement("ok ${UNSET}", DefaultMapping, replacement, out var ec);
            TemplateSubstitution.SubstituteWithReplacement("ok ${NOTHERE}", DefaultMapping, replacement, out var ed);
            string f = TemplateSubstitution.SubstituteWith("ok ${SUBDOMAIN:-redis}.${FOO:?}", DefaultMapping, null, out var ef);
            Check("TestSubstituteWithReplacementFunc",
                  a == "ok first" && ea == null && b == "ok foobar" && eb == null && c == "ok foobar" && ec == null
                  && ed != null && ed.Message.Contains("bad choice") && f == "ok redis.first" && ef == null,
                  a + "|" + b + "|" + c + "|" + ed?.Message + "|" + f + "|" + ef?.Message);
        }

        // TestSubstituteWithReplacementAppliedFunc
        ReplacementFunc applied = (string s, Mapping m, Config c, out TemplateException error) =>
        {
            error = null;
            if (s == "${NOTHERE}") { error = new InvalidTemplateException("bad choice: \"" + s + "\""); return ""; }
            string r = TemplateSubstitution.DefaultReplacementAppliedFunc(s, m, c, out bool done, out TemplateException err);
            if (err == null && done) return r;
            return "foobar";
        };
        {
            string a = TemplateSubstitution.SubstituteWithReplacement("ok ${FOO}", DefaultMapping, applied, out _);
            string b = TemplateSubstitution.SubstituteWithReplacement("ok ${BAR}", DefaultMapping, applied, out _);
            string c = TemplateSubstitution.SubstituteWithReplacement("ok ${UNSET}", DefaultMapping, applied, out _);
            TemplateSubstitution.SubstituteWithReplacement("ok ${NOTHERE}", DefaultMapping, applied, out var ed);
            Check("TestSubstituteWithReplacementAppliedFunc", a == "ok first" && b == "ok " && c == "ok foobar" && ed != null && ed.Message.Contains("bad choice"),
                  a + "|" + b + "|" + c + "|" + ed?.Message);
        }

        // TestPrecedence
        {
            string r = TemplateSubstitution.Substitute("${UNSET_VAR?bar-baz}", DefaultMapping, out var e);
            Check("TestPrecedence ?bar-baz", r == "" && e is MissingRequiredException m && m.Variable == "UNSET_VAR" && m.Reason == "bar-baz", r + " " + e?.Message);
        }
        Sub("TestPrecedence", "${UNSET_VAR-myerror?msg}", "myerror?msg");
        Sub("TestPrecedence", "${FOO?bar-baz}", "first");
        Sub("TestPrecedence", "${BAR:-default_value_for_empty_var}", "default_value_for_empty_var");
        Sub("TestPrecedence", "${UNSET_VAR-default_value_for_unset_var}", "default_value_for_unset_var");

        // TestSubstitutionFunctionChoice
        foreach (var (input, symbol) in new[]
        {
            ("VARNAME:?val?ue", ":?"), ("VARNAME?val:?ue", "?"), ("VARNAME?va-lu+e:?e", "?"), ("VARNAME?va+lu-e:?e", "?"),
            ("VARNAME:-value", ":-"), ("VARNAME-va:-lu:?e", "-"), ("VARNAME-va+lu?e", "-"), ("VARNAME-va?lu+e", "-"),
            ("VARNAME:+va:?lu:-e", ":+"), ("VARNAME+va:+lue", "+"), ("VARNAME+va?lu-e", "+"), ("VARNAME+va-lu?e", "+"),
        })
        {
            string got = TemplateSubstitution.GetSubstitutionFunctionForTemplate(input, out _);
            Check("TestSubstitutionFunctionChoice " + input, got == symbol, got);
        }

        // TestNoValueWithCurlyBracesDefault, TestValueWithCurlyBracesDefault
        foreach (string t in new[] { """ok ${missing:-{"json":1}}""", """ok ${missing-{"json":1}}""" }) Sub("TestNoValueWithCurlyBracesDefault", t, """ok {"json":1}""");
        foreach (string t in new[] { """ok ${JSON:-{"json":1}}""", """ok ${JSON-{"json":1}}""" }) Sub("TestValueWithCurlyBracesDefault", t, """ok {"json":2}""");
    }
}
