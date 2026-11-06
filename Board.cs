// The Pinboard window: one board of MultiViewer players at a time. Players of other boards park off screen.
// Each player keeps its place as a fraction of the board area, so players scale when the window is resized;
// a player is pushed back inside after a drag. Pinboard cannot clip MultiViewer windows: MultiViewer (Chromium)
// removes any window region at once (measured), so a player smaller than its minimum size can stick out.
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Text.RegularExpressions;

class Board : Form
{
    const uint MoveOnly = 0x15, MoveSize = 0x14; // SWP_NOZORDER | SWP_NOACTIVATE (| SWP_NOSIZE)
    Cfg cfg;
    readonly Regex skip;
    Rectangle scr, virt;                   // home monitor (the one the board is on); all monitors together
    Rectangle[] others = [];               // every monitor but home
    Rectangle content;                     // board area under the title bar, screen px: players live here
    HashSet<uint> mv = [];                 // MultiViewer process ids
    readonly HashSet<IntPtr> win = [], pinned = [], restored = [], hid = [];
    readonly N.ITaskbarList bar = (N.ITaskbarList)new N.TaskbarList();
    readonly Arrow left = new(false), right = new(true);
    readonly Dots dots = new();
    readonly Glass glass = new(false, false); // frosted glass under the title bar
    long dotsUntil;
    readonly Dictionary<IntPtr, Shield> shields = [];
    readonly Dictionary<IntPtr, Place> at = [];         // every player: its board and place
    readonly NotifyIcon tray = new();
    readonly Popup menu = new(true); // player list and menu; opens where the user last dragged it
    readonly System.Windows.Forms.Timer tick = new() { Interval = 16 };
    readonly N.WinEvent onEvent;
    readonly IntPtr[] hooks = new IntPtr[3];
    Bitmap? face;                          // window image handed to Windows (layered window)
    Point lastCursor;
    IntPtr drag;
    long holdFrom, homeAt;
    int cur, ticks, holdSide, hover = -1;
    bool busy, keys, layoutDue, menuDue;

    // Board and place of a player; F is the player rectangle as fractions of the board area.
    record struct Place(int Board, RectangleF F);

    public Board(Cfg c)
    {
        cfg = c;
        skip = new(c.Skip, RegexOptions.None, TimeSpan.FromMilliseconds(50));
        Text = Program.Name;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        BackColor = ColorTranslator.FromHtml(c.Back);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        var main = Screen.PrimaryScreen!.WorkingArea;
        Bounds = c.Window.Length == 4 && Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(new(c.Window[0], c.Window[1], c.Window[2], c.Window[3])))
            ? new(c.Window[0], c.Window[1], c.Window[2], c.Window[3])
            : Rectangle.Inflate(main, -main.Width / 8, -main.Height / 8);
        if (c.Maximized) WindowState = FormWindowState.Maximized;
        bar.HrInit();

        left.Go = () => Go(Wrap(cur - 1), -1);
        dots.Go = k => Go(k, k > cur ? 1 : -1);
        right.Go = () => Go(Wrap(cur + 1), 1);

        tray.Icon = Icon; tray.Text = Program.Name; tray.Visible = true;
        tray.MouseUp += (_, e) => { if (e.Button is MouseButtons.Left or MouseButtons.Right) menu.Open(Cursor.Position, MenuItems(), "Players"); };

        onEvent = OnEvent;
        // 60 Hz while MultiViewer players are open (edge arrows, drags, title bar); 2 Hz while waiting for them.
        tick.Tick += (_, _) =>
        {
            if (win.Count == 0 || ticks++ % 30 == 0) Scan();
            tick.Interval = win.Count == 0 ? 500 : 16;
            if (win.Count == 0) return;
            if (ticks % 60 == 0) N.DwmInvalidateIconicBitmaps(Handle); // preview refreshes when shown
            if (menuDue && ModifierKeys == Keys.None) { menuDue = false; menu.Open(Cursor.Position, MenuItems(), "Players"); }
            if (homeAt != 0 && Environment.TickCount64 >= homeAt) { homeAt = 0; Go(0, -1, 0, false); } // setup opened: board 1
            if (layoutDue && !busy) { layoutDue = false; Arrange(); }
            Back(); Hover(); Guard(); TitleHover();
        };
        tick.Start();
        _ = Handle; // hotkeys and hooks need it before the window first shows
        ShowHidden();
        Hub.AtStart(this);
    }

    public Cfg Settings => cfg;

    int S(int px) => (int)Math.Round(px * DeviceDpi / 96f);     // px at 96 dpi -> px on this monitor
    int TitleH => full ? 0 : S(32);
    bool full;                 // F11: whole monitor, taskbar and title bar hidden
    Rectangle normalBounds;    // window place before full screen
    bool normalMax;

    // ---- Window frame: borderless, layered, resizable at its edges, own title bar ----

    // POPUP | THICKFRAME | MINIMIZEBOX | MAXIMIZEBOX | SYSMENU: resize, snap, maximize and taskbar minimize work as in any app.
    // LAYERED: Windows keeps the window image itself (PaintBack), so the board never shows the screen behind it.
    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.Style = unchecked((int)0x80000000) | 0x40000 | 0x20000 | 0x10000 | 0x80000;
            p.ExStyle |= 0x80000;
            return p;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        MinimumSize = new(S(480), S(300));
        Areas();
        int on = 1; // taskbar preview drawn by Pinboard: the board on screen
        N.DwmSetWindowAttribute(Handle, 7, ref on, 4);  // DWMWA_FORCE_ICONIC_REPRESENTATION
        N.DwmSetWindowAttribute(Handle, 10, ref on, 4); // DWMWA_HAS_ICONIC_BITMAP
        hooks[0] = N.SetWinEventHook(0x16, 0x17, 0, onEvent, 0, 0, 0); // MINIMIZESTART..END
        hooks[1] = N.SetWinEventHook(0x03, 0x03, 0, onEvent, 0, 0, 0); // FOREGROUND
        hooks[2] = N.SetWinEventHook(0x0A, 0x0B, 0, onEvent, 0, 0, 0); // MOVESIZESTART..END
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Areas(true); }
    protected override void OnMove(EventArgs e) { base.OnMove(e); Areas(false); }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); MinimumSize = new(S(480), S(300)); Areas(true); }

    // Board area and home monitor follow the window; players follow on the next tick.
    void Areas(bool repaint = true)
    {
        if (!IsHandleCreated || WindowState == FormWindowState.Minimized) return;
        var b = full ? Bounds : WindowState == FormWindowState.Maximized ? Screen.FromHandle(Handle).WorkingArea : Bounds; // maximized: work area, even before Windows has applied it
        content = Rectangle.FromLTRB(b.Left + Edge, b.Top + TitleH, b.Right - Edge, b.Bottom - Edge);
        Monitors();
        if (repaint) PaintBack();
        layoutDue = true;
        quiet = Environment.TickCount64 + 1000;
    }

    // Until then, a player off its place is still on its way there (Pinboard or MultiViewer resizing), not moved by the user.
    long quiet;

    int Edge => full || WindowState == FormWindowState.Maximized ? 0 : 1; // 1 px frame line when not maximized

    // F11: the board covers its whole monitor (Windows hides the taskbar for it); again: back to where it was.
    // Pinboard holds F11 as a shortcut, so MultiViewer does not get the key and no player goes full screen with it.
    void FullScreen()
    {
        if (!full)
        {
            normalMax = WindowState == FormWindowState.Maximized;
            normalBounds = normalMax ? RestoreBounds : Bounds;
            var mon = Screen.FromHandle(Handle).Bounds;
            full = true;
            if (normalMax) WindowState = FormWindowState.Normal;
            Bounds = mon;
        }
        else
        {
            full = false;
            Bounds = normalBounds;
            if (normalMax) WindowState = FormWindowState.Maximized;
        }
        Areas(true);
    }

    void Monitors()
    {
        var home = Screen.FromHandle(Handle);
        scr = home.Bounds;
        others = Screen.AllScreens.Select(s => s.Bounds).Where(b => b != scr).ToArray();
        virt = others.Aggregate(scr, Rectangle.Union);
    }

    // Title bar parts, window px: 0-3 board dots, 10 minimize, 11 maximize, 12 close.
    Rectangle Part(int i)
    {
        int w = Width, h = TitleH, b = S(46);
        if (i >= 10) return new(w - (13 - i) * b, 0, b, h);
        int d = S(20), x0 = w / 2 - cfg.Boards * d / 2;
        return new(x0 + i * d, 0, d, h);
    }

    int PartAt(Point p)
    {
        if (p.Y < 0 || p.Y >= TitleH) return -1;
        foreach (var i in Enumerable.Range(0, cfg.Boards).Concat([10, 11, 12])) if (Part(i).Contains(p)) return i;
        return -1;
    }

    void TitleHover()
    {
        var p = PointToClient(Cursor.Position);
        int h = ClientRectangle.Contains(p) ? PartAt(p) : -1;
        if (h != hover) { hover = h; PaintBack(); }
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case 0x83 when m.WParam != 0: m.Result = 0; return;           // WM_NCCALCSIZE: no frame, the window is all client
            case 0x84: m.Result = HitTest(PointToClient(new((short)(long)m.LParam, (short)((long)m.LParam >> 16)))); return; // WM_NCHITTEST
            case 0xA1 when (int)m.WParam is 8 or 9 or 20: return;         // WM_NCLBUTTONDOWN on a caption button: acted on at button up
            case 0xA2:                                                    // WM_NCLBUTTONUP
                if ((int)m.WParam == 8) WindowState = FormWindowState.Minimized;
                else if ((int)m.WParam == 9) WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
                else if ((int)m.WParam == 20) Exit();
                if ((int)m.WParam is 8 or 9 or 20) return;
                break;
            case 0x24:                                                    // WM_GETMINMAXINFO: maximized = work area exactly, no overhang
                base.WndProc(ref m);
                var mm = System.Runtime.InteropServices.Marshal.PtrToStructure<N.MinMax>(m.LParam);
                var s = Screen.FromHandle(Handle);
                mm.MaxPosition = new(s.WorkingArea.X - s.Bounds.X, s.WorkingArea.Y - s.Bounds.Y);
                mm.MaxSize = new(s.WorkingArea.Width, s.WorkingArea.Height);
                System.Runtime.InteropServices.Marshal.StructureToPtr(mm, m.LParam, false);
                return;
            case 0x312: Shortcut((int)m.WParam); break;                   // WM_HOTKEY
            case 0x323: Thumb((int)((long)m.LParam >> 16) & 0xFFFF, (int)(long)m.LParam & 0xFFFF); return; // WM_DWMSENDICONICTHUMBNAIL
        }
        if (m.Msg == Program.HowTo) Front();
        if (m.Msg == Program.QuitMsg) Close(); // setup or uninstall asks
        base.WndProc(ref m);
        if (m.Msg == 0x47) Frost(); // WM_WINDOWPOSCHANGED: glass follows the window, z-order included
    }

    // Edges resize, title bar moves, buttons act; HT codes so Windows does the moving, sizing and snapping.
    IntPtr HitTest(Point p)
    {
        int e = S(6);
        if (full) return 1;
        bool max = WindowState == FormWindowState.Maximized;
        if (!max)
        {
            bool l = p.X < e, r = p.X >= Width - e, t = p.Y < e, b = p.Y >= Height - e;
            if (t && l) return 13; if (t && r) return 14; if (b && l) return 16; if (b && r) return 17;
            if (l) return 10; if (r) return 11; if (t) return 12; if (b) return 15;
        }
        int part = PartAt(p);
        if (part is 10 or 11 or 12) return part == 10 ? 8 : part == 11 ? 9 : 20; // HTMINBUTTON, HTMAXBUTTON (snap layouts), HTCLOSE
        if (part >= 0) return 1;                     // dots: client, clicked in OnMouseDown
        return p.Y < TitleH ? 2 : 1;                 // HTCAPTION : HTCLIENT
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int part = PartAt(e.Location);
        if (e.Button == MouseButtons.Left && part is >= 0 and < 4) Go(part, part > cur ? 1 : -1);
    }

    // Window image: board colour, title bar with name, board dots and buttons. Handed to Windows once per change.
    void PaintBack()
    {
        if (!IsHandleCreated || Width < 1 || Height < 1) return;
        if (face == null || face.Size != Size) { face?.Dispose(); face = new(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb); }
        using (var g = Graphics.FromImage(face))
        {
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = Theme.Hint;
            if (!full) Title(g); // full screen: board colour only
            if (Edge > 0) { using var line = new Pen(Theme.Line); g.DrawRectangle(line, 0, 0, Width - 1, Height - 1); }
        }
        Layer.Draw(Handle, face, Location);
    }

    // Title bar: see-through tint over the frosted glass window below it (Frost); name, board dots and buttons on top.
    void Title(Graphics g)
    {
        g.CompositingMode = CompositingMode.SourceCopy; // replace the board colour, alpha included
        using (var tint = new SolidBrush(Color.FromArgb(Theme.Tint, Theme.Title))) g.FillRectangle(tint, 0, 0, Width, TitleH);
        g.CompositingMode = CompositingMode.SourceOver;
        using var font = new Font(Theme.Face, 9.5f * DeviceDpi / 96f, GraphicsUnit.Point);
        using (var ico = new Icon(Icon!, S(16), S(16))) g.DrawIcon(ico, new Rectangle(S(10), (TitleH - S(16)) / 2, S(16), S(16)));
        var mid = new StringFormat { LineAlignment = StringAlignment.Center };
        using var soft = new SolidBrush(Theme.Soft);
        g.DrawString(Program.Name, font, soft, new RectangleF(S(34), 0, Width / 2f - S(60), TitleH), mid);
        Pips.Draw(g, cfg.Boards, cur, hover, Part, DeviceDpi / 96f);
        using var white = new SolidBrush(Theme.Text);
        if (cfg.Boards > 0) g.DrawString(cfg.Name(cur), font, white, new RectangleF(Part(cfg.Boards - 1).Right + S(8), 0, S(200), TitleH), mid);
        for (int i = 10; i <= 12; i++)
        {
            var r = Part(i);
            if (i == hover) { using var hb = new SolidBrush(i == 12 ? Theme.Red : Color.FromArgb(40, Color.White)); g.FillRectangle(hb, r); }
            using var pen = new Pen(Theme.Text, S(1));
            int cx = r.X + r.Width / 2, cy = r.Height / 2, q = S(5);
            if (i == 10) g.DrawLine(pen, cx - q, cy, cx + q, cy);
            if (i == 11 && WindowState == FormWindowState.Maximized) { g.DrawRectangle(pen, cx - q + S(2), cy - q, 2 * q - S(2), 2 * q - S(2)); g.DrawRectangle(pen, cx - q, cy - q + S(2), 2 * q - S(2), 2 * q - S(2)); }
            else if (i == 11) g.DrawRectangle(pen, cx - q, cy - q, 2 * q, 2 * q);
            if (i == 12) { g.DrawLine(pen, cx - q, cy - q, cx + q, cy + q); g.DrawLine(pen, cx - q, cy + q, cx + q, cy - q); }
        }
    }

    // Frosted glass right under the title bar; none in full screen, minimized or hidden.
    void Frost()
    {
        if (full || !N.IsWindowVisible(Handle) || N.IsIconic(Handle)) glass.Off();
        else glass.Under(Handle, new(Left + Edge, Top + Edge, Width - 2 * Edge, TitleH - Edge));
    }

    // ---- Shortcuts: only while Pinboard or MultiViewer is in front, so other apps keep these keys ----

    bool Mine(IntPtr fg)
    {
        N.GetWindowThreadProcessId(fg, out var p);
        return p == Environment.ProcessId || mv.Contains(p);
    }

    void Hotkeys(bool on)
    {
        if (on == keys) return;
        keys = on;
        for (int i = 0; i < Hotkey.All.Length; i++)
        {
            N.UnregisterHotKey(Handle, i + 1);
            if (on && Hotkey.Parse(cfg.Shortcuts.GetValueOrDefault(Hotkey.All[i].Id)) is { } k && !N.RegisterHotKey(Handle, i + 1, k.Mods | 0x4000, k.Vk))
                Program.Log(new($"Shortcut {cfg.Shortcuts[Hotkey.All[i].Id]} not registered: in use by another app"));
        }
    }

    void Shortcut(int id)
    {
        switch (Hotkey.All.ElementAtOrDefault(id - 1).Id)
        {
            case "prev": Go(Wrap(cur - 1), -1); break;
            case "next": Go(Wrap(cur + 1), 1); break;
            case "sendPrev": Send(-1); break;
            case "sendNext": Send(1); break;
            case "menu": menuDue = true; break; // opens once the keys are up
            case "full": FullScreen(); break;
            case { } s when s.StartsWith("board"): int k = s[^1] - '1'; if (k < cfg.Boards) Go(k, k > cur ? 1 : -1); break;
        }
    }

    // New settings from the Settings window: applied at once and saved.
    public void Apply(Cfg c)
    {

        cfg = c;
        foreach (var (h, p) in at.Where(a => a.Value.Board >= c.Boards).ToList()) at[h] = p with { Board = c.Boards - 1 }; // removed boards: players go to the last board
        if (!c.Protect) { foreach (var s in shields.Values) s.Dispose(); shields.Clear(); }
        keys = !keys; Hotkeys(!keys); // re-register with the new keys
        Setup.AutoStart(c.AutoStart);
        cfg.Save();
        if (cur >= c.Boards) Go(c.Boards - 1, -1, 0, false);
        layoutDue = true;
        PaintBack();
    }

    protected override void SetVisibleCore(bool v) => base.SetVisibleCore(v && win.Count > 0);

    // Window place is kept for next start; minimized boards hide their players, so restore first.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (full) FullScreen();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        cfg.Maximized = WindowState == FormWindowState.Maximized;
        var b = cfg.Maximized ? RestoreBounds : Bounds;
        cfg.Window = [b.X, b.Y, b.Width, b.Height];
        cfg.Save();
        base.OnFormClosing(e);
    }

    bool closePlayers;

    // Exit from the menu or the close button: Pinboard only, or MultiViewer players too (asked unless the user chose for good).
    // Setup, uninstall and Windows shut-down close Pinboard only, without asking.
    void Exit()
    {
        var choice = cfg.OnExit;
        if (choice == "ask")
        {
            var (t, remember) = Ask.Show(this, "Close Pinboard", "Players stay open in MultiViewer, or close with Pinboard.", "Don't ask again (change it in Settings)",
                ("Close Pinboard and players", "close"), ("Close Pinboard only", "keep"));
            if (t == null) return;
            choice = t;
            if (remember) { cfg.OnExit = t; cfg.Save(); }
        }
        closePlayers = choice == "close";
        Close();
    }

    // Exit: every player back on screen at its board place, owner link and taskbar button restored.
    // Players close too when the user chose so (MultiViewer closes each one as if its X were clicked).
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (closePlayers) foreach (var h in win) N.PostMessage(h, 0x10, 0, 0); // WM_CLOSE
        foreach (var h in hid) N.ShowWindow(h, 4);
        hid.Clear(); SaveHidden();
        foreach (var h in win)
        {
            if (!N.IsIconic(h)) ToScreen(h);
            N.SetWindowLongPtr(h, -8, 0);
            bar.AddTab(h);
        }
        foreach (var h in pinned) N.SetWindowPos(h, -1, 0, 0, 0, 0, 0x13);
        foreach (var h in hooks) N.UnhookWinEvent(h);
        tick.Stop();
        tray.Dispose();
        base.OnFormClosed(e);
        Application.Exit();
    }

    // ---- Players and boards ----

    static Rectangle R(IntPtr h) { N.GetWindowRect(h, out var r); return Rectangle.FromLTRB(r.L, r.T, r.R, r.B); }

    Rectangle Px(RectangleF f) => Rectangle.FromLTRB(
        content.X + (int)Math.Round(f.Left * content.Width), content.Y + (int)Math.Round(f.Top * content.Height),
        content.X + (int)Math.Round(f.Right * content.Width), content.Y + (int)Math.Round(f.Bottom * content.Height));

    RectangleF Frac(Rectangle r) => RectangleF.FromLTRB(
        (r.Left - content.X) / (float)content.Width, (r.Top - content.Y) / (float)content.Height,
        (r.Right - content.X) / (float)content.Width, (r.Bottom - content.Y) / (float)content.Height);

    // Too big for the board: smaller, same aspect ratio, top-left kept (players cannot be cut: MultiViewer undoes it).
    Rectangle Fit(Rectangle r)
    {
        double s = Math.Min(1, Math.Min(content.Width / (double)r.Width, content.Height / (double)r.Height));
        return s < 1 ? new(r.X, r.Y, (int)(r.Width * s), (int)(r.Height * s)) : r;
    }

    // Moved, not resized, so as much of it as fits is on the board.
    Rectangle Inside(Rectangle r)
    {
        r.X = Math.Clamp(r.X, content.Left, Math.Max(content.Left, content.Right - r.Width));
        r.Y = Math.Clamp(r.Y, content.Top, Math.Max(content.Top, content.Bottom - r.Height));
        return r;
    }

    // Own every visible MultiViewer window except the main one; sort new ones onto boards; repair drift.
    void Scan()
    {
        mv = Process.GetProcessesByName("MultiViewer").Select(p => { using (p) return (uint)p.Id; }).ToHashSet();
        bool was = win.Count > 0;
        Monitors();
        win.RemoveWhere(h => !N.IsWindow(h));
        hid.RemoveWhere(h => !N.IsWindow(h));
        if (drag != 0 && !N.IsWindow(drag)) drag = 0;
        foreach (var h in at.Keys.Where(h => !win.Contains(h)).ToList()) at.Remove(h);
        // main window can change title; a player put on another monitor, outside the board, leaves the boards
        foreach (var h in win.Where(h => MvMain(h) || Elsewhere(h)).ToList()) Release(h);
        var fresh = new List<IntPtr>();
        N.EnumWindows((h, _) =>
        {
            N.GetWindowThreadProcessId(h, out var p);
            if (!mv.Contains(p) || !N.IsWindowVisible(h) || win.Contains(h)) return true;
            var t = N.Title(h);
            if (t.Length == 0 || MvMain(h) || Elsewhere(h)) return true;
            win.Add(h);
            fresh.Add(h);
            N.SetWindowLongPtr(h, -8, Handle);
            if (!N.IsIconic(h)) bar.DeleteTab(h); // a minimized player keeps its button
            // Overlays (e.g. track map) have no title bar; their topmost flag cannot be trusted, Pinboard itself drops it.
            if ((N.GetWindowLongPtr(h, -20) & 0x8) != 0 || (N.GetWindowLongPtr(h, -16) & 0xC00000) == 0) pinned.Add(h);
            return true;
        }, 0);
        var used = win.Where(h => !N.IsIconic(h)).Select(Zone).Where(k => k >= 0 && k < cfg.Boards).ToHashSet();
        used.UnionWith(hid.Where(at.ContainsKey).Select(h => at[h].Board));
        int free = Enumerable.Range(0, cfg.Boards).Where(k => !used.Contains(k)).DefaultIfEmpty(-1).First();
        if (!was && win.Count > 0)
        {
            // first players: the board on screen is the first board whose parking place is empty; board 1 shows once the setup has opened
            cur = Math.Max(0, free);
            homeAt = Environment.TickCount64 + 1500;
            Visible = true;
            Areas();
        }
        // A MultiViewer setup opens players on screen for the board that showed at save, the rest in their parking places.
        // A player landing in the parking place of the board on screen: the setup was saved on another board, so take an empty one.
        else if (free >= 0 && fresh.Any(h => !N.IsIconic(h) && Zone(h) == cur))
        {
            foreach (var (h, p) in at.Where(a => a.Value.Board == cur).ToList()) at[h] = p with { Board = free };
            cur = free;
            PaintBack();
        }
        foreach (var h in fresh) Sort(h);
        if (!busy && drag == 0) { foreach (var h in win.ToList()) Keep(h); }
        if (was && win.Count == 0) Hide();
        Hotkeys(Mine(N.GetForegroundWindow()));
    }

    // New player: in a parking place joins that board; anywhere else joins the board on screen, inside the board area.
    void Sort(IntPtr h)
    {
        if (N.IsIconic(h)) return; // sorted when restored
        var r = R(h);
        int k = Zone(h);
        if (k >= 0 && k < cfg.Boards && k != cur) { r.Offset(-Park(k), 0); at[h] = new(k, Frac(r)); return; }
        if (k >= 0) r.Offset(-Park(k), 0);
        else if (k == int.MinValue) r.X = OnHome(r.Left, r.Right);
        var t = Full(h) || N.IsZoomed(h) ? r : Inside(Fit(r));
        at[h] = new(cur, Frac(t));
        if (t != R(h)) N.SetWindowPos(h, 0, t.X, t.Y, t.Width, t.Height, MoveSize);
    }

    // x that puts a player back on home, keeping its place within one screen width.
    int OnHome(int l, int r)
    {
        int cx = (l + r) / 2, W = scr.Width;
        int k = (int)Math.Floor((cx - scr.Left) / (double)W);
        return l - k * W;
    }

    // Repair: a player moved on the board (not by Pinboard) takes its new place; parked ones go back to theirs.
    void Keep(IntPtr h)
    {
        if (hid.Contains(h) || N.IsIconic(h) || !at.TryGetValue(h, out var p)) return;
        var r = R(h);
        bool home = Zone(h) == -1;
        if (p.Board == cur)
        {
            if (!home) { Sort(h); return; }
            // Never over the title bar or past the board edge where it fits (MultiViewer moves players when it keeps their aspect ratio).
            if (!Full(h) && !N.IsZoomed(h)) { var inside = Inside(r); if (inside != r) { N.SetWindowPos(h, 0, inside.X, inside.Y, 0, 0, MoveOnly); r = inside; } }
            var t = Px(p.F); // only a move counts: MultiViewer itself adjusts sizes (aspect ratio, minimum size)
            if (!layoutDue && Environment.TickCount64 > quiet && (Math.Abs(r.X - t.X) > 8 || Math.Abs(r.Y - t.Y) > 8)) at[h] = p with { F = Frac(r) };
            return;
        }
        if (Full(h)) { Conceal(h); return; } // made full screen while parked: MultiViewer pulls it on screen
        if (home) { at[h] = new(cur, Frac(r)); return; } // brought on screen (MultiViewer or user): it joins this board
        var to = Px(p.F); to.Offset(Park(p.Board), 0);
        if (r.X != to.X || r.Y != to.Y) N.SetWindowPos(h, 0, to.X, to.Y, 0, 0, MoveOnly);
    }

    // Every player to its place for the current board area and size.
    void Arrange()
    {
        if (content.Width < 50 || content.Height < 50 || WindowState == FormWindowState.Minimized) return;
        foreach (var (h, p) in at.ToList())
        {
            if (hid.Contains(h) || N.IsIconic(h) || Full(h) || N.IsZoomed(h)) continue;
            var t = Inside(Fit(Px(p.F)));
            if (p.Board != cur) t.Offset(Park(p.Board), 0);
            if (t != R(h)) N.SetWindowPos(h, 0, t.X, t.Y, t.Width, t.Height, MoveSize);
        }
        quiet = Environment.TickCount64 + 1000;
        Arrows();
    }

    void Release(IntPtr h)
    {
        if (hid.Remove(h)) { N.ShowWindow(h, 4); SaveHidden(); }
        win.Remove(h); pinned.Remove(h); at.Remove(h);
        N.SetWindowLongPtr(h, -8, 0);
        bar.AddTab(h);
    }

    // MultiViewer's own main window (home, archive, settings: where players are picked) never goes on a board.
    // Its title changes with the page; its style does not: it clips its children and has a system menu, players do neither.
    bool MvMain(IntPtr h) => skip.IsMatch(N.Title(h)) || (N.GetWindowLongPtr(h, -16) & 0x02080000) == 0x02080000;

    static Point Center(IntPtr h) { var r = R(h); return new((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2); }

    // Centre on a monitor other than home and outside the board: the user keeps it there. Parked players never count.
    bool Elsewhere(IntPtr h)
    {
        if (N.IsIconic(h) || (at.TryGetValue(h, out var p) && p.Board != cur)) return false;
        var c = Center(h);
        return !content.Contains(c) && others.Any(o => o.Contains(c));
    }

    int Zone(IntPtr h) => Geo.Region(Center(h).X, scr, virt);

    int Park(int k) => Geo.Park(k, scr, virt);

    bool OnBoard(IntPtr h) => at.TryGetValue(h, out var p) && p.Board == cur && !N.IsIconic(h);

    // Full screen: covers the home monitor, no title bar. MultiViewer puts such a player back on screen
    // when it is moved, so while its board is away it hides instead of parking.
    bool Full(IntPtr h)
    {
        var r = R(h);
        return (N.GetWindowLongPtr(h, -16) & 0xC00000) == 0 && r.Contains(scr);
    }

    void Conceal(IntPtr h) { N.ShowWindow(h, 0); hid.Add(h); SaveHidden(); } // SW_HIDE
    void Reveal(IntPtr h) { N.ShowWindow(h, 4); hid.Remove(h); SaveHidden(); }            // SW_SHOWNOACTIVATE

    // Hidden players are written down: after a crash the next start shows them again.
    static string HidFile => Path.Combine(Program.Data, "hidden.txt");

    void SaveHidden()
    {
        try { if (hid.Count == 0) File.Delete(HidFile); else File.WriteAllLines(HidFile, hid.Select(h => ((long)h).ToString())); }
        catch (IOException) { }
    }

    static void ShowHidden()
    {
        if (!File.Exists(HidFile)) return;
        var ids = Process.GetProcessesByName("MultiViewer").Select(p => { using (p) return (uint)p.Id; }).ToHashSet();
        foreach (var s in File.ReadAllLines(HidFile))
        {
            if (!long.TryParse(s, out var v)) continue;
            var h = (IntPtr)v;
            N.GetWindowThreadProcessId(h, out var p);
            if (N.IsWindow(h) && ids.Contains(p) && !N.IsWindowVisible(h) && Regex.IsMatch(N.Title(h), " . MultiViewer$")) N.ShowWindow(h, 4);
        }
        File.Delete(HidFile);
    }

    // Put a player on the board on screen at its place.
    void ToScreen(IntPtr h)
    {
        if (N.IsIconic(h)) return;
        var p = at.TryGetValue(h, out var q) ? q : new(cur, Frac(Inside(R(h))));
        at[h] = p with { Board = cur };
        if (Full(h) || N.IsZoomed(h)) return;
        var t = Px(p.F);
        N.SetWindowPos(h, 0, t.X, t.Y, t.Width, t.Height, MoveSize);
    }

    int Wrap(int b) => ((b % cfg.Boards) + cfg.Boards) % cfg.Boards;

    // Board `to` comes on screen from the right (dir 1) or left (-1); only the board leaving and the board arriving move.
    // The slide runs on a worker thread so Pinboard and MultiViewer stay responsive.
    // keep: player under the cursor in a drag, which lands on the new board.
    void Go(int to, int dir, IntPtr keep = 0, bool animate = true)
    {
        if (busy || cfg.Boards < 2 || to == cur || to < 0 || to >= cfg.Boards) return;
        busy = true;
        int from = cur;
        var area = content;
        var leaving = at.Where(a => a.Value.Board == from && a.Key != keep && !N.IsIconic(a.Key) && !hid.Contains(a.Key)).Select(a => a.Key).ToList();
        foreach (var h in leaving.Where(Full).ToList()) { Conceal(h); leaving.Remove(h); }
        var outgo = leaving.Select(h => (h, R(h))).ToArray();
        var income = at.Where(a => a.Value.Board == to && !N.IsIconic(a.Key) && !hid.Contains(a.Key))
            .Select(a => (a.Key, N.IsZoomed(a.Key) ? Shift(R(a.Key), -Park(to)) : Px(a.Value.F))).ToArray();
        // Slides only when the board fills its monitor and no monitor is beside it: players beside the board are then off screen.
        bool slide = animate && cfg.SlideMs > 0 && Visible && (full || WindowState == FormWindowState.Maximized) && Geo.CanSlide(scr, others);
        int W = area.Width, ms = cfg.SlideMs;
        Task.Run(() =>
        {
            N.timeBeginPeriod(1); // 15 ms frames need a fine system timer, only while sliding
            try
            {
                if (!slide) return;
                foreach (var (h, t) in income) { var s = Shift(t, dir * W); N.SetWindowPos(h, 0, s.X, s.Y, s.Width, s.Height, MoveSize); }
                var sw = Stopwatch.StartNew();
                for (double f = 0; f < 1;)
                {
                    f = Math.Min(1, sw.Elapsed.TotalMilliseconds / ms);
                    double e = f < .5 ? 4 * f * f * f : 1 - Math.Pow(2 - 2 * f, 3) / 2; // ease in-out cubic
                    int dx = (int)Math.Round(-e * dir * W);
                    var d = N.BeginDeferWindowPos(outgo.Length + income.Length);
                    foreach (var (h, r) in outgo) { var s = Shift(r, dx); if (d != 0) d = N.DeferWindowPos(d, h, 0, s.X, s.Y, 0, 0, MoveOnly); }
                    foreach (var (h, t) in income) { var s = Shift(t, dir * W + dx); if (d != 0) d = N.DeferWindowPos(d, h, 0, s.X, s.Y, 0, 0, MoveOnly); }
                    if (d != 0) N.EndDeferWindowPos(d);
                    Thread.Sleep(15);
                }
                Thread.Sleep(120); // MultiViewer answers fast moves late; the final placement follows on the UI thread
            }
            finally { N.timeEndPeriod(1); }
        }).ContinueWith(_ => BeginInvoke((Action)(() =>
        {
            foreach (var (h, r) in outgo) if (at.TryGetValue(h, out var p)) { var t = N.IsZoomed(h) ? r : Px(p.F); t.Offset(Park(from), 0); N.SetWindowPos(h, 0, t.X, t.Y, t.Width, t.Height, MoveSize); }
            foreach (var (h, t) in income) N.SetWindowPos(h, 0, t.X, t.Y, t.Width, t.Height, MoveSize);
            foreach (var h in hid.Where(h => at.TryGetValue(h, out var p) && p.Board == to).ToList()) Reveal(h);
            if (keep != 0 && at.TryGetValue(keep, out var kp)) at[keep] = kp with { Board = to };
            cur = to;
            busy = false;
            Raise();
            PaintBack();
            dotsUntil = Environment.TickCount64 + 1500; // full screen: dots show the new board for a moment
        })));
    }

    static Rectangle Shift(Rectangle r, int dx) { r.Offset(dx, 0); return r; }

    // Player in front goes to the board left (dir -1) or right (+1); the board on screen stays.
    void Send(int dir)
    {
        var h = N.GetForegroundWindow();
        if (busy || cfg.Boards < 2 || !OnBoard(h)) return;
        int to = Wrap(cur + dir);
        at[h] = at[h] with { Board = to };
        if (Full(h)) Conceal(h);
        else { var t = N.IsZoomed(h) ? R(h) : Px(at[h].F); t.Offset(Park(to), 0); N.SetWindowPos(h, 0, t.X, t.Y, t.Width, t.Height, MoveSize); }
        N.SetForegroundWindow(Handle); // shortcuts keep working
    }

    // Players of the board on screen sit above every other board's players, in their own order.
    void Raise()
    {
        var order = new List<IntPtr>();
        N.EnumWindows((h, _) => { if (win.Contains(h) && OnBoard(h)) order.Add(h); return true; }, 0);
        order.Reverse(); // bottom first, so the top one ends on top
        foreach (var h in order) N.SetWindowPos(h, 0, 0, 0, 0, 0, 0x13); // HWND_TOP; NOSIZE | NOMOVE | NOACTIVATE
        Pin(N.GetForegroundWindow());
    }

    // Started again while running: bring the board to the front, as apps do.
    void Front()
    {
        if (win.Count == 0) { tray.ShowBalloonTip(3000, Program.Name, "Open a setup or stream in MultiViewer.", ToolTipIcon.None); return; }
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        N.SetForegroundWindow(Handle);
        Raise();
    }

    // A restored player comes back on the board on screen and gives up its taskbar button.
    void Back()
    {
        foreach (var h in restored.Where(h => !N.IsIconic(h)).ToList())
        {
            restored.Remove(h);
            bar.DeleteTab(h);
            if (at.ContainsKey(h)) ToScreen(h); else Sort(h);
        }
    }

    // Called from Windows: an exception here would end the process, so log it instead.
    void OnEvent(IntPtr hk, uint ev, IntPtr h, int o, int c, uint th, uint t)
    {
        try
        {
            if (ev == 0x03) { Pin(h); Hotkeys(Mine(h)); }
            else if (o == 0 && win.Contains(h))
                switch (ev)
                {
                    case 0x16: bar.AddTab(h); break; // minimized alone: give it a taskbar button
                    case 0x17: restored.Add(h); break;
                    case 0x0A: drag = h; holdSide = 0; break;
                    case 0x0B: // dropped: on the board it takes its new place; dropped beside the board it comes back inside
                        drag = 0;
                        if (Zone(h) != -1 || Elsewhere(h)) break;
                        var r = R(h);
                        var inside = content.Contains(new Point((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2)) ? r : Inside(r);
                        if (inside != r) N.SetWindowPos(h, 0, inside.X, inside.Y, 0, 0, MoveOnly);
                        at[h] = new(cur, Frac(inside));
                        break;
                }
        }
        catch (Exception e) { Program.Log(e); }
    }

    bool Ours(IntPtr fg) => fg == Handle || win.Contains(fg) || fg == left.Handle || fg == right.Handle || fg == menu.Handle || fg == dots.Handle;

    // Always-on-top MultiViewer windows (e.g. track map) stay on top only while Pinboard is in front
    // and their board is on screen.
    void Pin(IntPtr fg)
    {
        pinned.RemoveWhere(h => !N.IsWindow(h));
        bool ours = Ours(fg);
        foreach (var h in pinned)
            if (ours && OnBoard(h)) N.SetWindowPos(h, -1, 0, 0, 0, 0, 0x13); // HWND_TOPMOST
            else if (ours) N.SetWindowPos(h, -2, 0, 0, 0, 0, 0x13);        // HWND_NOTOPMOST
            else { N.SetWindowPos(h, -2, 0, 0, 0, 0, 0x13); N.SetWindowPos(h, fg, 0, 0, 0, 0, 0x13); } // drop flag, sit under app in front
    }

    // ---- Menu: players with their board; click one to bring it here ----

    static string Short(string title) => Regex.Replace(title, " . MultiViewer$", "");

    public IEnumerable<(string Text, string Right, Action Go)> MenuItems()
    {
        var list = win.Select(h => (h, k: N.IsIconic(h) || !at.TryGetValue(h, out var p) ? int.MaxValue : p.Board))
            .OrderBy(w => w.k).ThenBy(w => N.Title(w.h));
        foreach (var (h, k) in list) yield return (Short(N.Title(h)), k == int.MaxValue ? "Minimized" : cfg.Name(k), () => Bring(h));
        yield return ("", "", () => { });
        yield return ("Previous board", Hotkey.Show(cfg.Shortcuts["prev"]), () => Go(Wrap(cur - 1), -1));
        yield return ("Next board", Hotkey.Show(cfg.Shortcuts["next"]), () => Go(Wrap(cur + 1), 1));
        yield return ("", "", () => { });
        yield return ("How to use", "", () => Hub.Open(this, 0));
        yield return ("Settings", "", () => Hub.Open(this, 1));
        yield return ("Exit", "", Exit);
    }

    // Restore if minimized or hidden, move onto the board on screen, put in front.
    void Bring(IntPtr h)
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        if (N.IsIconic(h)) { N.ShowWindow(h, 9); restored.Remove(h); bar.DeleteTab(h); } // SW_RESTORE
        if (hid.Contains(h)) Reveal(h);
        ToScreen(h);
        N.SetForegroundWindow(h);
    }

    // ---- Capture shields ----

    // Every video player gets a shield its size, owned by it so Windows keeps the shield
    // directly on top of its player in the window stack. Windows shows a shield only on the monitor, so
    // screenshots and screen sharing see black there. Pinboard cannot protect MultiViewer's own windows
    // (Windows refuses: access denied), only windows of its own.
    void Guard()
    {
        if (!cfg.Protect) return;
        foreach (var h in shields.Keys.Where(h => !win.Contains(h)).ToList()) { shields[h].Dispose(); shields.Remove(h); }
        foreach (var h in win.Where(IsPlayer))
        {
            if (!shields.TryGetValue(h, out var s)) shields[h] = s = new Shield(h);
            if (!N.IsWindowVisible(h)) { s.Off(); continue; }
            var r = R(h);
            s.Cover(r);
        }
    }

    // MultiViewer players: caption (no caption in full screen), no system menu. Timing windows have a system menu; track map has no caption.
    bool IsPlayer(IntPtr h)
    {
        long s = N.GetWindowLongPtr(h, -16);
        return (s & 0x80000) == 0 && ((s & 0xC00000) == 0xC00000 || Full(h));
    }

    // ---- Taskbar preview: the board on screen ----

    void Thumb(int maxW, int maxH)
    {
        if (face == null) return;
        using var shot = new Bitmap(face);
        using (var g = Graphics.FromImage(shot))
        {
            var order = new List<IntPtr>();
            N.EnumWindows((h, _) => { if (win.Contains(h)) order.Add(h); return true; }, 0);
            order.Reverse(); // bottom of z-order first
            g.SetClip(new Rectangle(content.X - Left, content.Y - Top, content.Width, content.Height));
            foreach (var h in order.Where(h => OnBoard(h) && !hid.Contains(h)))
            {
                var r = R(h);
                var to = new Rectangle(r.X - Left, r.Y - Top, r.Width, r.Height);
                if (to.Width <= 0 || to.Height <= 0) continue;
                if (IsPlayer(h) && cfg.Protect) { g.FillRectangle(Brushes.Black, to); continue; }
                using var w = new Bitmap(to.Width, to.Height);
                using (var wg = Graphics.FromImage(w)) { var dc = wg.GetHdc(); N.PrintWindow(h, dc, 2); wg.ReleaseHdc(dc); } // PW_RENDERFULLCONTENT
                g.DrawImage(w, to.Location);
            }
        }
        double s = Math.Min(maxW / (double)shot.Width, maxH / (double)shot.Height);
        using var t = new Bitmap(Math.Max(1, (int)(shot.Width * s)), Math.Max(1, (int)(shot.Height * s)), System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(t)) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.DrawImage(shot, 0, 0, t.Width, t.Height); }
        var hb = t.GetHbitmap();
        N.DwmSetIconicThumbnail(Handle, hb, 0);
        N.DeleteObject(hb);
    }

    // ---- Edge arrows: at the board edges, sized for the monitor and the board ----

    void Arrows()
    {
        int w = S(56), h = Math.Clamp(content.Height / 6, S(96), S(200));
        left.SetSize(w, h); right.SetSize(w, h);
    }

    // Arrows show when the cursor moves at a board edge, and hide after it stops.
    // While a player is dragged they stay up; hold the player over one for HoldMs and it rides to that board.
    void Hover()
    {
        var p = Cursor.Position;
        bool moved = p != lastCursor;
        lastCursor = p;
        int y = content.Y + content.Height / 2 - left.H / 2;
        Point lp = new(content.Left, y), rp = new(content.Right - right.W, y);
        bool can = cfg.Boards > 1 && WindowState != FormWindowState.Minimized; // boards loop
        if (drag != 0)
        {
            left.Hold(can, lp); right.Hold(can, rp);
            int side = can && left.Box.Contains(p) ? -1 : can && right.Box.Contains(p) ? 1 : 0;
            if (side != holdSide) { holdSide = side; holdFrom = Environment.TickCount64; }
            else if (side != 0 && Environment.TickCount64 - holdFrom >= cfg.HoldMs) { Go(Wrap(cur + side), side, drag); holdFrom = Environment.TickCount64; }
            return;
        }
        bool ours = can && Ours(N.GetForegroundWindow());
        bool inY = p.Y >= content.Top && p.Y < content.Bottom;
        left.Track(ours, lp, p, moved, inY && p.X >= content.Left && p.X <= content.Left + S(4), cfg.ArrowHideMs);
        right.Track(ours, rp, p, moved, inY && p.X < content.Right && p.X >= content.Right - S(5), cfg.ArrowHideMs);
        // Full screen: board dots at the bottom edge instead of the title bar.
        long now = Environment.TickCount64;
        if (full && ours && moved && p.Y >= content.Bottom - S(6) && p.X >= content.Left && p.X < content.Right) dotsUntil = now + cfg.ArrowHideMs;
        if (full && ours && (now < dotsUntil || dots.Box.Contains(p)))
        {
            dots.Set(cfg.Boards, cur, cfg.Name(cur), DeviceDpi / 96f);
            dots.ShowAt(new(content.X + (content.Width - dots.Box.Width) / 2, content.Bottom - dots.Box.Height - S(16)));
        }
        else dots.Off();
    }
}
