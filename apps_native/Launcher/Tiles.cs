using System;
using System.Collections.Generic;
using SharpOS.AppSdk;
using Terminal.Gui;

namespace Launcher
{
    /// <summary>
    /// One rectangle of the Home tab (step207): a name, a line under it, and
    /// what happens on Enter or a click. Arrows move between tiles.
    /// </summary>
    internal sealed class Tile : View
    {
        public const int TileWidth = 26, TileHeight = 6;

        public string Title { get; }
        public string Subtitle { get; set; }
        public event Action Activated;

        public Tile(string title, string subtitle)
        {
            Title = title;
            Subtitle = subtitle;
            CanFocus = true;
            Width = TileWidth;
            Height = TileHeight;
        }

        public override void Redraw(Rect bounds)
        {
            Driver.SetAttribute(HasFocus ? GetFocusColor() : GetNormalColor());
            DrawFrame(new Rect(0, 0, Bounds.Width, Bounds.Height), 0, true);
            Centered(2, Title);
            Centered(3, Subtitle ?? "");
        }

        private void Centered(int row, string text)
        {
            int room = Bounds.Width - 4;
            if (text.Length > room) text = text.Substring(0, room);
            Move(2 + (room - text.Length) / 2, row);
            Driver.AddStr(text);
        }

        public override void PositionCursor() => Move(1, 1);

        public override bool ProcessKey(KeyEvent kb)
        {
            switch (kb.Key)
            {
                case Key.Enter:
                case Key.Space:
                    Activated?.Invoke();
                    return true;
                case Key.CursorRight:
                case Key.CursorDown:
                    SuperView?.FocusNext();
                    return true;
                case Key.CursorLeft:
                case Key.CursorUp:
                    SuperView?.FocusPrev();
                    return true;
            }
            return base.ProcessKey(kb);
        }

        public override bool MouseEvent(MouseEvent me)
        {
            if (me.Flags != MouseFlags.Button1Clicked) return false;
            if (!HasFocus) SetFocus();
            Activated?.Invoke();
            return true;
        }
    }

    /// <summary>
    /// What the Home tab offers: the shell, PowerShell on the hosted runtime,
    /// and every program in \games.
    /// </summary>
    internal static class Tiles
    {
        internal const string ShellPath = "\\bin\\SHELL.EXE";
        internal const string PowerShellPath = "\\sharpos\\PowerShellBootstrap.dll";
        internal const string GamesDirectory = "\\games";

        // A name on a tile says more than a file name: DOOM is ManagedDoom.
        private static readonly string[][] Known =
        {
            new[] { "DOOM.EXE", "DOOM", "ManagedDoom" },
            new[] { "FAMI.EXE", "Fami", "NES emulator" },
            new[] { "TRICNES.EXE", "TriCNES", "NES emulator, exact" },
        };

        internal sealed class Entry
        {
            public string Title = "";
            public string Subtitle = "";
            public string Path = "";
            public bool Managed;
        }

        internal static List<Entry> Read()
        {
            var entries = new List<Entry>
            {
                new Entry { Title = "Shell", Subtitle = "pipes, KQL, files", Path = ShellPath },
            };
            // An image built with -SkipCoreClr has no hosted runtime: no tile.
            if (AppHost.FileExists(PowerShellPath))
                entries.Add(new Entry { Title = "PowerShell", Subtitle = "PowerShell 7, CoreCLR", Path = PowerShellPath, Managed = true });
            foreach (FileNode node in FileTree.Read(GamesDirectory))
            {
                if (node.Kind != NodeKind.Application) continue;
                var game = new Entry { Title = node.Name, Subtitle = "", Path = node.Path };
                if (game.Title.EndsWith(".EXE")) game.Title = game.Title.Substring(0, game.Title.Length - 4);
                foreach (string[] k in Known)
                {
                    if (node.Name != k[0]) continue;
                    game.Title = k[1];
                    game.Subtitle = k[2];
                }
                entries.Add(game);
            }
            return entries;
        }
    }
}
