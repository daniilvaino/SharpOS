// Split out of Terminal.cs: this helper exists to seed a child process's environment,
// which only a hosted (desktop) build does. It is the one place in the engine that needs
// System.Environment, so the kernel-tier build simply leaves this file out.
using System;
using System.Collections.Generic;

namespace XtermSharp {
	public partial class Terminal {
		/// <summary>
		/// Provides a baseline set of environment variables that would be useful to run the terminal,
		/// you can customzie these accordingly.
		/// </summary>
		public static string [] GetEnvironmentVariables (string termName = null)
		{
			var l = new List<string> ();
			if (termName == null)
				termName = "xterm-256color";

			l.Add ("TERM=" + termName);

			// Without this, tools like "vi" produce sequences that are not UTF-8 friendly
			l.Add ("LANG=en_US.UTF-8");
			var env = Environment.GetEnvironmentVariables ();
			foreach (var x in new [] { "LOGNAME", "USER", "DISPLAY", "LC_TYPE", "USER", "HOME", "PATH" })
				if (env.Contains (x))
					l.Add ($"{x}={env [x]}");
			return l.ToArray ();
		}
	}
}
