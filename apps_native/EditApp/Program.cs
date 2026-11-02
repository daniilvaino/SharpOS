// EDIT [path]: the text editor (release 0.1, section 3).
//
// Ported from Terminal.Gui's UICatalog Scenarios/Editor.cs (MIT, Miguel de
// Icaza and contributors; vendor/Terminal.Gui) from a scenario of the catalog
// into a program of its own. Cuts: the culture menu (one culture here), the
// demo file, and the menus that demonstrate the control rather than edit with
// it (ScrollBarView, Cursor, Responder, ContextMenu). Changes: the file comes
// from the command line; a save is said on the status line, not in a box per
// save; Open and Save As go through FileDialog, whose FileSystemWatcher is cut
// (the listing is read when a folder is entered). Syntax highlighting by the
// file's extension (HighlightedTextView.cs).
//
// Keys are TextView's own: Ctrl+C copy, Ctrl+W cut, Ctrl+Y paste, Ctrl+Z undo,
// Ctrl+R redo; F2 open, F3 save, F4 save as, Ctrl+Q quit (asks to save);
// Alt+F/E/T or F9 the menus.

using SharpOS.AppSdk;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime;
using Terminal.Gui;

namespace EditApp
{
    internal static unsafe class AppEntry
    {
        private static string s_fileName;
        private static Window s_win = null!;
        private static HighlightedTextView s_textView = null!;
        private static StatusItem s_language = null!;
        private static ScrollBarView s_scrollBar = null!;
        private static StatusItem s_note = null!;
        private static StatusBar s_statusBar = null!;
        private static string s_originalText = "";

        private static string s_textToFind;
        private static string s_textToReplace;
        private static bool s_matchCase;
        private static bool s_matchWholeWord;
        private static Window s_winDialog;
        private static TabView s_tabView = null!;

        [RuntimeExport("SharpAppEntry")]
        private static int SharpAppEntry(ulong startupPointer)
        {
            AppRuntime.Initialize((AppStartupBlock*)startupPointer);
            return AppRuntime.Finish(Run(AppHost.Arguments));
        }

        [RuntimeExport("SharpAppBootstrap")]
        private static int SharpAppBootstrap(ulong startupPointer)
        {
            RuntimeImports.ManagedStartup();
            return SharpAppEntry(startupPointer);
        }

        private static int Main() => Run(AppHost.Arguments);

        private static int Run(string[] args)
        {
            if (args.Length > 1 || (args.Length == 1 && (args[0] == "--help" || args[0] == "-h")))
            {
                Console.Error.WriteLine("EDIT [path]: edits a text file; without a path, a new one");
                return args.Length == 1 ? 0 : 2;
            }

            // Threads first: the library keeps background work on tasks.
            TaskBackendInstaller.Install();

            var driver = new SharpOSDriver();
            var mainLoop = new SharpOSMainLoop(driver);
            try
            {
                Application.Init(driver, mainLoop);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("EDIT: the screen could not be set up: " + ex.Message);
                return 1;
            }
            SharpOSTheme.Apply(driver);

            Build();
            if (args.Length == 1) Open(args[0]);
            else New(false);

            Application.Run(Application.Top);
            Application.Shutdown();
            return 0;
        }

        // ---- the window ----

        private static void Build()
        {
            Toplevel top = Application.Top;
            s_win = new Window("Untitled")
            {
                X = 0,
                Y = 1,
                Width = Dim.Fill(),
                Height = Dim.Fill(1),
            };
            top.Add(s_win);

            s_textView = new HighlightedTextView
            {
                X = 0,
                Y = 0,
                Width = Dim.Fill(),
                Height = Dim.Fill(),
                BottomOffset = 1,
                RightOffset = 1,
            };
            // Alt+F is TextView's emacs word-right, and it wins over the menu's
            // _File. The menu keeps it; words go by Ctrl+Right as before.
            s_textView.ClearKeybinding((Key)((int)'F' + Key.AltMask));
            // TextView draws its text in the scheme's Focus and the selection in
            // Focus reversed; the theme's Focus is a highlight (black on brown),
            // which made the whole page one. Text white on black instead.
            s_textView.ColorScheme = new ColorScheme
            {
                Normal = Colors.Base.Normal,
                Focus = Application.Driver.MakeColor(Color.White, Color.Black),
                HotNormal = Colors.Base.HotNormal,
                HotFocus = Colors.Base.HotFocus,
                Disabled = Colors.Base.Disabled,
            };
            s_win.Add(s_textView);

            top.Add(new MenuBar(new MenuBarItem[]
            {
                new MenuBarItem("_File", new MenuItem[]
                {
                    new MenuItem("_New", "", () => New()),
                    new MenuItem("_Open", "", () => OpenWithDialog()),
                    new MenuItem("_Save", "", () => Save()),
                    new MenuItem("Save _As", "", () => SaveAs()),
                    null,
                    new MenuItem("_Quit", "", () => Quit()),
                }),
                new MenuBarItem("_Edit", new MenuItem[]
                {
                    new MenuItem("_Copy", "", () => s_textView.Copy(), null, null, Key.CtrlMask | Key.C),
                    new MenuItem("C_ut", "", () => s_textView.Cut(), null, null, Key.CtrlMask | Key.W),
                    new MenuItem("_Paste", "", () => s_textView.Paste(), null, null, Key.CtrlMask | Key.Y),
                    null,
                    new MenuItem("_Find", "", () => CreateFindReplace(true), null, null, Key.CtrlMask | Key.S),
                    new MenuItem("Find _Next", "", () => ContinueFind(true, false), null, null, Key.CtrlMask | Key.ShiftMask | Key.S),
                    new MenuItem("Find P_revious", "", () => ContinueFind(false, false), null, null, Key.CtrlMask | Key.ShiftMask | Key.AltMask | Key.S),
                    new MenuItem("R_eplace", "", () => CreateFindReplace(false), null, null, Key.CtrlMask | Key.H),
                    new MenuItem("Replace Ne_xt", "", () => ContinueFind(true, true), null, null, Key.CtrlMask | Key.ShiftMask | Key.H),
                    new MenuItem("Replace _All", "", () => ReplaceAll(), null, null, Key.CtrlMask | Key.ShiftMask | Key.AltMask | Key.A),
                    null,
                    new MenuItem("_Select All", "", () => s_textView.SelectAll(), null, null, Key.CtrlMask | Key.T),
                }),
                new MenuBarItem("Forma_t", new MenuItem[]
                {
                    WrapItem(),
                    AutocompleteItem(),
                    Toggle("Allows _Tab", () => s_textView.AllowsTab, v => s_textView.AllowsTab = v),
                    Toggle("Read _Only", () => s_textView.ReadOnly, v => s_textView.ReadOnly = v),
                }),
            }));

            var position = new StatusItem(Key.Null, "Ln 1, Col 1", null);
            s_note = new StatusItem(Key.Null, "", null);
            s_language = new StatusItem(Key.Null, "Text", null);
            s_statusBar = new StatusBar(new StatusItem[]
            {
                position,
                s_language,
                new StatusItem(Key.F2, "~F2~ Open", () => OpenWithDialog()),
                new StatusItem(Key.F3, "~F3~ Save", () => Save()),
                new StatusItem(Key.F4, "~F4~ Save As", () => SaveAs()),
                new StatusItem(Key.CtrlMask | Key.Q, "~^Q~ Quit", () => Quit()),
                s_note,
            });
            s_textView.UnwrappedCursorPosition += e =>
            {
                position.Title = "Ln " + (e.Y + 1).ToString() + ", Col " + (e.X + 1).ToString();
                s_statusBar.SetNeedsDisplay();
            };
            top.Add(s_statusBar);

            s_scrollBar = new ScrollBarView(s_textView, true);
            s_scrollBar.ChangedPosition += () =>
            {
                s_textView.TopRow = s_scrollBar.Position;
                if (s_textView.TopRow != s_scrollBar.Position) s_scrollBar.Position = s_textView.TopRow;
                s_textView.SetNeedsDisplay();
            };
            s_scrollBar.OtherScrollBarView.ChangedPosition += () =>
            {
                s_textView.LeftColumn = s_scrollBar.OtherScrollBarView.Position;
                if (s_textView.LeftColumn != s_scrollBar.OtherScrollBarView.Position)
                    s_scrollBar.OtherScrollBarView.Position = s_textView.LeftColumn;
                s_textView.SetNeedsDisplay();
            };
            s_scrollBar.VisibleChanged += () =>
            {
                if (s_scrollBar.Visible && s_textView.RightOffset == 0) s_textView.RightOffset = 1;
                else if (!s_scrollBar.Visible && s_textView.RightOffset == 1) s_textView.RightOffset = 0;
            };
            s_scrollBar.OtherScrollBarView.VisibleChanged += () =>
            {
                if (s_scrollBar.OtherScrollBarView.Visible && s_textView.BottomOffset == 0) s_textView.BottomOffset = 1;
                else if (!s_scrollBar.OtherScrollBarView.Visible && s_textView.BottomOffset == 1) s_textView.BottomOffset = 0;
            };
            s_textView.DrawContent += _ =>
            {
                s_scrollBar.Size = s_textView.Lines;
                s_scrollBar.Position = s_textView.TopRow;
                if (s_scrollBar.OtherScrollBarView != null)
                {
                    // + 1: the cursor may stand after a line's last character.
                    s_scrollBar.OtherScrollBarView.Size = s_textView.Maxlength + 1;
                    s_scrollBar.OtherScrollBarView.Position = s_textView.LeftColumn;
                }
                s_scrollBar.LayoutSubviews();
                s_scrollBar.Refresh();
            };

            s_win.KeyPress += e =>
            {
                Key keys = ShortcutHelper.GetModifiersKey(e.KeyEvent);
                if (s_winDialog != null && (e.KeyEvent.Key == Key.Esc || e.KeyEvent.Key == (Key.Q | Key.CtrlMask)))
                {
                    DisposeWinDialog();
                    e.Handled = true;
                }
                else if (e.KeyEvent.Key == (Key.Q | Key.CtrlMask))
                {
                    Quit();
                    e.Handled = true;
                }
                else if (s_winDialog != null && keys == (Key.Tab | Key.CtrlMask))
                {
                    if (s_tabView.SelectedTab == s_tabView.Tabs.ElementAt(s_tabView.Tabs.Count - 1))
                        s_tabView.SelectedTab = s_tabView.Tabs.ElementAt(0);
                    else
                        s_tabView.SwitchTabBy(1);
                    e.Handled = true;
                }
            };
        }

        private static MenuItem WrapItem()
        {
            var item = new MenuItem { Title = "_Word Wrap" };
            item.CheckType |= MenuItemCheckStyle.Checked;
            item.Checked = s_textView.WordWrap;
            item.Action += () =>
            {
                s_textView.WordWrap = item.Checked = !item.Checked;
                if (s_textView.WordWrap)
                {
                    s_scrollBar.OtherScrollBarView.ShowScrollIndicator = false;
                    s_textView.BottomOffset = 0;
                }
                else
                {
                    s_textView.BottomOffset = 1;
                }
            };
            return item;
        }

        // Every word of the text as a suggestion, taken when switched on.
        private static MenuItem AutocompleteItem()
        {
            var item = new MenuItem { Title = "Auto_complete" };
            item.CheckType |= MenuItemCheckStyle.Checked;
            item.Action += () =>
            {
                if (item.Checked = !item.Checked)
                    s_textView.Autocomplete.AllSuggestions = Words(s_textView.Text.ToString());
                else
                    s_textView.Autocomplete.AllSuggestions.Clear();
            };
            return item;
        }

        // The upstream sample takes them with Regex \w+; letters, digits and
        // '_' by hand keep Regex out of the program.
        private static List<string> Words(string text)
        {
            var words = new List<string>();
            var seen = new HashSet<string>();
            int start = -1;
            for (int i = 0; i <= text.Length; i++)
            {
                bool word = i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '_');
                if (word && start < 0) start = i;
                else if (!word && start >= 0)
                {
                    string w = text.Substring(start, i - start);
                    if (seen.Add(w)) words.Add(w);
                    start = -1;
                }
            }
            return words;
        }

        private static MenuItem Toggle(string title, Func<bool> get, Action<bool> set)
        {
            var item = new MenuItem { Title = title };
            item.CheckType |= MenuItemCheckStyle.Checked;
            item.Checked = get();
            item.Action += () => set(item.Checked = !item.Checked);
            return item;
        }

        private static void Note(string text)
        {
            s_note.Title = text;
            s_statusBar.SetNeedsDisplay();
        }

        // Highlighting by the file's extension; the status bar names it.
        private static void SetLanguage(string path)
        {
            s_textView.SetLanguage(path);
            s_language.Title = s_textView.Language ?? "Text";
            if (s_textView.Failure != null) Note("no highlighting: " + s_textView.Failure);
            s_statusBar.SetNeedsDisplay();
        }

        // ---- files ----

        private static void New(bool checkChanges = true)
        {
            if (checkChanges && !CanCloseFile()) return;
            s_win.Title = "Untitled";
            s_fileName = null;
            s_originalText = "";
            s_textView.Text = "";
            s_textView.ClearHistoryChanges();
            SetLanguage(null);
        }

        // A path that does not exist yet is a new file of that name: the
        // first save creates it.
        private static void Open(string path)
        {
            string full = Path.GetFullPath(path);
            try
            {
                string text = File.Exists(full) ? File.ReadAllText(full) : "";
                s_textView.Text = text;
                s_originalText = s_textView.Text.ToString();
                s_textView.ClearHistoryChanges();
                s_fileName = full;
                s_win.Title = Path.GetFileName(full);
                Note(File.Exists(full) ? "" : "new file");
                SetLanguage(full);
            }
            catch (Exception ex)
            {
                MessageBox.ErrorQuery("Open", full + ": " + ex.Message, "Ok");
            }
        }

        private static void OpenWithDialog()
        {
            if (!CanCloseFile()) return;
            var types = new List<string> { ".txt;.md;.json;.xml;.cs;.lua;.js;.kql;.sh;.env", ".txt", ".*" };
            var d = new OpenDialog("Open", "The file to edit.", types) { AllowsMultipleSelection = false };
            Application.Run(d);
            if (!d.Canceled && d.FilePaths.Count > 0) Open(d.FilePaths[0]);
        }

        private static bool Save() => s_fileName != null ? SaveFile(s_fileName) : SaveAs();

        private static bool SaveAs()
        {
            var types = new List<string> { ".txt", ".*" };
            var sd = new SaveDialog("Save As", "Where to save the file.", types);
            sd.FilePath = Path.Combine(sd.FilePath.ToString(), s_win.Title.ToString());
            Application.Run(sd);
            if (sd.Canceled) return false;
            string path = sd.FilePath.ToString();
            if (File.Exists(path) && path != s_fileName
                && MessageBox.Query("Save As", "The file exists. Replace it?", "No", "Yes") != 1)
                return false;
            return SaveFile(path);
        }

        private static bool SaveFile(string path)
        {
            try
            {
                File.WriteAllText(path, s_textView.Text.ToString());
                s_fileName = path;
                s_win.Title = Path.GetFileName(path);
                s_originalText = s_textView.Text.ToString();
                s_textView.ClearHistoryChanges();
                Note("saved");
                SetLanguage(path);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.ErrorQuery("Save", path + ": " + ex.Message, "Ok");
                return false;
            }
        }

        private static bool CanCloseFile()
        {
            if (s_textView.Text.ToString() == s_originalText) return true;
            int r = MessageBox.ErrorQuery("Save", "Save the changes to " + s_win.Title + "?", "Yes", "No", "Cancel");
            if (r == 0) return Save();
            return r == 1;
        }

        private static void Quit()
        {
            if (CanCloseFile()) Application.RequestStop();
        }

        // ---- find and replace ----

        private static void ContinueFind(bool next, bool replace)
        {
            if (!replace && string.IsNullOrEmpty(s_textToFind))
            {
                CreateFindReplace(true);
                return;
            }
            if (replace && (string.IsNullOrEmpty(s_textToFind) || (s_winDialog == null && string.IsNullOrEmpty(s_textToReplace))))
            {
                CreateFindReplace(false);
                return;
            }

            bool found;
            bool gaveFullTurn;
            if (next)
                found = replace
                    ? s_textView.FindNextText(s_textToFind, out gaveFullTurn, s_matchCase, s_matchWholeWord, s_textToReplace, true)
                    : s_textView.FindNextText(s_textToFind, out gaveFullTurn, s_matchCase, s_matchWholeWord);
            else
                found = replace
                    ? s_textView.FindPreviousText(s_textToFind, out gaveFullTurn, s_matchCase, s_matchWholeWord, s_textToReplace, true)
                    : s_textView.FindPreviousText(s_textToFind, out gaveFullTurn, s_matchCase, s_matchWholeWord);

            if (!found) Note("not found: " + s_textToFind);
            else if (gaveFullTurn) Note("no more: " + s_textToFind);
            else Note("");
        }

        private static void ReplaceAll()
        {
            if (string.IsNullOrEmpty(s_textToFind) || (string.IsNullOrEmpty(s_textToReplace) && s_winDialog == null))
            {
                CreateFindReplace(false);
                return;
            }
            Note(s_textView.ReplaceAllText(s_textToFind, s_matchCase, s_matchWholeWord, s_textToReplace)
                ? "replaced all: " + s_textToFind
                : "not found: " + s_textToFind);
        }

        private static void DisposeWinDialog()
        {
            s_winDialog.Dispose();
            s_win.Remove(s_winDialog);
            s_winDialog = null;
            s_textView.SetFocus();
        }

        private static void CreateFindReplace(bool isFind)
        {
            if (s_winDialog != null)
            {
                s_winDialog.SetFocus();
                return;
            }
            s_winDialog = new Window(isFind ? "Find" : "Replace")
            {
                X = s_win.Bounds.Width / 2 - 30,
                Y = s_win.Bounds.Height / 2 - 10,
                ColorScheme = Colors.TopLevel,
            };
            s_winDialog.Border.Effect3D = true;

            s_tabView = new TabView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
            s_tabView.AddTab(new TabView.Tab("Find", FindTab()), isFind);
            View replace = ReplaceTab();
            s_tabView.AddTab(new TabView.Tab("Replace", replace), !isFind);
            s_tabView.SelectedTabChanged += (s, e) => s_tabView.SelectedTab.View.FocusFirst();
            s_winDialog.Add(s_tabView);

            s_win.Add(s_winDialog);
            s_winDialog.Width = replace.Width + 4;
            s_winDialog.Height = replace.Height + 4;
            s_winDialog.SuperView.BringSubviewToFront(s_winDialog);
            s_winDialog.SetFocus();
        }

        // The selection, if any, is what to find.
        private static void SetFindText()
        {
            s_textToFind = !s_textView.SelectedText.IsEmpty
                ? s_textView.SelectedText.ToString()
                : string.IsNullOrEmpty(s_textToFind) ? "" : s_textToFind;
            s_textToReplace = string.IsNullOrEmpty(s_textToReplace) ? "" : s_textToReplace;
        }

        private static View FindTab()
        {
            var d = new View();
            d.DrawContent += _ =>
            {
                foreach (View v in d.Subviews) v.SetNeedsDisplay();
            };
            int labelWidth = "Replace:".Length;

            var label = new Label("Find:") { Y = 1, Width = labelWidth, TextAlignment = TextAlignment.Right, AutoSize = false };
            d.Add(label);

            SetFindText();
            var toFind = new TextField(s_textToFind) { X = Pos.Right(label) + 1, Y = Pos.Top(label), Width = 20 };
            toFind.Enter += _ => toFind.Text = s_textToFind;
            d.Add(toFind);

            var next = Button("Find _Next", Pos.Right(toFind) + 1, Pos.Top(label), !toFind.Text.IsEmpty, () => ContinueFind(true, false));
            next.IsDefault = true;
            d.Add(next);
            var previous = Button("Find _Previous", Pos.Right(toFind) + 1, Pos.Top(next) + 1, !toFind.Text.IsEmpty, () => ContinueFind(false, false));
            d.Add(previous);

            toFind.TextChanged += _ =>
            {
                s_textToFind = toFind.Text.ToString();
                s_textView.FindTextChanged();
                next.Enabled = previous.Enabled = !toFind.Text.IsEmpty;
            };

            var cancel = Button("Cancel", Pos.Right(toFind) + 1, Pos.Top(previous) + 2, true, DisposeWinDialog);
            d.Add(cancel);
            AddMatchBoxes(d, toFind);

            d.Width = label.Width + toFind.Width + next.Width + 2;
            d.Height = next.Height + previous.Height + cancel.Height + 4;
            return d;
        }

        private static View ReplaceTab()
        {
            var d = new View();
            d.DrawContent += _ =>
            {
                foreach (View v in d.Subviews) v.SetNeedsDisplay();
            };
            int labelWidth = "Replace:".Length;

            var label = new Label("Find:") { Y = 1, Width = labelWidth, TextAlignment = TextAlignment.Right, AutoSize = false };
            d.Add(label);

            SetFindText();
            var toFind = new TextField(s_textToFind) { X = Pos.Right(label) + 1, Y = Pos.Top(label), Width = 20 };
            toFind.Enter += _ => toFind.Text = s_textToFind;
            d.Add(toFind);

            var next = Button("Replace _Next", Pos.Right(toFind) + 1, Pos.Top(label), !toFind.Text.IsEmpty, () => ContinueFind(true, true));
            next.IsDefault = true;
            d.Add(next);

            var replaceLabel = new Label("Replace:") { X = Pos.Left(label), Y = Pos.Top(label) + 1, Width = labelWidth, TextAlignment = TextAlignment.Right };
            d.Add(replaceLabel);
            SetFindText();
            var toReplace = new TextField(s_textToReplace) { X = Pos.Right(replaceLabel) + 1, Y = Pos.Top(replaceLabel), Width = 20 };
            toReplace.TextChanged += _ => s_textToReplace = toReplace.Text.ToString();
            d.Add(toReplace);

            var previous = Button("Replace _Previous", Pos.Right(toFind) + 1, Pos.Top(next) + 1, !toFind.Text.IsEmpty, () => ContinueFind(false, true));
            d.Add(previous);
            var all = Button("Replace _All", Pos.Right(toFind) + 1, Pos.Top(previous) + 1, !toFind.Text.IsEmpty, ReplaceAll);
            d.Add(all);

            toFind.TextChanged += _ =>
            {
                s_textToFind = toFind.Text.ToString();
                s_textView.FindTextChanged();
                next.Enabled = previous.Enabled = all.Enabled = !toFind.Text.IsEmpty;
            };

            var cancel = Button("Cancel", Pos.Right(toFind) + 1, Pos.Top(all) + 1, true, DisposeWinDialog);
            d.Add(cancel);
            AddMatchBoxes(d, toFind);

            d.Width = labelWidth + toFind.Width + next.Width + 2;
            d.Height = next.Height + previous.Height + cancel.Height + 4;
            return d;
        }

        private static Button Button(string title, Pos x, Pos y, bool enabled, Action clicked)
        {
            var b = new Button(title)
            {
                X = x,
                Y = y,
                Width = 20,
                Enabled = enabled,
                TextAlignment = TextAlignment.Centered,
                AutoSize = false,
            };
            b.Clicked += clicked;
            return b;
        }

        private static void AddMatchBoxes(View d, View under)
        {
            var matchCase = new CheckBox("Match c_ase") { X = 0, Y = Pos.Top(under) + 2, Checked = s_matchCase };
            matchCase.Toggled += _ => s_matchCase = matchCase.Checked;
            d.Add(matchCase);
            var wholeWord = new CheckBox("Match _whole word") { X = 0, Y = Pos.Top(matchCase) + 1, Checked = s_matchWholeWord };
            wholeWord.Toggled += _ => s_matchWholeWord = wholeWord.Checked;
            d.Add(wholeWord);
        }
    }
}
