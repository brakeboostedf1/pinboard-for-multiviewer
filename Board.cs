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
    readonly Regex mainTitles, timingTitles;
    IntPtr mvMain;                         // MultiViewer's main window, once seen on one of its own pages
    Rectangle scr, virt;                   // home monitor (the one the board is on); all monitors together
    Rectangle[] others = [];               // every monitor but home
    Rectangle content;                     // home monitor, screen px: players live here, as MultiViewer puts them
    HashSet<uint> mv = [];                 // MultiViewer process ids
    readonly HashSet<IntPtr> win = [], pinned = [], restored = [], hid = [];
    readonly N.ITaskbarList bar = (N.ITaskbarList)new N.TaskbarList();
    readonly Arrow left = new(false), right = new(true);
    readonly Dots dots = new();
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
    int cur, ticks, holdSide;
    bool busy, keys, layoutDue, menuDue;

    // Board and place of a player; F is the player rectangle as fractions of the home monitor.
    record struct Place(int Board, RectangleF F);

    public Board(Cfg c)
    {
        cfg = c;
        mainTitles = new(c.MainTitles, RegexOptions.None, TimeSpan.FromMilliseconds(50));
        timingTitles = new(c.TimingTitles, RegexOptions.None, TimeSpan.FromMilliseconds(50));
        Text = Program.Name;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        BackColor = ColorTranslator.FromHtml(c.Back);
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        Bounds = Screen.PrimaryScreen!.WorkingArea;
        bar.HrInit();

        left.Go = () => Go(Wrap(cur - 1), -1);
        dots.Go = k => Go(k, k > cur ? 1 : -1);
        right.Go = () => Go(Wrap(cur + 1), 1);

        tray.Icon = Icon; tray.Text = Program.Name; tray.Visible = true;
        tray.MouseUp += (_, e) => { if (e.Button is MouseButtons.Left or MouseButtons.Right) menu.Open(Cursor.Position, MenuItems(), "Players"); };

        onEvent = OnEvent;
        // 60 Hz while MultiViewer players are open (edge arrows, drags, board dots); 2 Hz while waiting for them.
        tick.Tick += (_, _) =>
        {
            if (win.Count == 0 || ticks++ % 30 == 0) Scan();
            tick.Interval = win.Count == 0 ? 500 : 16;
            if (win.Count == 0) return;
            if (ticks % 60 == 0) N.DwmInvalidateIconicBitmaps(Handle); // preview refreshes when shown
            if (menuDue && ModifierKeys == Keys.None) { menuDue = false; menu.Open(Cursor.Position, MenuItems(), "Players"); }
            if (homeAt != 0 && Environment.TickCount64 >= homeAt) { homeAt = 0; Go(0, -1, 0, false); } // setup opened: board 1
            if (layoutDue && !busy) { layoutDue = false; Arrange(); }
            Back(); Hover(); Guard();
        };
        tick.Start();
        _ = Handle; // hotkeys and hooks need it before the window first shows
        ShowHidden();
        Hub.AtStart(this);
    }

    public Cfg Settings => cfg;

    int S(int px) => (int)Math.Round(px * DeviceDpi / 96f);     // px at 96 dpi -> px on this monitor
    bool full;                 // F11: whole monitor, over the taskbar

    // ---- Window frame: borderless, layered, fills its monitor's work area (F11: the whole monitor) ----

    // POPUP | MINIMIZEBOX | SYSMENU: the taskbar button minimizes and restores it, as in any app.
    // LAYERED: Windows keeps the window image itself (PaintBack), so the board never shows the screen behind it.
    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.Style = unchecked((int)0x80000000) | 0x20000 | 0x80000;
            p.ExStyle |= 0x80000;
            return p;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Areas();
        int on = 1; // taskbar preview drawn by Pinboard: the board on screen
        N.DwmSetWindowAttribute(Handle, 7, ref on, 4);  // DWMWA_FORCE_ICONIC_REPRESENTATION
        N.DwmSetWindowAttribute(Handle, 10, ref on, 4); // DWMWA_HAS_ICONIC_BITMAP
        hooks[0] = N.SetWinEventHook(0x16, 0x17, 0, onEvent, 0, 0, 0); // MINIMIZESTART..END
        hooks[1] = N.SetWinEventHook(0x03, 0x03, 0, onEvent, 0, 0, 0); // FOREGROUND
        hooks[2] = N.SetWinEventHook(0x0A, 0x0B, 0, onEvent, 0, 0, 0); // MOVESIZESTART..END
    }

    protected override void OnResize(EventArgs e) { base.OnResize(e); Areas(); }
    protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); Areas(); }

    // Board covers the work area (F11: the whole monitor); checked each scan, so taskbar or resolution changes follow.
    void Fill()
    {
        if (WindowState == FormWindowState.Minimized) return;
        var s = Screen.FromHandle(Handle);
        var want = full ? s.Bounds : s.WorkingArea;
        if (Bounds != want) Bounds = want;
    }

    // Home monitor follows the window; players follow on the next tick.
    void Areas()
    {
        if (!IsHandleCreated || WindowState == FormWindowState.Minimized) return;
        Monitors();
        content = scr;
        PaintBack();
        layoutDue = true;
        quiet = Environment.TickCount64 + 1000;
    }

    // Until then, a player off its place is still on its way there (Pinboard or MultiViewer moving it), not moved by the user.
    long quiet;

    // F11: the board covers its whole monitor (Windows hides the taskbar for it); again: back to the work area.
    // Pinboard holds F11 as a shortcut, so MultiViewer does not get the key and no player goes full screen with it.
    void FullScreen() { full = !full; Fill(); }

    void Monitors()
    {
        var home = Screen.FromHandle(Handle);
        scr = home.Bounds;
        others = Screen.AllScreens.Select(s => s.Bounds).Where(b => b != scr).ToArray();
        virt = others.Aggregate(scr, Rectangle.Union);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case 0x312: Shortcut((int)m.WParam); break;                   // WM_HOTKEY
            case 0x323: Thumb((int)((long)m.LParam >> 16) & 0xFFFF, (int)(long)m.LParam & 0xFFFF); return; // WM_DWMSENDICONICTHUMBNAIL
        }
        if (m.Msg == Program.HowTo) Front();
        if (m.Msg == Program.QuitMsg) Close(); // setup or uninstall asks
        base.WndProc(ref m);
    }

    // Window image: board colour only. Handed to Windows once per size change.
    void PaintBack()
    {
        if (!IsHandleCreated || Width < 1 || Height < 1) return;
        if (face == null || face.Size != Size) { face?.Dispose(); face = new(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb); }
        using (var g = Graphics.FromImage(face)) g.Clear(BackColor);
        Layer.Draw(Handle, face, Location);
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

    // Minimized boards hide their players, so restore first: players stay visible after exit.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
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
        // a player put on another monitor leaves the boards
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
            dots.Wake();
            Visible = true;
            Fill();
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
        if (win.Count > 0) Fill();
        if (was && win.Count == 0) Hide();
        Hotkeys(Mine(N.GetForegroundWindow()));
    }

    // New player: in a parking place joins that board; anywhere else joins the board on screen, where it is.
    void Sort(IntPtr h)
    {
        if (N.IsIconic(h)) return; // sorted when restored
        var r = R(h);
        int k = Zone(h);
        if (k >= 0 && k < cfg.Boards && k != cur) { r.Offset(-Park(k), 0); at[h] = new(k, Frac(r)); return; }
        if (k >= 0) r.Offset(-Park(k), 0);
        else if (k == int.MinValue) r.X = OnHome(r.Left, r.Right);
        var t = r;
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
            var t = Px(p.F); // only a move counts: MultiViewer itself adjusts sizes (aspect ratio, minimum size)
            if (!layoutDue && Environment.TickCount64 > quiet && (Math.Abs(r.X - t.X) > 8 || Math.Abs(r.Y - t.Y) > 8)) at[h] = p with { F = Frac(r) };
            return;
        }
        if (Full(h)) { Conceal(h); return; } // made full screen while parked: MultiViewer pulls it on screen
        if (home) { at[h] = new(cur, Frac(r)); return; } // brought on screen (MultiViewer or user): it joins this board
        var to = Px(p.F); to.Offset(Park(p.Board), 0);
        if (r.X != to.X || r.Y != to.Y) N.SetWindowPos(h, 0, to.X, to.Y, 0, 0, MoveOnly);
    }

    // Every player to its place on the home monitor (it changes only with resolution or monitor).
    void Arrange()
    {
        if (content.Width < 50 || content.Height < 50 || WindowState == FormWindowState.Minimized) return;
        foreach (var (h, p) in at.ToList())
        {
            if (hid.Contains(h) || N.IsIconic(h) || Full(h) || N.IsZoomed(h)) continue;
            var t = Px(p.F);
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

    // MultiViewer's own main window (home, videos, a session: where players are picked) never goes on a board.
    // It has the same window style as timing windows, so titles tell them apart: a window with a system menu joins only
    // with a timing window title. The main window is remembered once seen on one of its pages, whatever page it shows later.
    bool MvMain(IntPtr h)
    {
        if ((N.GetWindowLongPtr(h, -16) & 0x80000) == 0) return false; // players and overlays: no system menu
        if (!N.IsWindow(mvMain)) mvMain = 0;
        var t = N.Title(h);
        if (mainTitles.IsMatch(t)) mvMain = h;
        return h == mvMain || !timingTitles.IsMatch(t);
    }

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
        var p = at.TryGetValue(h, out var q) ? q : new(cur, Frac(R(h)));
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
        bool slide = animate && cfg.SlideMs > 0 && Visible && Geo.CanSlide(scr, others);
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
            dots.Wake(); // board dots and name show the new board
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
                    case 0x0B: // dropped on the board: it takes its new place, as it is
                        drag = 0;
                        if (Zone(h) != -1 || Elsewhere(h)) break;
                        at[h] = new(cur, Frac(R(h)));
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
        // Board dots and name: top centre. They wake on a board change or when the cursor touches the top edge above them;
        // with a player under them they fade out after ArrowHideMs, with nothing under them they stay.
        dots.Set(cfg.Boards, cur, cfg.Name(cur), DeviceDpi / 96f);
        var box = new Rectangle(new(Left + (Width - dots.Image.Width) / 2, Top + S(12)), dots.Image);
        if (ours && moved && p.Y <= Top + 1 && p.X >= box.Left && p.X < box.Right) dots.Wake();
        bool covered = win.Any(h => OnBoard(h) && !hid.Contains(h) && R(h).IntersectsWith(box));
        dots.Tick(ours, box.Location, covered, p, cfg.ArrowHideMs);
    }
}
