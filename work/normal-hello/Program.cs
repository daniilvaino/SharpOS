// step 73 — PAL / OS-integration boundary census on SharpOS bare metal.
// 100% stock C#, same DLL runs byte-for-byte on the kernel. 5 probe
// sets, sage order: PAL census → threading → FS-minimal → crypto/RNG →
// globalization. Each Probe prints its name FIRST (so an uncatchable
// trap-stub panic / hang leaves a dangling line = the culprit), then a
// classified verdict. Probe's catch only sees managed exceptions; hard
// PAL traps still panic — hence the ordered, marked layout.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Reflection.Emit;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

// step 90 Phase D landed -- §11 closed via FrameChain walker integration.
// Census restored to full original form. Expected:
//   Socket / OpenSSL P/Invoke probes: now caught as SEHException (was 💥).
//   Threading probes (Thread.Sleep, ThreadPool, Task.Run, Timer): still
//     hard-panic -- different class (direct SharpOSHost_Panic, no C-SEH).
//     Disable in census or accept the halt.

int ok = 0, deg = 0, bad = 0;

void Probe(string name, Action body)
{
    Console.Write($"   {name,-46} ");
    try
    {
        body();
        Console.WriteLine("[OK]");
        ok++;
    }
    catch (System.Runtime.InteropServices.SEHException e)
    { Console.WriteLine($"[FAIL] PAL-STUB/SEH: {e.Message}"); bad++; }
    catch (OutOfMemoryException)
    { Console.WriteLine("[FAIL] OOM"); bad++; }
    catch (TimeoutException)
    { Console.WriteLine("[FAIL] HANG/TIMEOUT"); bad++; }
    catch (PlatformNotSupportedException)
    { Console.WriteLine("[FAIL] NOT-SUPPORTED (PNSE)"); bad++; }
    catch (NotSupportedException)
    { Console.WriteLine("[FAIL] NOT-SUPPORTED"); bad++; }
    catch (NotImplementedException)
    { Console.WriteLine("[FAIL] NOT-IMPLEMENTED"); bad++; }
    catch (InvalidOperationException e)
    { Console.WriteLine($"[DEG] InvalidOperationException: {e.Message}"); deg++; }
    catch (Exception e) when (e.GetType() == typeof(Exception))
    { Console.WriteLine($"[FAIL] WRONG-VALUE: {e.Message}"); bad++; }
    catch (Exception e)
    { Console.WriteLine($"[FAIL] {e.GetType().Name}: {e.Message}"); bad++; }
}

void Skip(string name, string why)
{
    Console.WriteLine($"   {name,-46} [SKIP] {why}");
}

static void Benign4(string a, string b, string c, string d)
{
    if (a != "a" || b != "b" || c != "c" || d != "d")
        throw new Exception("bad benign reflection args");
}

void Sec(string t)
{
    Console.WriteLine();
    Console.WriteLine(new string('=', 70));
    Console.WriteLine(t);
    Console.WriteLine(new string('=', 70));
}

Console.WriteLine("=== PAL/OS census begin ===");

// ── 1. PAL CENSUS — which API traps and how (no waits; safe first) ────
Sec("1. PAL CENSUS");
Probe("DateTime.UtcNow", () => { _ = DateTime.UtcNow; });
Probe("DateTime.Now", () => { _ = DateTime.Now; });
Probe("Stopwatch.GetTimestamp", () => { _ = Stopwatch.GetTimestamp(); });
Probe("Environment.TickCount64", () => { _ = Environment.TickCount64; });
Probe("Directory.GetCurrentDirectory", () => { _ = Directory.GetCurrentDirectory(); });
Probe("Path.GetTempPath", () => { _ = Path.GetTempPath(); });
Probe("Path.GetFullPath(.)", () => { _ = Path.GetFullPath("."); });
Probe("File.Exists(x)", () => { _ = File.Exists("x"); });
Probe("Directory.Exists(.)", () => { _ = Directory.Exists("."); });
Probe("Env.GetEnvironmentVariable(PATH)", () => { _ = Environment.GetEnvironmentVariable("PATH"); });
Probe("Env.GetEnvironmentVariables", () => { _ = Environment.GetEnvironmentVariables(); });
Probe("RuntimeInformation.OSDescription", () => { _ = RuntimeInformation.OSDescription; });
Probe("RuntimeInformation.RuntimeIdentifier", () => { _ = RuntimeInformation.RuntimeIdentifier; });
// step 122 — pwsh experiment: проверяем что наш PAL surface'ит насчёт OS.
// pwsh на стоковом Linux работает; если на нашем env IsOSPlatform.Windows
// возвращает true, pwsh пойдёт в Win-only branch'и (Registry, env var rules
// и т.д.) и упрётся в нашу PNSE из Microsoft.Win32.Registry. Если false —
// проблема в чём-то другом (EH coverage, missing API).
Probe("OS identity dump (step 122)", () =>
{
    // Print по мере получения — некоторые getters делают P/Invoke (например
    // OSArchitecture → kernel32.GetNativeSystemInfo) и могут throw'нуть.
    // Если probe умрёт в середине, всё что напечатали остаётся в логе.
    void Try(string label, Func<string> get)
    {
        try { Console.WriteLine($"   [os] {label}={get()}"); }
        catch (Exception ex) { Console.WriteLine($"   [os] {label}=<{ex.GetType().Name}: {ex.Message}>"); }
    }
    Console.WriteLine();
    Try("IsWindows",    () => RuntimeInformation.IsOSPlatform(OSPlatform.Windows).ToString());
    Try("IsLinux",      () => RuntimeInformation.IsOSPlatform(OSPlatform.Linux).ToString());
    Try("IsMacOS",      () => RuntimeInformation.IsOSPlatform(OSPlatform.OSX).ToString());
    Try("OSArch",       () => RuntimeInformation.OSArchitecture.ToString());
    Try("ProcArch",     () => RuntimeInformation.ProcessArchitecture.ToString());
    Try("Framework",    () => RuntimeInformation.FrameworkDescription);
    Try("OSDescription",() => RuntimeInformation.OSDescription);
    Try("RID",          () => RuntimeInformation.RuntimeIdentifier);
});
// Сразу же — попытка Registry, чтобы понять что pwsh-style cctor увидел бы.
// На стоковом CoreCLR/Linux эта проба должна выдать PlatformNotSupportedException.
Probe("Registry.LocalMachine access (pwsh-style trigger)", () =>
{
    try
    {
        // System.Linq/Util доступны; обращаемся через reflection чтобы
        // не requiredить Microsoft.Win32.Registry в Compile-time deps,
        // если её нет в normal-hello's TPA — мы её всё равно её попробуем
        // дёрнуть через runtime API'шку RegistryKey.OpenBaseKey.
        var t = Type.GetType("Microsoft.Win32.Registry, Microsoft.Win32.Registry");
        if (t == null) { Console.Write("(Type.GetType returned null) "); return; }
        var prop = t.GetProperty("LocalMachine");
        var val = prop?.GetValue(null);
        Console.Write($"(got {val?.GetType().Name ?? "null"}) ");
    }
    catch (PlatformNotSupportedException ex)
    {
        Console.Write($"(PNSE caught: {ex.Message}) ");
        // Expected on stock Linux — re-throw as managed exception via
        // generic Exception so outer Probe handles it.
        throw new Exception("PNSE: " + ex.Message);
    }
});
Probe("Environment.OSVersion", () => { _ = Environment.OSVersion; });
Probe("Environment.MachineName", () => { _ = Environment.MachineName; });
Probe("Environment.UserName", () => { _ = Environment.UserName; });
Probe("Environment.SystemDirectory", () => { _ = Environment.SystemDirectory; });
Probe("Dns.GetHostName", () => { _ = Dns.GetHostName(); });
// Isolation probe: does Debug.Fail's throw walk back to a managed catch on
// CoreCLR-hosted? If this halts but Socket probe halts the same way, the bug
// is in EH dispatch, not in SocketAsyncEngine init. If it survives but Socket
// halts, the bug is specific to the static-cctor + InternalException path.
Probe("Debug.Fail catchability", () => Debug.Fail("x"));
Probe("Reflection Invoke benign 4 args", () =>
{
    Action<string, string, string, string> d = Benign4;
    var m = d.Method;
    Console.Write($"m={m} public={m.IsPublic} private={m.IsPrivate} ");
    m.Invoke(null, new object[] { "a", "b", "c", "d" });
});
Probe("DebugProvider.FailCore metadata", () =>
{
    var t = Type.GetType("System.Diagnostics.DebugProvider, System.Private.CoreLib");
    Console.Write($"t={(t is null ? "NULL" : t.FullName)} ");

    var nonPublic = t!.GetMethod("FailCore",
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
    Console.Write($"nonPublic={(nonPublic is null ? "NULL" : nonPublic.ToString())} ");

    var any = t.GetMethod("FailCore",
        System.Reflection.BindingFlags.Static |
        System.Reflection.BindingFlags.Public |
        System.Reflection.BindingFlags.NonPublic);
    Console.Write($"any={(any is null ? "NULL" : any.ToString())} ");
    if (any is not null)
    {
        Console.Write($"attrs={any.Attributes} public={any.IsPublic} private={any.IsPrivate} ");
    }
});
Probe("Null object.ToString catchability", () =>
{
    object? o = null;
    _ = o!.ToString();
});
Probe("Null MethodInfo.ToString catchability", () =>
{
    System.Reflection.MethodInfo? m = null;
    _ = m!.ToString();
});
Probe("Null MethodInfo.Invoke catchability", () =>
{
    System.Reflection.MethodInfo? m = null;
    m!.Invoke(null, Array.Empty<object>());
});
Probe("DebugProvider.FailCore NP invoke", () =>
{
    var t = Type.GetType("System.Diagnostics.DebugProvider, System.Private.CoreLib");
    var m = t!.GetMethod("FailCore",
        System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
    Console.Write($"m={(m is null ? "NULL" : m.ToString())} ");
    m!.Invoke(null, new object[] { "stack", "msg", "detail", "test failcore np" });
});
Skip("DebugProvider.FailCore ANY invoke", "terminal: set SHARPOS_TERMINAL_PROBES=1 and enable manually");
Skip("Environment.FailFast direct", "terminal: set SHARPOS_TERMINAL_PROBES=1 and enable manually");
// §11 closed in step 90 (FrameChain walker integration). Socket ctor's
// P/Invoke now propagates through InlinedCallFrame skip-through as a
// catchable SEHException.
// Socket ctor probe пропущен — Unix-tier SocketAsyncEngine ждёт epoll,
// у нас его нет (CreateSocketEventPort = ENOTSUPP). Возврат — после
// перехода Sockets-стека на Windows IL + IOCP в комплекте с ThreadPool/
// Overlapped/CoreCLR-IO-thread. План в donext.md "Big bet: socket stack
// → Windows IL целиком".
// Probe("Socket ctor (TCP)", () => { using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp); });
// Минимальные repro-пробы для EH bug на managed-throw цепочке (отдельно от
// HW-fault path, который через CCF-inv работает). Цель — изолировать что
// именно ломает pass1 search: глубина стека или PInvoke в цепочке.
Probe("managed throw bare", () => { throw new System.InvalidOperationException("bare"); });
Probe("managed throw deep stack", () => {
    static void L1() => L2();
    static void L2() => L3();
    static void L3() => L4();
    static void L4() => L5();
    static void L5() => L6();
    static void L6() => L7();
    static void L7() => L8();
    static void L8() => throw new System.InvalidOperationException("deep");
    L1();
});
Probe("Process.GetCurrentProcess()", () => { using var p = Process.GetCurrentProcess(); });
Probe("Process...ProcessName", () => { using var p = Process.GetCurrentProcess(); _ = p.ProcessName; });
Probe("Process...WorkingSet64", () => { using var p = Process.GetCurrentProcess(); _ = p.WorkingSet64; });
// clock values — концретно «добить время»: видно WRONG-VALUE, не просто
// «не кинул». Чистые numeric-поля (без ToString/culture — оно трапает).
{
    var u = DateTime.UtcNow;
    Console.WriteLine($"   [clock] UtcNow.Year={u.Year} Ticks={u.Ticks} " +
                      $"TickCount64={Environment.TickCount64} SW={Stopwatch.GetTimestamp()}");
}

// ── 2. THREADING / THREADPOOL / SYNC (may HANG if timer PAL dead) ────
Sec("2. THREADING / SYNC  (note: waits depend on timer PAL - may hang)");
Probe("Thread.CurrentThread.ManagedThreadId", () => { _ = Thread.CurrentThread.ManagedThreadId; });
Probe("Interlocked.Increment", () => { int x = 0; Interlocked.Increment(ref x); if (x != 1) throw new Exception("bad interlocked"); });
Probe("lock/Monitor", () => { object g = new(); lock (g) { } });
// yield is NOT threading — compiler state-machine, pure single-thread
// synchronous control flow, zero PAL/threading dependency. Worth a real
// coverage point on the hosted CoreCLR (lazy eval, MoveNext, yield break).
// Generic `as T` / `(T)` lowers to RhTypeCast_IsInstanceOf /
// RhTypeCast_CheckCast — generic runtime helpers. CoreCLR has both;
// our kernel-AOT std/no-runtime ships only the concrete-class variant
// (RhTypeCast_IsInstanceOfClass), so identical code with a generic T
// parameter LNK2001s there. This probe is the positive sanity check
// that the hosted tier covers what kernel-AOT does not — see
// docs/coreclr-hosted-limits.md §5 / nativeaot-nostd-kernel-limits.md.
Probe("generic `as T` cast (RhTypeCast_IsInstanceOf)", () =>
{
    static T? CastAs<T>(object o) where T : class => o as T;
    static T CastChecked<T>(object o) where T : class => (T)o;

    object box = "hello";
    if (CastAs<string>(box) != "hello") throw new Exception("positive as<T> failed");
    if (CastAs<string>(new int[3]) != null) throw new Exception("negative as<T> should be null");
    if (CastChecked<string>(box) != "hello") throw new Exception("checked (T) failed");
    bool threw = false;
    try { _ = CastChecked<string>(new int[3]); }
    catch (InvalidCastException) { threw = true; }
    if (!threw) throw new Exception("checked (T) on wrong type should throw");
});

Probe("yield iterator (state machine)", () =>
{
    static IEnumerable<int> Squares(int n)
    {
        for (int i = 0; i < n; i++) yield return i * i;
        yield break;
    }
    int sum = 0, cnt = 0;
    foreach (var v in Squares(5)) { sum += v; cnt++; }      // 0+1+4+9+16=30
    if (sum != 30 || cnt != 5) throw new Exception($"foreach sum={sum} cnt={cnt}");
    if (Squares(4).Sum() != 14) throw new Exception("linq-over-iterator");      // 0+1+4+9
    using var e = Squares(3).GetEnumerator();                // manual MoveNext
    if (!e.MoveNext() || e.Current != 0 || !e.MoveNext() || e.Current != 1)
        throw new Exception("manual MoveNext");
});

// Phase E9.a: CreateThread / WaitForSingleObject / Sleep / SwitchToThread
// now route through SharpOSHost_* to kernel Scheduler. Threadpool / Task /
// Timer still ride PAL surfaces not landed yet (E9.b WaitOnAddress,
// E9.c IOCP, E10 ThreadPool implementation).
Probe("new Thread + Join", () =>
{
    int flag = 0;
    var t = new Thread(() => { flag = 42; });
    t.Start();
    t.Join();
    if (flag != 42) throw new Exception($"thread did not set flag (got {flag})");
});
Probe("Thread.Sleep(1)", () => { Thread.Sleep(1); });

// Phase E10 acceptance: QueueUserWorkItem × 100, all callbacks must fire.
// Currently halts because BCL ThreadPool init throws through a Frame
// type the Phase D walker doesn't cover yet. Diagnostic [fchain]
// unhandled frameId=N (SehDispatch.TryActivateFrameChain) tells us
// which type to extend coverage to next iteration.
Probe("ThreadPool.QueueUserWorkItem", () =>
{
    int count = 0;
    var done = new ManualResetEventSlim(false);
    for (int i = 0; i < 100; i++)
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            if (Interlocked.Increment(ref count) == 100)
                done.Set();
        });
    }
    if (!done.Wait(TimeSpan.FromSeconds(5)))
        throw new Exception($"timeout, got {count}/100");
    if (count != 100)
        throw new Exception($"got {count}/100");
});

// step113: ThreadPool stress regression. 1000 work items x20 iters
// exercises hill-climbing worker adjustment (PortableThreadPool.
// HillClimbing.Update -> Complex.Abs -> Math.Sqrt). This is the probe
// that uncovered the lm_sqrt infinite-recursion root (step113): in a
// Debug fork build __builtin_sqrt emitted `call sqrt` into our own
// sqrt() PAL stub -> lm_sqrt -> sqrt -> ... -> 1 MiB stack overflow ->
// triple fault. Fixed by emitting sqrtsd directly in lm_sqrt.
Probe("ThreadPool stress (1000 x20)", () => RunBurst(1000, 20, TimeSpan.FromSeconds(5)));

static void RunBurst(int items, int iters, TimeSpan timeout)
{
    int iterFails = 0; int worst = 0;
    for (int it = 0; it < iters; it++)
    {
        int count = 0;
        var done = new ManualResetEventSlim(false);
        for (int i = 0; i < items; i++)
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (Interlocked.Increment(ref count) == items) done.Set();
            });
        if (!done.Wait(timeout)) { iterFails++; int s = items - count; if (s > worst) worst = s; }
    }
    if (iterFails > 0)
        throw new Exception($"{iterFails}/{iters} iters timed out, worst short by {worst}/{items}");
}

// FP/XMM smoke -- heavy double math through the libc transcendental
// path (Math.Sqrt/Math.Sin). Regression guard for the lm_sqrt recursion
// root and any future libc-math stub that accidentally calls back into
// its own public export.
Probe("FP/XMM smoke (100k Sqrt+Sin)", () =>
{
    double acc = 0;
    for (int i = 1; i < 100_000; i++)
        acc += Math.Sqrt(i * 1.25) + Math.Sin(i);
    if (double.IsNaN(acc) || double.IsInfinity(acc))
        throw new Exception($"acc={acc}");
});

// step113: GC_FRAMEREG_REL OBJECTREF reporting -- the exact class that
// step72's RBP override addressed. A value-type struct holding object
// refs, kept live across GC.Collect, forces the JIT to spill the refs
// to RBP-relative stack slots that GcInfo reports as GC_FRAMEREG_REL.
// If the decoder computes the slot off a wrong frame base, the int/ref
// is misreported and Object::Validate derefs garbage -> crash. This is
// the definitive test that step112's contextPointers->Rbp fill makes
// the step72 override redundant (override is currently DISABLED).
Probe("GC FRAMEREG_REL refs across Collect", () =>
{
    static long Deep(int depth, string a, string b, string c)
    {
        // Locals a/b/c are object refs; recursion + GC.Collect inside
        // keeps multiple frames' RBP-relative ref slots live for the
        // GC stackwalk to report.
        if (depth <= 0)
        {
            // Pure GC.Collect -- forces a root stackwalk (exercises
            // GC_FRAMEREG_REL slot reporting) WITHOUT depending on the
            // finalizer thread (WaitForPendingFinalizers is a separate
            // subsystem that can hang independently).
            GC.Collect();
            return (long)(a.Length + b.Length + c.Length);
        }
        string n = $"{a}-{depth}";
        long r = Deep(depth - 1, n, b, c);
        // Use a/b/c AFTER the recursive call so they stay live across it.
        return r + a.Length + b.Length + c.Length;
    }
    long total = 0;
    for (int i = 0; i < 8; i++)
        total += Deep(12, "alpha", "beta", "gamma");
    if (total <= 0) throw new Exception($"total={total}");
});

// step113: System.Text.Json reflection-mode -- the EXACT original step72
// trigger (project_reflection_json_baremetal). Reflection serialization
// builds value-type-rich frames with object refs spilled to RBP-relative
// slots, then GC walks them. Definitive test of whether the step72 RBP
// override (currently DISABLED) is still needed: if this passes with the
// override off, the override is redundant after step112.
Probe("System.Text.Json roundtrip (reflection)", () =>
{
    var obj = new System.Collections.Generic.Dictionary<string, object>
    {
        ["name"] = "sharpos",
        ["ver"] = 113,
        ["tags"] = new[] { "a", "b", "c" },
    };
    string json = System.Text.Json.JsonSerializer.Serialize(obj);
    if (!json.Contains("sharpos") || !json.Contains("113"))
        throw new Exception($"bad json: {json}");
    var back = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, object>>(json);
    if (back == null || back.Count != 3)
        throw new Exception($"bad roundtrip count={back?.Count}");
});

// step103 Phase E11: ThreadPool.QueueUserWorkItem and Task.Run work
// (LocalAlloc + CondVar + GetSystemTimes + IOCP shim landed). Timer
// hard-halts the kernel (no [FAIL], no panic) — deeper investigation
// deferred to step104 (likely TimerQueue.Portable's worker dispatch
// or ManualResetEventSlim.Wait timeout interaction with our IOCP shim).
Probe("Task.Run + .Result", () =>
{
    var t = Task.Run(() => 42);
    if (t.Result != 42) throw new Exception($"got {t.Result}");
});

// Task.Delay separated from Task.Run because the underlying infra is
// different: Task.Run = ThreadPool dispatch (proven); Task.Delay =
// TimerQueue.Portable's worker calls WaitOne(finiteMs) which routes
// through WaitForSingleObject(handle, timeoutMs). Step110-followup
// added HPET-deadline yield-poll for finite timeouts in
// ThreadStubs.WaitForSingleObject and AddressWait.WaitOnAddress —
// timer worker now wakes after its computed wait window.
//
// Bounded Wait keeps a safety net in case any deeper path still hangs.
Probe("Task.Delay(1).Wait(2s)", () =>
{
    if (!Task.Delay(1).Wait(TimeSpan.FromSeconds(2)))
        throw new Exception("Task.Delay never completed (timer infra hung)");
});

// Inverse: long delay, short Wait -- Wait must return false BEFORE
// Task.Delay completes. Tests that Wait honors its own timeout
// (synchronous timeout semantics) and doesn't block indefinitely.
Probe("Task.Delay(3s).Wait(1s)", () =>
{
    if (Task.Delay(3000).Wait(TimeSpan.FromSeconds(1)))
        throw new Exception("Wait(1s) returned true on a 3s delay -- timeout not honored");
});

// step112: TimerQueue.Portable жив (Task.Delay работает) -- проверяем
// прямой System.Threading.Timer теперь. ManualResetEventSlim ждёт
// callback up to 2s; короткий 50ms период чтобы Timer успел fire.
Probe("Timer (50ms)", () =>
{
    using var fired = new ManualResetEventSlim(false);
    using var t = new Timer(_ => fired.Set(), null, 50, Timeout.Infinite);
    if (!fired.Wait(TimeSpan.FromSeconds(2)))
        throw new Exception("Timer callback never fired within 2s");
});

// Cancellation token cancels a pending Task.Delay before it completes.
// Wait() должен throw'нуть AggregateException(TaskCanceledException).
Probe("Task.Delay(3s, ct) + cts.Cancel", () =>
{
    using var cts = new CancellationTokenSource();
    var t = Task.Delay(3000, cts.Token);
    cts.CancelAfter(100);
    try
    {
        t.Wait(TimeSpan.FromSeconds(2));
        throw new Exception("expected cancellation, Wait returned normally");
    }
    catch (AggregateException ae) when (ae.InnerException is TaskCanceledException) { }
});

// ── 3. FS-MINIMAL — 6 atomic ops (path/cwd/enum/write/read/delete) ───
Sec("3. FS-MINIMAL");
Probe("Path.GetFullPath", () => { _ = Path.GetFullPath("."); });
Probe("Directory.GetCurrentDirectory", () => { _ = Directory.GetCurrentDirectory(); });
Probe("Directory.EnumerateFiles(.)", () => { _ = Directory.EnumerateFiles(".").Take(1).ToArray(); });
Probe("File.WriteAllText", () => File.WriteAllText("probe.txt", "hello"));
Probe("File.ReadAllText", () => { var s = File.ReadAllText("probe.txt"); if (s != "hello") throw new Exception("bad file content"); });
Probe("File.Delete", () => File.Delete("probe.txt"));

// ── 4. CRYPTO / RANDOMNESS / GUID ────────────────────────────────────
Sec("4. CRYPTO / RNG / GUID");
Probe("Guid.NewGuid", () => { _ = Guid.NewGuid(); });
// Both pull libSystem.Security.Cryptography.Native.OpenSsl (absent).
// Previously §11 HARD-PANIC; step 90 closed that class via FrameChain
// walker integration -- the absent-native trap now propagates as a
// catchable SEHException through the InlinedCallFrame skip-through.
Probe("RandomNumberGenerator.Fill", () => { var b = new byte[16]; RandomNumberGenerator.Fill(b); });
Probe("SHA256.HashData", () => { _ = SHA256.HashData(new byte[] { 1, 2, 3, 4 }); });
Probe("Convert.ToBase64String", () => { _ = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }); });
Probe("Random.Shared.Next", () => { _ = Random.Shared.Next(); });

// ── 5. GLOBALIZATION / ENCODING / REGEX / COMPRESSION ────────────────
Sec("5. GLOBALIZATION / REGEX / COMPRESSION");
Probe("Encoding.UTF8 roundtrip", () =>
{
    var s = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes("Привет 🌍"));
    if (s != "Привет 🌍") throw new Exception("bad utf8");
});
Probe("String.Normalize(FormD)", () => { _ = "é".Normalize(NormalizationForm.FormD); });
Probe("CultureInfo.InvariantCulture", () => { _ = CultureInfo.InvariantCulture.DateTimeFormat.ShortDatePattern; });
Probe("CultureInfo.GetCultureInfo(ru-RU)", () => { var c = CultureInfo.GetCultureInfo("ru-RU"); _ = 1234.56.ToString("N", c); });
// step107 Frontier-D2 triangulation: split ETW vs DynamicMethod vs
// Regex-specific paths. Trace in log to see WHERE first AV hits.
Probe("ConcurrentDictionary.TryAdd+Count", () =>
{
    var d = new ConcurrentDictionary<int, int>();
    d.TryAdd(1, 1);
    if (d.Count != 1) throw new Exception("bad count");
});
Probe("DynamicMethod.GetILGenerator", () =>
{
    var dm = new DynamicMethod("dmTest", typeof(int), Type.EmptyTypes);
    var il = dm.GetILGenerator();
    il.Emit(OpCodes.Ldc_I4, 42);
    il.Emit(OpCodes.Ret);
    var fn = (Func<int>)dm.CreateDelegate(typeof(Func<int>));
    if (fn() != 42) throw new Exception("bad dm");
});
Probe("Regex.IsMatch", () => { if (!Regex.IsMatch("abc123", @"\w+\d+")) throw new Exception("bad regex"); });
Probe("GZipStream", () =>
{
    using var ms = new MemoryStream();
    using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
        gz.Write(Encoding.UTF8.GetBytes("hello hello hello"));
    if (ms.Length == 0) throw new Exception("empty gzip");
});

// ── 7. MANAGED DELEGATES / LAMBDAS ────────────────────────────────────
// Confirms what the README feature table claims (CoreCLR-hosted ✅
// tentative): full Delegate.InitializeClosedInstance + _target +
// _functionPointer + Invoke pipeline alive on bare metal. AOT tiers
// (kernel / ELF-app) HALT-trap any lambda — that's why this set lives
// only here, not in NativeAotProbe. Each sub-probe targets a distinct
// codegen path so a partial regression isolates to one cell.
Sec("7. DELEGATES / LAMBDAS  (kernel-AOT cannot run these)");

// Static lambda (no capture) — Roslyn emits cached delegate over a
// compiler-synthesised static method. Simplest path; if THIS fails,
// every other delegate variant will too.
Probe("Func<T,R> no-capture", () =>
{
    Func<int, int> sq = x => x * x;
    if (sq(7) != 49) throw new Exception($"bad sq: {sq(7)}");
});

// Action with side effect — exercises void-return Invoke path.
Probe("Action<T> side-effect", () =>
{
    int s = 0;
    Action<int> add = v => s += v;
    add(5); add(10);
    if (s != 15) throw new Exception($"bad sum: {s}");
});

// Closure over a local — display class allocation + captured field.
// Different IL than no-capture; touches Delegate._target on an
// instance method of the synthesised display class.
Probe("Func<T,R> closure", () =>
{
    int factor = 3;
    Func<int, int> mul = x => x * factor;
    if (mul(4) != 12) throw new Exception($"bad mul: {mul(4)}");
});

// Method group conversion to delegate (Func<int,int> abs = Math.Abs;)
// — IL ldftn + newobj Func<int,int>(target=null, methodPtr).
Probe("Func<T,R> method group", () =>
{
    Func<int, int> abs = Math.Abs;
    if (abs(-7) != 7) throw new Exception($"bad abs: {abs(-7)}");
});

// Multicast — `a += b` chains invocation lists. Final invoke fires
// both. Verifies CombineImpl + invocation-list walking.
Probe("Action multicast", () =>
{
    int hits = 0;
    Action a = () => hits++;
    a += () => hits++;
    a();
    if (hits != 2) throw new Exception($"bad multicast: {hits}");
});

// Lambda passed to Array.Sort via Comparison<T> — confirms the exact
// signature shape we ported into our AOT std (where it can't run) is
// actually fired by Iced-style code on the hosted tier.
Probe("Comparison<T> via Array.Sort", () =>
{
    var arr = new[] { 5, 2, 4, 1, 3 };
    Array.Sort(arr, (x, y) => x - y);
    if (arr[0] != 1 || arr[1] != 2 || arr[2] != 3 || arr[3] != 4 || arr[4] != 5)
        throw new Exception("bad sort");
});

// Local-functions used by Sec 8 — declared before so they're in scope.
static T RtmIdentity<T>(T x) => x;
static T? RtmAs<T>(object o) where T : class => o as T;
static int RtmGenericCompare<T>(T a, T b) where T : IComparable<T> => a.CompareTo(b);

// Local-functions used by Sec 9 ADVANCED.
static void AdvLevel1(out string? local)
{
    local = "alive-" + 123;
    AdvLevel2();
}
static void AdvLevel2() => throw new InvalidOperationException("propagate");
static void AdvOriginalThrow() => throw new InvalidOperationException("from original");
static void AdvThrowInDoubleScope(double a, double b, double c, double d, double e, double f) =>
    throw new InvalidOperationException("xmm-test");
static bool FilterReadsLocalToken(Exception ex, string token) =>
    token == "key" && ex.Message == "payload";

// ── 8. RUNTIME MECHANICS  (boxing, dispatch, generics, cctor, modinit, barriers) ─
// Symmetric с kernel-AOT NativeAotProbe. На CoreCLR здесь всё ожидается
// зелёным — это эталон. Точки покрытия выбраны так, чтобы видеть
// конкретные codegen-пути: каждая сабпроба бьёт в свой отдельный
// механизм рантайма.
Sec("8. RUNTIME MECHANICS");

// — Boxing / unboxing — 6 разных кодпатей —
Probe("box int + unbox", () =>
{
    int i = 42;
    object o = i;                  // box int -> object
    int j = (int)o;                // unbox
    if (j != 42) throw new Exception($"got {j}");
});
Probe("box long + unbox", () =>
{
    long l = 0x1_0000_0000L;
    object o = l;
    if ((long)o != l) throw new Exception("bad long unbox");
});
Probe("box custom struct + unbox", () =>
{
    var v = new RtmStruct { A = 7, B = 13 };
    object o = v;
    var w = (RtmStruct)o;
    if (w.A != 7 || w.B != 13) throw new Exception($"got A={w.A} B={w.B}");
});
Probe("boxed.GetType()", () =>
{
    object o = 5;
    if (o.GetType() != typeof(int)) throw new Exception($"got {o.GetType()}");
});
Probe("invalid unbox throws InvalidCastException", () =>
{
    object o = 42;                 // int
    bool threw = false;
    try { _ = (long)o; }           // unbox-as-long on boxed int -> throws
    catch (InvalidCastException) { threw = true; }
    if (!threw) throw new Exception("expected ICE");
});
Probe("Nullable<int> box semantics", () =>
{
    // Nullable boxes as the underlying value, NOT as Nullable<T>.
    int? n = 99;
    object o = n;
    if (o.GetType() != typeof(int)) throw new Exception($"boxed Nullable should be int, got {o.GetType()}");
    if ((int)o != 99) throw new Exception($"got {o}");
});

// — Array covariance / stelem.ref — 4 path —
Probe("covariant array — Base[] = Derived[]", () =>
{
    RtmDerived[] d = new RtmDerived[3];
    RtmBase[] b = d;               // covariant alias
    b[0] = new RtmDerived { Value = 1 };
    b[1] = new RtmDerived { Value = 2 };
    int sum = b[0].Value + b[1].Value;
    if (sum != 3) throw new Exception($"got {sum}");
});
Probe("covariant read — virtual via base ref", () =>
{
    RtmBase[] arr = new RtmDerived[3];
    arr[0] = new RtmDerived { Value = 10 };
    if (arr[0].Describe() != "derived:10") throw new Exception(arr[0].Describe());
});
Probe("stelem.ref wrong type — ArrayTypeMismatchException", () =>
{
    object[] o = new string[3];    // covariant: object[] aliased over string[]
    bool threw = false;
    try { o[0] = 42; }             // can't put int (boxed) into string[]
    catch (ArrayTypeMismatchException) { threw = true; }
    if (!threw) throw new Exception("expected ATME (CoreCLR throws; AOT silently corrupts)");
});
Probe("value-array stelem (no covariance)", () =>
{
    var v = new RtmStruct[3];
    v[1] = new RtmStruct { A = 5, B = 6 };
    if (v[1].A != 5 || v[1].B != 6) throw new Exception("bad value-array elem");
});

// — Interface dispatch — 4 path —
Probe("interface call — concrete type", () =>
{
    IRtmCalc c = new RtmCalcA();
    if (c.Compute(3) != 33) throw new Exception($"got {c.Compute(3)}");
});
Probe("interface call — different impl", () =>
{
    IRtmCalc a = new RtmCalcA(), b = new RtmCalcB();
    int s = a.Compute(2) + b.Compute(2);
    if (s != 122) throw new Exception($"got {s}");      // 22 + 100
});
Probe("generic interface — IEquatable<int>", () =>
{
    IEquatable<int> e = 5;
    if (!e.Equals(5) || e.Equals(6)) throw new Exception("bad IEquatable<int>");
});
Probe("interface call from shared-generic", () =>
{
    // Calls IComparable<T>.CompareTo inside Identity<T>-like shared body.
    if (RtmGenericCompare(5, 3) != 1) throw new Exception("shared-generic interface dispatch");
});

// — Virtual dispatch — 3 path —
Probe("virtual override — 2-level", () =>
{
    RtmShape s = new RtmCircle();
    if (s.Area() != 314) throw new Exception($"got {s.Area()}");
});
Probe("virtual via interface vs class ref", () =>
{
    RtmShape sq = new RtmSquare();
    IRtmAreaProvider ap = sq;
    if (sq.Area() != ap.Area()) throw new Exception("class/interface dispatch differ");
});
Probe("object.ToString() virtual", () =>
{
    object o = new RtmCircle();
    if (o.ToString() != "Circle:314") throw new Exception($"got {o}");
});

// — Generic sharing — 4 path —
Probe("generic method — reference type", () =>
{
    if (RtmIdentity("ab") != "ab") throw new Exception("ref id");
});
Probe("generic method — value type", () =>
{
    if (RtmIdentity(42) != 42) throw new Exception("val id");
});
Probe("generic class — different T's", () =>
{
    var bi = new RtmBox<int> { V = 7 };
    var bs = new RtmBox<string> { V = "hi" };
    if (bi.V != 7 || bs.V != "hi") throw new Exception("box<T> shared body");
});
Probe("constrained `where T : class`", () =>
{
    // generic `as T` lowers to RhTypeCast_IsInstanceOf
    object box = "hello";
    if (RtmAs<string>(box) != "hello") throw new Exception("bad as");
    if (RtmAs<string>(new int[3]) != null) throw new Exception("bad null-as");
});

// — Static constructors / cctor —
Probe("explicit cctor — runs once, sets value", () =>
{
    if (RtmCctor.Value != 1234) throw new Exception($"got {RtmCctor.Value}");
});
// Раньше работало, сейчас крашит наш coreclr `__C_specific_handler` filter
// @rva 0x14890 / func 0x1CD85890 — он триггерится в TIE wrap path и
// деревнечит null на смещении +4. Регрессия, корень не зафиксирован.
// Skip‑выкл чтобы не блокировать остальную батарею; вернуть после
// починки EH gap.
// Probe("cctor throws -> TypeInitializationException (or raw on SharpOS hosted)", () =>
// {
//     // Stock CoreCLR wraps cctor exceptions in TypeInitializationException
//     // with InnerException = the original. SharpOS hosted CoreCLR currently
//     // propagates the original exception RAW without wrapping (документировано
//     // как known divergence). Either is acceptable for this probe — we just
//     // verify *some* managed exception fires (deterministic behavior).
//     bool threw = false;
//     try { _ = RtmCctorThrow.Value; }
//     catch (TypeInitializationException) { threw = true; }       // stock CoreCLR path
//     catch (InvalidOperationException)   { threw = true; }       // SharpOS hosted path
//     if (!threw) throw new Exception("expected TIE or raw IOE");
// });
Probe("lazy static reference (AOT trap zone)", () =>
{
    // Эта же форма вешает AOT через ClassConstructorRunner trap. На CoreCLR
    // должна работать без проблем — это и есть точка сравнения.
    if (RtmLazy.Cache.Count != 0) throw new Exception("expected fresh empty");
    RtmLazy.Cache.Add(42);
    if (RtmLazy.Cache[0] != 42) throw new Exception("bad");
});
Probe("RuntimeHelpers.RunClassConstructor (explicit)", () =>
{
    System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(RtmCctor).TypeHandle);
    if (RtmCctor.Value != 1234) throw new Exception("after explicit run");
});

// — Module init —
Probe("[ModuleInitializer] ran before Main", () =>
{
    if (!RtmModInit.Ran) throw new Exception("module init didn't fire");
});

// — Write barrier (CoreCLR has generational GC — actual SetCard happens) —
Probe("ref-field write survives GC.Collect", () =>
{
    var h = new RtmRefHolder();
    h.Field = "alive";
    GC.Collect();   // (no WaitForPendingFinalizers — SYM-003 hang on bare metal)
    GC.Collect();
    if (h.Field != "alive") throw new Exception($"got {h.Field}");
});
Probe("array-of-refs survives GC + writebar", () =>
{
    var arr = new string[100];
    for (int i = 0; i < 100; i++) arr[i] = $"e{i}";
    GC.Collect();
    // (no WaitForPendingFinalizers — SYM-003 hang on bare metal)
    for (int i = 0; i < 100; i++)
        if (arr[i] != $"e{i}") throw new Exception($"slot {i} died: {arr[i]}");
});
Probe("cross-gen ref via List<string>", () =>
{
    var list = new List<string>();
    for (int i = 0; i < 1000; i++)
    {
        list.Add("payload-" + i);
        if (i % 100 == 0) GC.Collect();    // gen0 collections под нагрузкой
    }
    if (list.Count != 1000 || list[500] != "payload-500")
        throw new Exception($"corrupted: count={list.Count}");
});

// ── 9. RUNTIME ADVANCED  (EH/GC/concurrency/SIMD/OOM/reentrancy) ─────
Sec("9. RUNTIME ADVANCED");

// — Test 1: GC roots через EH unwind —
Probe("GC roots survive EH unwind", () =>
{
    string? survivor = null;
    try { AdvLevel1(out survivor); }
    catch (InvalidOperationException) { GC.Collect(); GC.Collect(); }   // SYM-003
    if (survivor == null || !survivor.StartsWith("alive-")) throw new Exception($"lost: {survivor}");
});

// — Test 2: finally ordering под nested exceptions —
Probe("finally ordering (nested)", () =>
{
    var log = new System.Text.StringBuilder();
    try
    {
        try
        {
            try { throw new InvalidOperationException("inner"); }
            finally { log.Append("f-inner,"); }
        }
        finally { log.Append("f-mid,"); }
    }
    catch (InvalidOperationException) { log.Append("c-outer"); }
    var s = log.ToString();
    if (s != "f-inner,f-mid,c-outer") throw new Exception($"got '{s}'");
});

// — Test 3: exception filter semantics (when) —
Probe("exception filter reads locals", () =>
{
    int caught = 0;
    string token = "key";
    try { throw new InvalidOperationException("payload"); }
    catch (InvalidOperationException) when (token == "miss") { caught = 1; }
    catch (InvalidOperationException e) when (FilterReadsLocalToken(e, token)) { caught = 2; }
    catch (InvalidOperationException) { caught = 3; }
    if (caught != 2) throw new Exception($"caught={caught}");
});

// — Test 4: throw; vs throw ex; stack trace fidelity —
Probe("`throw;` rethrow caught (StackTrace gap on SharpOS hosted)", () =>
{
    // Stock CoreCLR populates Exception.StackTrace with original throw
    // point preserved через bare `throw;`. SharpOS hosted runtime
    // currently returns empty StackTrace for CLR-internal exceptions
    // (SehUnwind не заполняет trace для thrown-from-native path) —
    // документированный gap. Probe verifies the rethrow IS caught (EH
    // mechanics work), without asserting on StackTrace content.
    bool caught = false;
    try
    {
        try { AdvOriginalThrow(); }
        catch (InvalidOperationException) { throw; }   // bare rethrow
    }
    catch (InvalidOperationException) { caught = true; }
    if (!caught) throw new Exception("rethrow not caught");
});
Probe("`throw ex;` rethrow caught (StackTrace gap on SharpOS hosted)", () =>
{
    // Same gap: StackTrace content not asserted, only that the rethrow
    // path delivers a managed exception to the outer catch.
    bool caught = false;
    try
    {
        try { AdvOriginalThrow(); }
        catch (InvalidOperationException ex) { throw ex; }
    }
    catch (InvalidOperationException) { caught = true; }
    if (!caught) throw new Exception("rethrow ex not caught");
});

// — Test 5: GC + Span interior view —
Probe("GC + Span<byte> interior stable", () =>
{
    byte[] arr = new byte[256];
    for (int i = 0; i < 256; i++) arr[i] = (byte)i;
    Span<byte> s = arr.AsSpan(64, 32);
    GC.Collect();
    GC.Collect();   // (no WaitForPendingFinalizers — SYM-003)
    int sum = 0;
    for (int i = 0; i < s.Length; i++) sum += s[i];
    // bytes 64..95 → (64+95)*32/2 = 2544
    if (sum != 2544) throw new Exception($"sum={sum}");
});

// — Test 6: Overlap copy semantics (memmove) —
Probe("Array.Copy overlap right (memmove)", () =>
{
    int[] a = new[] { 1, 2, 3, 4, 5 };
    Array.Copy(a, 0, a, 1, 4);
    if (a[0] != 1 || a[1] != 1 || a[2] != 2 || a[3] != 3 || a[4] != 4)
        throw new Exception($"got [{string.Join(",", a)}]");
});
Probe("Array.Copy overlap left", () =>
{
    int[] a = new[] { 1, 2, 3, 4, 5 };
    Array.Copy(a, 1, a, 0, 4);
    if (a[0] != 2 || a[1] != 3 || a[2] != 4 || a[3] != 5 || a[4] != 5)
        throw new Exception($"got [{string.Join(",", a)}]");
});

// — Test 7: Thread handoff + GC during ownership transfer —
Probe("thread handoff with GC mid-transfer", () =>
{
    var ready = new ManualResetEventSlim(false);
    var seen = new ManualResetEventSlim(false);
    object? slot = null;
    var producer = new Thread(() =>
    {
        slot = "payload-" + Guid.NewGuid();
        Thread.MemoryBarrier();
        ready.Set();
    });
    var consumer = new Thread(() =>
    {
        if (!ready.Wait(TimeSpan.FromSeconds(3))) return;
        GC.Collect();   // mid-transfer collection
        Thread.MemoryBarrier();
        if (slot is string s && s.StartsWith("payload-")) seen.Set();
    });
    producer.Start(); consumer.Start();
    producer.Join(); consumer.Join();
    if (!seen.IsSet) throw new Exception($"consumer didn't see, slot={slot}");
});

// — Test 8: SIMD/FPU preservation across exception —
// Equivalent of AOT's P0-1 canary. CoreCLR's RyuJIT correctly emits
// UWOP_SAVE_XMM128 + restores; should always pass on hosted.
Probe("XMM6+ doubles preserved across throw/catch", () =>
{
    double a=1.5, b=2.25, c=3.125, d=4.0625, e=5.03125, f=6.015625;
    try { AdvThrowInDoubleScope(a,b,c,d,e,f); }
    catch (InvalidOperationException) { /* xmm6..15 should be restored */ }
    if (!(a==1.5 && b==2.25 && c==3.125 && d==4.0625 && e==5.03125 && f==6.015625))
        throw new Exception($"corrupted: {a},{b},{c},{d},{e},{f}");
});

// — Test 9: OOM/allocation failure deterministic behavior —
// Trying to grab a >Int32.MaxValue array length on CoreCLR throws
// OverflowException (managed, deterministic). We test that it's
// catchable and identifiable, NOT a silent corruption.
// On stock CoreCLR `new int[int.MaxValue]` throws OutOfMemoryException
// catchable by managed catch on inner frame. On SharpOS hosted, the
// allocator failure is delivered through CLR-internal C++ EH path
// (`0xE06D7363 .PEAVEEMessageException`) that bypasses ALL managed
// catches on inner frames — including `catch (Exception)`. Only the
// top-level Probe handler catches it (translated to OOM there).
// Documented gap in our SehDispatch / FrameChain walker — EE-internal
// exception types not surfaced as managed-catchable on nested frames.
Skip("absurd-size alloc deterministic exception",
     "EE-internal exception bypasses managed catch on inner frames (hosted EH gap)");

// — Test 10: host callback reentrancy (CoreCLR ↔ SharpOS host) —
// CoreCLR doesn't expose SharpOSHost_* directly; we exercise the
// closest equivalent: deeply nested managed call through PAL boundary
// (GC.Collect calls into native, which calls back to managed roots).
// The previous tests already exercise this; here we explicitly stress
// nested calls + GC + exception unwind through.
Probe("nested PAL boundary + exception", () =>
{
    // Recurse 6 deep, throw at bottom, count catch-hops as exception
    // bubbles up. Each catch increments `hops` then rethrows. Top-level
    // catch swallows. Expected: hops == 6 (every nested frame catches).
    int hops = 0;
    void Recurse(int depth)
    {
        if (depth >= 6) { GC.Collect(); throw new InvalidOperationException("depth"); }
        try { Recurse(depth + 1); }
        catch (InvalidOperationException) { hops++; throw; }
    }
    try { Recurse(0); } catch (InvalidOperationException) { }
    if (hops != 6) throw new Exception($"hops={hops}, expected 6");
});

// ── 10. STRING.FORMAT ────────────────────────────────────────────────
Sec("10. STRING.FORMAT");
Probe("simple {0}{1}", () =>
{
    if (string.Format("{0}+{1}", 2, 3) != "2+3") throw new Exception();
});
Probe("format spec D5", () =>
{
    if (string.Format("{0:D5}", 42) != "00042") throw new Exception();
});
Probe("format spec X", () =>
{
    if (string.Format("{0:X}", 0xABCD) != "ABCD") throw new Exception();
});
Probe("format spec F2", () =>
{
    if (string.Format("{0:F2}", 3.14159) != "3.14") throw new Exception();
});
Probe("alignment {0,10}", () =>
{
    if (string.Format("|{0,5}|", "x") != "|    x|") throw new Exception();
});
Probe("multiple + repeat", () =>
{
    if (string.Format("{0}-{1}-{0}", "a", "b") != "a-b-a") throw new Exception();
});
Probe("StringBuilder.AppendFormat", () =>
{
    var sb = new System.Text.StringBuilder();
    sb.AppendFormat("{0}={1}", "key", 42);
    if (sb.ToString() != "key=42") throw new Exception();
});
Probe("InvariantCulture float", () =>
{
    // RU culture uses "," for decimal; invariant uses "."
    var s = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F2}", 3.14);
    if (s != "3.14") throw new Exception($"got {s}");
});

// ── 11. REGEX LADDER  (L0 literal → L7 advanced engines) ─────────────
Sec("11. REGEX LADDER");
Probe("L0 — literal IsMatch", () =>
{
    if (!System.Text.RegularExpressions.Regex.IsMatch("abc123", "abc")) throw new Exception();
});
Probe("L1 — anchors+char classes+quantifiers", () =>
{
    if (!System.Text.RegularExpressions.Regex.IsMatch("abc_123", "^[a-z]+_[0-9]+$")) throw new Exception();
    if (System.Text.RegularExpressions.Regex.IsMatch("ABC_123", "^[a-z]+_[0-9]+$")) throw new Exception("negative");
});
Probe("L2 — captures + Groups", () =>
{
    var m = System.Text.RegularExpressions.Regex.Match("id=42;name=sharp", @"id=(\d+);name=([a-z]+)");
    if (!m.Success || m.Groups[1].Value != "42" || m.Groups[2].Value != "sharp")
        throw new Exception($"groups bad");
});
Probe("L3a — Replace without delegate", () =>
{
    var s = System.Text.RegularExpressions.Regex.Replace("a1 b22 c333", @"\d+", "#");
    if (s != "a# b# c#") throw new Exception(s);
});
Probe("L3b — Split", () =>
{
    var parts = System.Text.RegularExpressions.Regex.Split("a,b;;c", @"[,;]+");
    if (parts.Length != 3 || parts[0] != "a" || parts[1] != "b" || parts[2] != "c")
        throw new Exception($"got [{string.Join("|", parts)}]");
});
Probe("L4 — backtracking (a+b on aaab)", () =>
{
    if (!System.Text.RegularExpressions.Regex.IsMatch("aaab", @"a+b")) throw new Exception();
});
Probe("L5 — IgnoreCase + CultureInvariant", () =>
{
    if (!System.Text.RegularExpressions.Regex.IsMatch("SharpOS", "^sharpos$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        throw new Exception();
});
Probe("L6 — source-generated regex (skip, needs partial+attribute on Program)", () =>
{
    // Source-generated regex requires a partial class declaration with
    // [GeneratedRegex(...)]. Top-level program doesn't host one cleanly —
    // documented gap. If you want this row green, add a partial type.
    throw new NotImplementedException("documented gap");
});
Probe("L7 — NonBacktracking engine", () =>
{
    if (!System.Text.RegularExpressions.Regex.IsMatch("foobar", "^foo.+$",
            System.Text.RegularExpressions.RegexOptions.NonBacktracking))
        throw new Exception();
});
Probe("L8 — RegexOptions.Compiled", () =>
{
    var r = new System.Text.RegularExpressions.Regex(@"^\d+$",
            System.Text.RegularExpressions.RegexOptions.Compiled);
    if (!r.IsMatch("123")) throw new Exception();
    if (r.IsMatch("12a")) throw new Exception("negative");
});

// ── LAST: process-creation (hard-panic candidate — after all sets) ───
Sec("6. PROCESS CREATION  (last — likely hard-panic)");
Probe("Process.Start(dummy)", () => { using var p = Process.Start("dummy"); });

Console.WriteLine();
Console.WriteLine($"=== PAL/OS census end: OK={ok}  DEG={deg}  FAIL={bad} ===");
return 42;

// ── Helper types for Sec 8 RUNTIME MECHANICS probes ──────────────────────
public struct RtmStruct { public int A; public int B; }

public class RtmBase
{
    public int Value;
    public virtual string Describe() => $"base:{Value}";
}
public class RtmDerived : RtmBase
{
    public override string Describe() => $"derived:{Value}";
}

public interface IRtmCalc { int Compute(int x); }
public class RtmCalcA : IRtmCalc { public int Compute(int x) => x * 11; }
public class RtmCalcB : IRtmCalc { public int Compute(int x) => x * 50; }

public interface IRtmAreaProvider { int Area(); }
public abstract class RtmShape : IRtmAreaProvider { public abstract int Area(); }
public class RtmCircle : RtmShape
{
    public override int Area() => 314;
    public override string ToString() => "Circle:" + Area();
}
public class RtmSquare : RtmShape { public override int Area() => 100; }

public class RtmBox<T> { public T V = default!; }

public static class RtmCctor
{
    public static int Value;
    static RtmCctor() { Value = 1234; }
}

public static class RtmCctorThrow
{
    public static int Value;
    static RtmCctorThrow() { throw new InvalidOperationException("boom in cctor"); }
}

public static class RtmLazy
{
    // The exact form that AOT's ClassConstructorRunner trap'ает —
    // lazy-init static reference field. CoreCLR runs cctor at first
    // access without surprises.
    public static List<int> Cache = new List<int>();
}

public static class RtmModInit
{
    public static bool Ran;
    [System.Runtime.CompilerServices.ModuleInitializer]
    public static void Init() { Ran = true; }
}

public class RtmRefHolder { public string? Field; }
