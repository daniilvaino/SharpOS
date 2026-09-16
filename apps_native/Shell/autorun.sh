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
# here: AOTTESTS.EXE returns the number of tests it passed, so 61 is a clean
# run and 0 would be a catastrophe.
#
# Arguments are not passed to programs yet, so every line is just a path. The
# census (NormalHello.dll) already runs earlier, from the kernel.
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

expect 61 /apps/AOTTESTS.EXE
expect 0  /apps/BENCHAOT.EXE
