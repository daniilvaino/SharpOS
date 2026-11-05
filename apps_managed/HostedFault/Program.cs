// How a hosted program dies, one way an assembly (its name picks it):
//   HostedFailFast  Environment.FailFast
//   HostedThrow     an exception nobody catches, on the main thread
//   HostedExit      Environment.Exit(3)
// Each is the end of the process on Windows — and the hosted runtime is
// linked into the kernel, initialized once: what the system does then is the
// question.

using System;

string name = typeof(Program).Assembly.GetName().Name ?? "";
Console.WriteLine("[hostedfault] " + name + " start");

switch (name)
{
    case "HostedFailFast":
        Environment.FailFast("hosted failfast probe");
        break;
    case "HostedThrow":
        throw new InvalidOperationException("hosted unhandled probe");
    case "HostedExit":
        Environment.Exit(3);
        break;
}

Console.WriteLine("[hostedfault] " + name + " still running");
return 1;
