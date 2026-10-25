// SYSINFO (step211): what this system is and what came up, from the kernel's
// SystemInfo. Into a pipe it goes as the typed record (`sysinfo | kql
// "project Cpu.Avx, Build.Tests"`); on the screen it is printed by section.

using System;
using System.Runtime;
using SharpOS.AppSdk;
using SharpOS.Std.Pipes;
using SharpOS.Std.SystemInformation;

namespace SysInfoApp
{
    internal static unsafe class AppEntry
    {
        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            return Run();
        }

        [RuntimeExport("SharpAppBootstrap")]
        private static int SharpAppBootstrap(ulong startupPointer)
        {
            RuntimeImports.ManagedStartup();
            return SharpAppEntry(startupPointer);
        }

        private static int Main() => Run();

        private static int Run()
        {
            SystemInfo info;
            try { info = Process.GetSystemInfo(); }
            catch (InvalidOperationException e)
            {
                Console.Error.WriteLine("sysinfo: " + e.Message);
                return 1;
            }

            using PipeWriter<SystemInfo> output = Pipe.Write<SystemInfo>();
            if (!output.IsScreen)
            {
                output.Copy(info);
                return 0;
            }

            Print(info);
            return 0;
        }

        private static void Print(SystemInfo s)
        {
            Section("build");
            Line("id", s.Build.Id);
            Line("fork", s.Build.Fork);
            Line("tests", YesNo(s.Build.Tests));
            Line("hosted runtime", YesNo(s.Build.HostedRuntime));

            Section("firmware");
            Line("vendor", s.Firmware.Vendor + " rev " + s.Firmware.Revision.ToString());
            Line("boot medium", s.Firmware.BootMedium);

            Section("cpu");
            Line("vendor", s.Cpu.Vendor);
            Line("processors", s.Cpu.Processors.ToString());
            Line("sse4.1 / avx / fma", YesNo(s.Cpu.Sse41) + " / " + YesNo(s.Cpu.Avx) + " / " + YesNo(s.Cpu.Fma));

            Section("memory");
            Line("total", s.Memory.TotalMiB.ToString() + " MiB");
            Line("free", s.Memory.FreeMiB.ToString() + " MiB");

            Section("storage");
            if (s.Storage.Length == 0) Line("volume", "none");
            foreach (StorageFacts d in s.Storage)
                Line(d.Kind, d.Mounted ? "mounted, " + d.FileSystem : "not mounted");

            Section("input");
            foreach (InputFacts i in s.Input)
                Line(i.Kind, i.Present ? "present" : "absent");

            Section("display");
            Line("framebuffer", s.Display.Present
                ? s.Display.Width.ToString() + "x" + s.Display.Height.ToString() + ", stride " + s.Display.Stride.ToString()
                  + (s.Display.WriteCombining ? ", write-combining" : "")
                : "none");

            Section("timers");
            Line("hpet", s.Timers.HpetHz == 0 ? "none" : (s.Timers.HpetHz / 1000000).ToString() + " MHz");
            Line("apic timer", YesNo(s.Timers.ApicTimer));

            Section("bus");
            Line("pci devices", s.Bus.PciDevices.ToString());
            Line("xhci controllers", s.Bus.XhciControllers.ToString());
            Line("usb devices", s.Bus.UsbDevices.ToString());
            Line("msi sources", s.Bus.MsiSources.ToString());

            Section("power");
            Line("acpi power off", YesNo(s.Power.PowerOff));
        }

        private static string YesNo(bool value) => value ? "yes" : "no";

        private static void Section(string name)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(name);
            Console.ResetColor();
        }

        private static void Line(string name, string value)
        {
            Console.Write("  ");
            Console.Write(name);
            Console.Write(new string(' ', Math.Max(1, 20 - name.Length)));
            Console.WriteLine(value ?? "");
        }
    }
}
