# The battery the machine runs by itself.
#
# Staged to \apps\AUTORUN.SH by run_build.ps1 -Autorun. Its presence there is
# the whole switch: the kernel starts SHELL.EXE instead of LAUNCHER.EXE, the
# shell runs these lines and exits, and the batch ends the way it always does
# — by powering the machine off. Without the switch, a build boots to the
# launcher exactly as before.
#
# One command per line, '#' for comments. The line worth grepping for is
#
#     [sh] done ran=N failed=M
#
# `expect CODE COMMAND` runs the command and counts it as a failure unless it
# exits with exactly CODE. Needed because "non-zero means broken" is not true
# here: AOTTESTS.EXE returns the number of tests it passed, so 129 is a clean
# run and 0 would be a catastrophe.
#
# Arguments reach native programs (startup data); the --echo-args line checks
# that they arrive whole — quoted, with spaces, in UTF-8. The last two lines
# call an interface method on an untranslated region object (pipe_plan.md
# "Проверить опытом", 3), each in a run of its own: 1 means the app caught an
# exception, 2 that the call returned — which is what ILC's compare-only code
# for an interface with two implementations does. The census
# (NormalHello.dll) already runs earlier, from the kernel.
#
# A line saying exactly `shell` hands the machine to a prompt after the list
# instead of powering off — and that prompt can launch things, because the
# kernel started it. A shell reached from the launcher is one level down
# already.
#
# Paths use FORWARD slashes. This is bash syntax — the parser is a bash parser
# — and in bash a backslash escapes the next character, so \apps\AOTTESTS.EXE
# arrives as appsAOTTESTS.EXE with the separators eaten. The shell converts
# forward slashes to the kernel's own separator on the way to the file system.
# Single quotes would also work ('\apps\AOTTESTS.EXE'), and read worse.

expect 129 /apps/AOTTESTS.EXE
expect 129 /apps/AOTTESTS.EXE
expect 0  /apps/BENCHAOT.EXE
expect 3 /apps/AOTTESTS.EXE --echo-args 'two words' третий
expect 1 /apps/AOTTESTS.EXE --untranslated-interface
expect 2 /apps/AOTTESTS.EXE --untranslated-devirtualized
