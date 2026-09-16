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
        private const string BootDirectory = "\\apps";

        // Where the tree is rooted right now. Enter on a folder moves it; the
        // ".." row at the top moves it back.
        //
        // A tree that only expands is fine for a small volume and wrong for
        // browsing: everything stays on screen at once, indented further and
        // further, and there is no way to leave the folder you started in.
        private static string s_current = BootDirectory;

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
                AppHost.WriteError("[launcher] Init failed: ");
                AppHost.WriteError(ex.Message);
                AppHost.WriteError("\n");
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

        private static FrameView s_volumeFrame = null!;

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
            s_volumeFrame = volume;

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
                new StatusItem(Key.Backspace, "~Backspace~ Up", GoUp),
                new StatusItem(Key.F5, "~F5~ Refresh", LoadRoot),
                new StatusItem(Key.Esc, "~Esc~ Quit", () => Application.RequestStop()),
                s_status,
            });
        }

        /// <summary>Reads the current folder into the tree.</summary>
        private static void LoadRoot()
        {
            s_tree.ClearObjects();

            var rows = new List<FileNode>();

            // The way out goes first, where a hand reaching for it expects it.
            string? parent = FileTree.ParentOf(s_current);
            if (parent != null)
                rows.Add(new FileNode { Kind = NodeKind.Up, Path = parent, Name = ".." });

            List<FileNode> entries = FileTree.Read(s_current);
            for (int i = 0; i < entries.Count; i++) rows.Add(entries[i]);

            s_tree.AddObjects(rows);
            s_volumeFrame.Title = s_current;

            if (entries.Count == 0)
            {
                // Said out loud rather than shown as an empty box: an
                // unreadable folder and an empty one look identical, and the
                // difference is the whole question when browsing is new.
                SetStatus("Nothing listed in " + s_current + " — empty, or it could not be read.");
            }
            else
            {
                SetStatus(entries.Count.ToString() + " entries in " + s_current);
            }

            ShowSelection();
            s_tree.SetNeedsDisplay();
            s_volumeFrame.SetNeedsDisplay();
        }

        /// <summary>Moves the tree to another folder.</summary>
        private static void Navigate(string directory)
        {
            s_current = directory;
            LoadRoot();
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
                s_name.Text = node.Kind == NodeKind.More ? "(hidden entries)"
                        : node.Kind == NodeKind.Up ? "(up one folder)"
                        : node.Name;
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
                case NodeKind.Up:
                    return "Enter goes back to " + node.Path + ".";

                case NodeKind.Directory:
                    return "Enter opens this folder.\nRight arrow peeks inside without leaving here.";

                case NodeKind.Application:
                    return "Enter runs it. The launcher waits and\nreports the exit code.";

                case NodeKind.More:
                    return "Only " + FileTree.PreviewChildren.ToString()
                        + " entries per folder are listed,\nso one crowded folder cannot bury\nthe rest of the tree.";

                default:
                    return "Not an application - nothing to run.";
            }
        }

        private static string Describe(NodeKind kind)
        {
            switch (kind)
            {
                case NodeKind.Up: return "parent folder";
                case NodeKind.Directory: return "folder";
                case NodeKind.Application: return "application";
                case NodeKind.ManagedApplication: return "managed assembly";
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
                case NodeKind.Up:
                    Navigate(node.Path);
                    return;

                case NodeKind.Directory:
                    // Enter walks INTO the folder. Peeking without moving is
                    // still there on the arrow keys, which the tree handles
                    // itself — two ways to look, and only one of them changes
                    // where you are.
                    Navigate(node.Path);
                    return;

                case NodeKind.Application:
                case NodeKind.ManagedApplication:
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
            // A dialog closing is the library's own business: it knows what
            // it covered and repaints that. Only a CHILD PROCESS drawing over
            // us needs the screen declared stale by hand.
            if (MessageBox.Query("Run", "Start " + node.Name + "?", "Run", "Cancel") != 0)
                return;

            SetStatus("Running " + node.Name + " ...");
            Application.Refresh();

            // Step out of the way first. A child that prints lines would
            // otherwise start writing over the interface, and what the user
            // reads is then half its output and half our frames.
            var driver = Application.Driver as SharpOSDriver;
            driver?.Suspend();

            // Two different things behind one key. A PE is loaded into the
            // address space and jumped to; an assembly is handed to a runtime
            // that already exists. Neither the waiting nor the screen handling
            // differs, so only the call does.
            bool managed = node.Kind == NodeKind.ManagedApplication;

            AppServiceStatus status = managed
                ? AppHost.TryRunManagedApp(node.Path, out int exitCode)
                : AppHost.TryRunApp(node.Path, out exitCode);

            // Take the screen back: wipe what the child left, then declare
            // every cell stale. Nothing on OUR side changed while it ran, so
            // without that the library would repaint nothing.
            driver?.Resume();
            Repaint();

            if (status == AppServiceStatus.Ok)
            {
                SetStatus(node.Name + " exited with " + exitCode.ToString());
                MessageBox.Query("Finished",
                    node.Name + " exited with code " + exitCode.ToString() + ".", "OK");
            }
            else if (status == AppServiceStatus.Unsupported)
            {
                // Named exactly, because both of these are rules rather than
                // failures: the kernel permits one level of nested launching,
                // and it hosts a runtime only if it was built with one.
                if (managed)
                {
                    SetStatus("No hosted runtime.");
                    MessageBox.ErrorQuery("Cannot run",
                        "This kernel published no hosted runtime, so a\n"
                        + "managed assembly has nothing to run on.\n\n"
                        + "A build with CoreCLR left out looks like this.", "OK");
                }
                else
                {
                    SetStatus("Nesting limit reached.");
                    MessageBox.ErrorQuery("Cannot run",
                        "The chain of programs is as deep as the kernel allows.\n"
                        + "Leave one of the programs above this one first.", "OK");
                }
            }
            else
            {
                SetStatus("Could not start " + node.Name);
                MessageBox.ErrorQuery("Cannot run",
                    node.Name + " could not be started (" + status.ToString() + ").", "OK");
            }
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

        /// <summary>Backspace, for the motion people reach for without looking.</summary>
        private static void GoUp()
        {
            string? parent = FileTree.ParentOf(s_current);
            if (parent == null)
            {
                SetStatus("Already at the top of the volume.");
                return;
            }

            Navigate(parent);
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
                + FileTree.PreviewChildren.ToString() + " entries per folder.\n"
                + "Enter opens a folder or runs an application.", "OK");
            Repaint();
        }
    }
}
