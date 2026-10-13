# The battery the machine runs by itself.
#
# Staged to \config\autorun.sh by run_build.ps1 -Autorun, which also makes
# INIT in \config\system.env the shell with this script (step207): the kernel
# starts SHELL.EXE instead of LAUNCHER.EXE, the shell runs these lines and
# exits, and the batch ends the way it always does — by powering the machine
# off. Without the switch, a build boots to the launcher.
#
# Programs are named bare and found along PATH (\bin;\tests;\games); the
# shell starts in HOME (\home), where the tests write their files.
#
# One command per line, '#' for comments. The line worth grepping for is
#
#     [sh] done ran=N failed=M
#
# `expect CODE COMMAND` runs the command and counts it as a failure unless it
# exits with exactly CODE. Needed because "non-zero means broken" is not true
# here: AOTTESTS.EXE returns the number of tests it passed, so 302 is a clean
# run and 0 would be a catastrophe.
#
# The two --pipe-writer-dies runs end a pipe writer with its queue full —
# by a normal exit and by an unhandled exception — before the battery, which
# reads the kernel's verdicts on them (pipe test 2). --pipe-stress repeats
# pipe tests 1-6 under a collector on every load iteration, the heap walked
# around every collection; it caught the GC root bugs fixed in step 189.
#
# --gc-stress N K runs the rest of its line with a collection before every
# N-th allocation in the app and every K-th in the kernel, the heap walked
# around each collection and freed blocks poisoned (step 190). The last two
# lines are that pass with a small N: the battery, and the pipe stress with a
# collection at every allocation on both sides; the `dynamic` tests alone
# (--dynamic, step 193) the same way.
#
# Arguments reach native programs (startup data); the --echo-args line checks
# that they arrive whole — quoted, with spaces, in UTF-8. The last four lines
# call an interface method on an untranslated region object (pipe_plan.md
# "Проверить опытом", 3), each in a run of its own; the digit is how many types
# implement the interface. 1 means the app caught an exception, 2 that the call
# returned: up to three implementations ILC compiles table compares (one: a
# direct call), the last type assumed, and the key is never read. The census
# (NormalHello.dll) already runs earlier, from the kernel.
#
# A line saying exactly `shell` hands the machine to a prompt after the list
# instead of powering off — and that prompt can launch things, because the
# kernel started it. A shell reached from the launcher is one level down
# already.
#
# Processes (step 194): PIPEGEN 5 prints its five records (its output was
# handed to nobody), the pipeline prints 400, and PROCTEST.EXE runs the
# process and pipe tests — exit code = checks passed; under --gc-stress the
# measurements are left out, and every program it starts runs stressed too.
#
# PIPEPERF.EXE (step 195): the lower layer's speed and its checks — no
# allocation per message on any path, 100 000-object graphs, the reverse
# pass's bytes, a refusal that changes nothing, fifty types in one stream,
# and (step201) the Expando: a lookup by hash, edits in place without an
# allocation, 100 stages adding a field, 1 000 adds and removes; exit code =
# checks passed. Its [perf195] and [perf201] lines are the measurements.
#
# External data (step 197): JSONTEST.EXE checks the JSON reader and writer;
# DATATEST.EXE runs READ, WRITE, CONVERT, `>`/`>>`, bytes and text over a pipe,
# Into<T> from an Expando and pipelines from code — exit code = checks passed;
# --quick leaves out the 100 MiB file and the 64 MiB copy, which the full run
# makes. DATATEST --perf prints the [perf197] measurements. Tests 16-17
# (step206): `> file` of objects keeps them as an externalpipe and READ gives
# them back; CBOR there and back.
#
# KQL (step200, streaming since step201): KQL 'query' runs the query over the
# messages on its input (the table Input) as they come. `where Level == 4`
# sends PIPEGEN's own messages on — four LogEntry lines; the summarize line
# prints Level 3 and 4 with n = 20 each; KQL with an unknown column says
# where and exits with 1. PROCTEST test 18 checks the rest: take stops the
# reading, an Expando input, extend writing into Expandos in place, an input
# that ends at once. KQLBABY.EXE checks the engine itself on a table in
# memory — exit code = checks passed.
#
# A path, when one is needed, uses FORWARD slashes. This is bash syntax — the
# parser is a bash parser — and in bash a backslash escapes the next
# character, so \tests\AOTTESTS.EXE arrives as testsAOTTESTS.EXE with the
# separators eaten. The shell converts forward slashes to the kernel's own
# separator on the way to the file system.

expect 0 AOTTESTS.EXE --pipe-writer-dies normal
expect 134 AOTTESTS.EXE --pipe-writer-dies crash
expect 0 AOTTESTS.EXE --pipe-stress 2
expect 303 AOTTESTS.EXE
expect 303 AOTTESTS.EXE
expect 0  BENCHAOT.EXE
expect 3 AOTTESTS.EXE --echo-args 'two words' третий
expect 2 AOTTESTS.EXE --untranslated-interface1
expect 2 AOTTESTS.EXE --untranslated-interface2
expect 2 AOTTESTS.EXE --untranslated-interface3
expect 1 AOTTESTS.EXE --untranslated-interface4
expect 303 AOTTESTS.EXE --gc-stress 16 4
expect 0 AOTTESTS.EXE --gc-stress 1 1 --pipe-stress 1
expect 23 AOTTESTS.EXE --gc-stress 1 1 --dynamic
PIPEGEN 5
PIPEGEN 1000 | PIPEFILT 3 | PIPECNT
PIPEGEN 20 | KQL 'where Level == 4'
PIPEGEN 100 | KQL 'where Level >= 3 | summarize n = count() by Level | sort by Level asc'
expect 2 KQL.EXE 'where Levl > 1'
expect 83 PROCTEST.EXE
expect 78 PROCTEST.EXE --gc-stress 16
expect 19 PIPEPERF.EXE
expect 64 JSONTEST.EXE
expect 73 DATATEST.EXE
expect 70 DATATEST.EXE --gc-stress 16 --quick
expect 10 GVMTEST.EXE
expect 7 KQLTEST.EXE
expect 9 KQLBABY.EXE
