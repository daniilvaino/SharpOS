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
Probe("Environment.OSVersion", () => { _ = Environment.OSVersion; });
Probe("Environment.MachineName", () => { _ = Environment.MachineName; });
Probe("Environment.UserName", () => { _ = Environment.UserName; });
Probe("Environment.SystemDirectory", () => { _ = Environment.SystemDirectory; });
Probe("Dns.GetHostName", () => { _ = Dns.GetHostName(); });
// §11 closed in step 90 (FrameChain walker integration). Socket ctor's
// P/Invoke now propagates through InlinedCallFrame skip-through as a
// catchable SEHException.
Probe("Socket ctor (TCP)", () => { using var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp); });
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

// ── LAST: process-creation (hard-panic candidate — after all sets) ───
Sec("6. PROCESS CREATION  (last — likely hard-panic)");
Probe("Process.Start(dummy)", () => { using var p = Process.Start("dummy"); });

Console.WriteLine();
Console.WriteLine($"=== PAL/OS census end: OK={ok}  DEG={deg}  FAIL={bad} ===");
return 42;
