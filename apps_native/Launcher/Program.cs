// The launcher, as an interface rather than a print loop.
//
// The old one (HelloSharpFs) draws its menu by writing lines and reading keys —
// three hundred lines of cursor arithmetic for a list. This is the same job
// written against a library: a tree of the boot volume on the left, what is
// known about the selection on the right, a menu, and a status line.
//
// It is also the first real consumer of the Terminal.Gui port, which is rather
// the point. A demo exercises what its author thought to exercise; a program
// somebody uses finds the rest.
//
// One limit worth knowing while reading: the kernel permits ONE level of nested
// launching. Started from the old launcher, this one is already at that level,
// so its own launches come back "unsupported" — reported plainly rather than
// looking like nothing happened. Replacing the old launcher removes the nesting
// and the limit stops mattering.

using SharpOS.AppSdk;
using System;
using System.Collections.Generic;
using System.Runtime;
using Terminal.Gui;
using Terminal.Gui.Trees;

namespace Launcher
{
    internal static unsafe class AppEntry
    {
        private const string BootDirectory = "\\EFI\\BOOT";

        private static TreeView<FileNode> s_tree = null!;
        private static Label s_name = null!;
        private static Label s_kind = null!;
        private static Label s_path = null!;
        private static Label s_note = null!;
        private static StatusItem s_status = null!;

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
            SharpOS.AppSdk.TaskBackendInstaller.Install();

            var driver = new SharpOSDriver();
            var mainLoop = new SharpOSMainLoop(driver);

            try
            {
                Application.Init(driver, mainLoop);
            }
            catch (Exception ex)
            {
                AppHost.WriteString("[launcher] Init failed: ");
                AppHost.WriteString(ex.Message);
                AppHost.WriteString("\n");
                return 1;
            }

            ApplyTheme(driver);

            Toplevel top = Application.Top;
            top.Add(BuildMenu());
            top.Add(BuildBody());
            top.Add(BuildStatusBar());

            LoadRoot();

            Application.Run(top);
            Application.Shutdown();

            AppHost.WriteString("[launcher] exited\n");
            return 0;
        }

        // The theme lives here, not in the driver: the driver's job is to report
        // the library's colours faithfully, and a look imposed down there could
        // never be overridden from up here.
        private static void ApplyTheme(SharpOSDriver driver)
        {
            Colors.Base.Normal = driver.MakeColor(Color.Gray, Color.Black);
            Colors.Base.Focus = driver.MakeColor(Color.Black, Color.Gray);
            Colors.Base.HotNormal = driver.MakeColor(Color.BrightCyan, Color.Black);
            Colors.Base.HotFocus = driver.MakeColor(Color.BrightBlue, Color.Gray);
            Colors.Base.Disabled = driver.MakeColor(Color.DarkGray, Color.Black);

            Colors.Menu.Normal = driver.MakeColor(Color.Gray, Color.Black);
            Colors.Menu.Focus = driver.MakeColor(Color.Black, Color.Gray);
            Colors.Menu.HotNormal = driver.MakeColor(Color.BrightCyan, Color.Black);
            Colors.Menu.HotFocus = driver.MakeColor(Color.BrightBlue, Color.Gray);
            Colors.Menu.Disabled = driver.MakeColor(Color.DarkGray, Color.Black);

            Colors.Dialog.Normal = driver.MakeColor(Color.Black, Color.Gray);
            Colors.Dialog.Focus = driver.MakeColor(Color.Gray, Color.Black);
            Colors.Dialog.HotNormal = driver.MakeColor(Color.BrightBlue, Color.Gray);
            Colors.Dialog.HotFocus = driver.MakeColor(Color.BrightCyan, Color.Black);
            Colors.Dialog.Disabled = driver.MakeColor(Color.DarkGray, Color.Gray);
        }

        private static MenuBar BuildMenu()
        {
            return new MenuBar(new MenuBarItem[]
            {
                new MenuBarItem("_File", new MenuItem[]
                {
                    new MenuItem("_Run", "Start the selected application", Activate),
                    new MenuItem("Re_fresh", "Read the volume again", LoadRoot),
                    null,
                    new MenuItem("_Quit", "Leave the launcher", () => Application.RequestStop()),
                }),
                new MenuBarItem("_Help", new MenuItem[]
                {
                    new MenuItem("_About", "", ShowAbout),
                }),
            });
        }

        private static View BuildBody()
        {
            var volume = new FrameView("Boot volume")
            {
                X = 0,
                Y = 1,
                Width = Dim.Percent(55),
                Height = Dim.Fill(1),
            };

            // Children are fetched when a folder is opened, not up front: a
            // volume should not be walked in full to draw its first level.
            s_tree = new TreeView<FileNode>(new DelegateTreeBuilder<FileNode>(
                node => FileTree.ChildrenOf(node),
                node => node.IsDirectory))
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
            };

            s_tree.SelectionChanged += (object sender, SelectionChangedEventArgs<FileNode> e) => ShowSelection();
            s_tree.ObjectActivated += (ObjectActivatedEventArgs<FileNode> e) => Activate();

            volume.Add(s_tree);

            var details = new FrameView("Selected")
            {
                X = Pos.Percent(55),
                Y = 1,
                Width = Dim.Fill(),
                Height = Dim.Fill(1),
            };

            s_name = new Label("") { X = 1, Y = 1, Width = Dim.Fill(1) };
            s_kind = new Label("") { X = 1, Y = 3, Width = Dim.Fill(1) };
            s_path = new Label("") { X = 1, Y = 5, Width = Dim.Fill(1) };
            s_note = new Label("") { X = 1, Y = 7, Width = Dim.Fill(1), Height = 5 };

            details.Add(s_name, s_kind, s_path, s_note);

            var body = new View
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
            };
            body.Add(volume, details);
            return body;
        }

        private static StatusBar BuildStatusBar()
        {
            s_status = new StatusItem(Key.Null, "Ready", null);

            return new StatusBar(new StatusItem[]
            {
                new StatusItem(Key.Enter, "~Enter~ Open/Run", Activate),
                new StatusItem(Key.F5, "~F5~ Refresh", LoadRoot),
                new StatusItem(Key.Esc, "~Esc~ Quit", () => Application.RequestStop()),
                s_status,
            });
        }

        private static void LoadRoot()
        {
            s_tree.ClearObjects();

            List<FileNode> roots = FileTree.Read(BootDirectory);
            s_tree.AddObjects(roots);

            // Open the first level straight away: an unopened root is a single
            // line, and a launcher that shows one line has told the user
            // nothing.
            for (int i = 0; i < roots.Count; i++)
                if (roots[i].IsDirectory) s_tree.Expand(roots[i]);

            SetStatus(roots.Count.ToString() + " entries in " + BootDirectory);
            ShowSelection();
            s_tree.SetNeedsDisplay();
        }

        private static void ShowSelection()
        {
            FileNode node = s_tree.SelectedObject;

            if (node == null)
            {
                s_name.Text = "";
                s_kind.Text = "";
                s_path.Text = "";
                s_note.Text = "";
            }
            else
            {
                s_name.Text = node.Kind == NodeKind.More ? "(hidden entries)" : node.Name;
                s_kind.Text = "Kind:  " + Describe(node.Kind);
                s_path.Text = "Path:  " + node.Path;
                s_note.Text = NoteFor(node);
            }

            s_name.SetNeedsDisplay();
            s_kind.SetNeedsDisplay();
            s_path.SetNeedsDisplay();
            s_note.SetNeedsDisplay();
        }

        private static string NoteFor(FileNode node)
        {
            switch (node.Kind)
            {
                case NodeKind.Directory:
                    return "Enter opens and closes this folder.";

                case NodeKind.Application:
                    return "Enter runs it. The launcher waits and\nreports the exit code.";

                case NodeKind.More:
                    return "Only " + FileTree.MaxChildrenShown.ToString()
                        + " entries per folder are listed,\nso one crowded folder cannot bury\nthe rest of the tree.";

                default:
                    return "Not an application - nothing to run.";
            }
        }

        private static string Describe(NodeKind kind)
        {
            switch (kind)
            {
                case NodeKind.Directory: return "folder";
                case NodeKind.Application: return "application";
                case NodeKind.More: return "hidden entries";
                default: return "file";
            }
        }

        /// <summary>Enter: open a folder, run an application.</summary>
        private static void Activate()
        {
            FileNode node = s_tree.SelectedObject;
            if (node == null) return;

            switch (node.Kind)
            {
                case NodeKind.Directory:
                    if (s_tree.IsExpanded(node)) s_tree.Collapse(node);
                    else s_tree.Expand(node);
                    s_tree.SetNeedsDisplay();
                    return;

                case NodeKind.Application:
                    Launch(node);
                    return;

                case NodeKind.More:
                    SetStatus("That folder holds " + node.Hidden.ToString() + " entries the tree does not list.");
                    return;

                default:
                    SetStatus("Not an application.");
                    return;
            }
        }

        private static void Launch(FileNode node)
        {
            if (MessageBox.Query("Run", "Start " + node.Name + "?", "Run", "Cancel") != 0)
            {
                Repaint();
                return;
            }

            SetStatus("Running " + node.Name + " ...");
            Application.Refresh();

            AppServiceStatus status = AppHost.TryRunApp(node.Path, out int exitCode);

            // The child owned the screen while it ran and left whatever it drew
            // behind. Nothing on OUR side changed, so the library would repaint
            // nothing — every cell has to be declared stale by hand.
            Repaint();

            if (status == AppServiceStatus.Ok)
            {
                SetStatus(node.Name + " exited with " + exitCode.ToString());
                MessageBox.Query("Finished",
                    node.Name + " exited with code " + exitCode.ToString() + ".", "OK");
            }
            else if (status == AppServiceStatus.Unsupported)
            {
                // Named exactly, because this is a rule rather than a failure:
                // the kernel permits one level of nested launching, and a
                // launcher started BY a launcher is already at it.
                SetStatus("Nested launch refused.");
                MessageBox.ErrorQuery("Cannot run",
                    "The kernel allows one level of nested launching, and\n"
                    + "this launcher was itself started by another one.\n\n"
                    + "Replacing the old launcher removes the nesting.", "OK");
            }
            else
            {
                SetStatus("Could not start " + node.Name);
                MessageBox.ErrorQuery("Cannot run",
                    node.Name + " could not be started (" + status.ToString() + ").", "OK");
            }

            Repaint();
        }

        /// <summary>
        /// Marks the whole screen stale. Needed after something else has drawn
        /// over us: the driver repaints only the cells the library changed, so a
        /// screen dirtied from outside would otherwise stay as the intruder left
        /// it.
        /// </summary>
        private static void Repaint()
        {
            Application.Driver.UpdateOffScreen();
            Application.Top.SetNeedsDisplay();
            Application.Refresh();
        }

        private static void SetStatus(string text)
        {
            s_status.Title = text;
            Application.Top.SetNeedsDisplay();
        }

        private static void ShowAbout()
        {
            MessageBox.Query("SharpOS launcher",
                "Terminal.Gui running on SharpOS.\n\n"
                + "A tree of the boot volume, "
                + FileTree.MaxChildrenShown.ToString() + " entries per folder.\n"
                + "Enter opens a folder or runs an application.", "OK");
            Repaint();
        }
    }
}
