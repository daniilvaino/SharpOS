// A workbench for USB, not a test for one device.
//
// It started as a way to read a security key's 548-byte answer, which no boot
// log is a reasonable place to read. That shape - a menu of single keystrokes
// printing lines - stops working the moment there is more than one subject: the
// bus, a key, whatever comes next. So the subjects are tabs, each owning its own
// views, and the shell here owns nothing but the frame around them.
//
// Terminal.Gui rather than printed lines for the same reason the launcher uses
// it: the interesting question is usually about one device, and a listing of
// every endpoint on every interface buries it. A tree can be collapsed.

using SharpOS.AppSdk;
using System;
using System.Runtime;
using Terminal.Gui;

namespace UsbTest
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
            // Threads first: the library keeps background work on tasks, and a
            // Task with no backend fails rather than running inline.
            TaskBackendInstaller.Install();

            var driver = new SharpOSDriver();
            var mainLoop = new SharpOSMainLoop(driver);

            try
            {
                Application.Init(driver, mainLoop);
            }
            catch (Exception ex)
            {
                AppHost.WriteError("[usbtest] Init failed: ");
                AppHost.WriteError(ex.Message);
                AppHost.WriteError("\n");
                return 1;
            }

            Shell.ApplyTheme(driver);

            Toplevel top = Application.Top;
            top.Add(Shell.BuildMenu());
            top.Add(Shell.BuildTabs());
            top.Add(Shell.BuildStatusBar());

            BusTab.Refresh();

            Application.Run(top);
            Application.Shutdown();

            AppHost.WriteString("[usbtest] exited\n");
            return 0;
        }
    }

    // The frame: menu, tabs, status line. Everything a tab does is the tab's.
    internal static class Shell
    {
        private static StatusItem s_status = null!;
        private static TabView s_tabs = null!;

        public static void ApplyTheme(SharpOSDriver driver) => SharpOSTheme.Apply(driver);

        public static MenuBar BuildMenu()
        {
            return new MenuBar(new MenuBarItem[]
            {
                new MenuBarItem("_Bus", new MenuItem[]
                {
                    new MenuItem("Re_fresh", "Read the bus again", BusTab.Refresh),
                    null,
                    new MenuItem("_Quit", "Leave", () => Application.RequestStop()),
                }),
                new MenuBarItem("_Help", new MenuItem[]
                {
                    new MenuItem("_About", "", ShowAbout),
                }),
            });
        }

        public static View BuildTabs()
        {
            s_tabs = new TabView
            {
                X = 0,
                Y = 1,
                Width = Dim.Fill(),
                Height = Dim.Fill(1),
            };

            // One tab per subject. New subjects - a mass-storage panel, a HID
            // report viewer - are another AddTab and another file, which is the
            // reason for the shape.
            s_tabs.AddTab(new TabView.Tab("Bus", BusTab.Build()), true);
            s_tabs.AddTab(new TabView.Tab("Security key", KeyTab.Build()), false);

            return s_tabs;
        }

        public static StatusBar BuildStatusBar()
        {
            s_status = new StatusItem(Key.Null, "Ready", null);

            return new StatusBar(new StatusItem[]
            {
                new StatusItem(Key.F5, "~F5~ Refresh", BusTab.Refresh),
                new StatusItem(Key.Esc, "~Esc~ Quit", () => Application.RequestStop()),
                s_status,
            });
        }

        public static void SetStatus(string text)
        {
            if (s_status == null) return;
            s_status.Title = text;
            Application.Top?.SetNeedsDisplay();
        }

        /// <summary>
        /// The bus tab telling the rest of the program what it found.
        /// </summary>
        /// <remarks>
        /// Through the shell rather than tab to tab: a tab that reaches into
        /// another one is a tab that cannot be removed, and the whole point of
        /// the tabs is that there will be more of them.
        /// </remarks>
        public static void SetKeyPresence(int fidoInterfaces)
        {
            KeyTab.NoteKeyPresence(fidoInterfaces);

            SetStatus(fidoInterfaces == 0
                ? "No security key on the bus."
                : fidoInterfaces.ToString() + " FIDO interface(s) found.");
        }

        private static void ShowAbout()
        {
            MessageBox.Query("USB test",
                "A workbench for the USB stack.\n\n"
                + "Bus: what the kernel found, and everything it knows\n"
                + "about one node.\n\n"
                + "Security key: CTAPHID against a FIDO authenticator.\n",
                "OK");
        }
    }
}
