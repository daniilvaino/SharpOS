using System;

namespace XtermSharp {
	/// <summary>
	/// Where the engine's diagnostics go. The engine used to call Console.WriteLine
	/// directly, which binds it to one particular console — the kernel has several (HAL
	/// console, framebuffer, UART) and picks per boot, and the app tier has its own.
	/// The host installs a writer if it wants the messages; by default they are dropped.
	/// </summary>
	/// <remarks>
	/// Deliberately a plain static field with no initializer: a static field that needs
	/// initializing would need a class constructor, and those do not run on the kernel tier
	/// (see docs/nativeaot-nostd-kernel-limits.md §1).
	/// </remarks>
	public static class TerminalLog {
		public static Action<string> Writer;

		public static void Write (string message)
		{
			var writer = Writer;
			if (writer != null)
				writer (message);
		}
	}
}
