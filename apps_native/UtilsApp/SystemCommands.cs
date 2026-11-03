using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SharpOS.AppSdk;
using SharpOS.Std.Pipes;

namespace UtilsApp
{
    /// <summary>What ps sends down a pipe: a process as an object.</summary>
    [Message]
    public sealed class ProcessRow
    {
        public uint Id;
        public uint Parent;
        public uint Group;
        public string Name;
        public bool Ending;
        public bool Foreground;
        public int Threads;
        public long HeapKiB;
        public long Milliseconds;
    }

    internal static class SystemCommands
    {
        // ps: a table on the screen, objects into a pipe.
        public static int Ps(string[] args)
        {
            var options = new Options("ps — the running processes: a table on the screen, objects into a pipe");
            options.Parse(args, "", "");
            if (options.Failed || options.Words.Count > 0) return options.Refuse();

            RunningProcess[] running = Process.Running();
            if (Pipe.OutputIsTerminal)
            {
                Console.WriteLine("   PID   PPID      GROUP FG THR     HEAP      TIME NAME");
                foreach (RunningProcess p in running)
                {
                    var line = new StringBuilder();
                    line.Append(p.Id.ToString().PadLeft(6)).Append(' ');
                    line.Append(p.Parent.ToString().PadLeft(6)).Append(' ');
                    line.Append(p.Group.ToString("x").PadLeft(10)).Append(' ');
                    line.Append(p.Foreground ? " *" : "  ").Append(' ');
                    line.Append(p.Threads.ToString().PadLeft(3)).Append(' ');
                    line.Append(((p.HeapBytes / 1024).ToString() + "K").PadLeft(8)).Append(' ');
                    line.Append(Elapsed(p.Milliseconds).PadLeft(9)).Append(' ');
                    line.Append(p.Name);
                    if (p.Ending) line.Append(" (ending)");
                    Console.WriteLine(line.ToString());
                }
                return 0;
            }

            using PipeWriter<ProcessRow> output = Pipe.Write<ProcessRow>();
            foreach (RunningProcess p in running)
                output.Copy(new ProcessRow
                {
                    Id = p.Id,
                    Parent = p.Parent,
                    Group = p.Group,
                    Name = p.Name,
                    Ending = p.Ending,
                    Foreground = p.Foreground,
                    Threads = p.Threads,
                    HeapKiB = p.HeapBytes / 1024,
                    Milliseconds = p.Milliseconds,
                });
            return 0;
        }

        private static string Elapsed(long ms)
        {
            long seconds = ms / 1000;
            string tenths = "." + (ms % 1000 / 100).ToString();
            if (seconds < 60) return seconds.ToString() + tenths + "s";
            long minutes = seconds / 60;
            return minutes.ToString() + ":" + (seconds % 60).ToString().PadLeft(2, '0');
        }

        // kill ID...: ends with 137. No signals: -9 and -TERM are accepted and
        // mean the same, since a program has no way to answer one.
        public static int Kill(string[] args)
        {
            const string usage = "usage: kill ID... — ends the processes with code 137 (ps lists them)";
            int code = 0, named = 0;
            foreach (string a in args)
            {
                if (a == "--help" || a == "-h" || a == "-?") { Console.WriteLine(usage); return 0; }
                if (a == "-9" || a == "-KILL" || a == "-TERM" || a == "-15") continue;
                named++;
                if (!uint.TryParse(a, out uint id))
                {
                    Console.Error.WriteLine("kill: not a process id: " + a);
                    code = 2;
                    continue;
                }
                if (Process.KillById(id)) continue;
                Console.Error.WriteLine("kill: " + a + ": " + (id == Process.CurrentId ? "this is kill itself" : "no such process"));
                if (code == 0) code = 1;
            }
            if (named == 0)
            {
                Console.Error.WriteLine(usage);
                return 2;
            }
            return code;
        }

        // sleep N[s|m|h|d]...: N may have a fraction.
        public static int Sleep(string[] args)
        {
            const string usage = "usage: sleep N[s|m|h|d]...";
            double seconds = 0;
            foreach (string a in args)
            {
                if (a == "--help" || a == "-h" || a == "-?") { Console.WriteLine(usage); return 0; }
                double scale = 1;
                string number = a;
                if (a.Length > 0)
                {
                    char unit = a[a.Length - 1];
                    scale = unit == 's' ? 1 : unit == 'm' ? 60 : unit == 'h' ? 3600 : unit == 'd' ? 86400 : 0;
                    if (scale != 0) number = a.Substring(0, a.Length - 1);
                    else scale = 1;
                }
                if (!double.TryParse(number, out double n) || n < 0)
                {
                    Console.Error.WriteLine("sleep: not a time: " + a);
                    Console.Error.WriteLine(usage);
                    return 2;
                }
                seconds += n * scale;
            }
            if (args.Length == 0)
            {
                Console.Error.WriteLine(usage);
                return 2;
            }
            // The kernel's own wait: Thread.Sleep needs a thread backend this
            // program does not install. A uint of milliseconds is 49 days a call.
            double left = seconds * 1000;
            while (left > 0)
            {
                uint now = left > uint.MaxValue ? uint.MaxValue : (uint)Math.Ceiling(left);
                AppThreads.Sleep(now);
                left -= now;
            }
            return 0;
        }

        private static readonly string[] Days = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };
        private static readonly string[] Months = { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

        // date [-u] [+FORMAT]: the time is UTC here, so -u changes nothing.
        public static int Date(string[] args)
        {
            const string usage = "usage: date [-u] [+FORMAT] — %Y %m %d %H %M %S %j %a %b %e %y %s %F %T %D %%";
            string format = "%a %b %e %T UTC %Y";
            foreach (string a in args)
            {
                if (a == "--help" || a == "-h" || a == "-?") { Console.WriteLine(usage); return 0; }
                if (a == "-u") continue;
                if (a.Length > 0 && a[0] == '+') { format = a.Substring(1); continue; }
                Console.Error.WriteLine(usage);
                return 2;
            }
            if (!DateTime.Clock.IsRealClock)
            {
                Console.Error.WriteLine("date: the machine's clock is not known");
                return 1;
            }
            using TextWriter output = Pipe.WriteText();
            output.WriteLine(Format(format, DateTime.UtcNow));
            return 0;
        }

        private static string Two(int n) => n.ToString().PadLeft(2, '0');

        private static string Format(string format, DateTime t)
        {
            var text = new StringBuilder();
            for (int i = 0; i < format.Length; i++)
            {
                char c = format[i];
                if (c != '%' || i + 1 == format.Length)
                {
                    text.Append(c);
                    continue;
                }
                char f = format[++i];
                switch (f)
                {
                    case 'Y': text.Append(t.Year.ToString()); break;
                    case 'y': text.Append(Two(t.Year % 100)); break;
                    case 'm': text.Append(Two(t.Month)); break;
                    case 'd': text.Append(Two(t.Day)); break;
                    case 'e': text.Append(t.Day.ToString().PadLeft(2)); break;
                    case 'H': text.Append(Two(t.Hour)); break;
                    case 'M': text.Append(Two(t.Minute)); break;
                    case 'S': text.Append(Two(t.Second)); break;
                    case 'j': text.Append(t.DayOfYear.ToString().PadLeft(3, '0')); break;
                    case 'a': text.Append(Days[(int)t.DayOfWeek]); break;
                    case 'b': text.Append(Months[t.Month - 1]); break;
                    case 's': text.Append(((t.Ticks - new DateTime(1970, 1, 1).Ticks) / TimeSpan.TicksPerSecond).ToString()); break;
                    case 'F': text.Append(Format("%Y-%m-%d", t)); break;
                    case 'T': text.Append(Format("%H:%M:%S", t)); break;
                    case 'D': text.Append(Format("%m/%d/%y", t)); break;
                    case '%': text.Append('%'); break;
                    default: text.Append('%').Append(f); break;
                }
            }
            return text.ToString();
        }
    }
}
