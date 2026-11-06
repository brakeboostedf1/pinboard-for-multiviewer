// Pinboard for MultiViewer: MultiViewer windows on boards; one board on screen at a time, the others parked off screen.
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)] // never load a DLL from the exe's folder

static class Program
{
    public const string Name = "Pinboard for MultiViewer";
    public static readonly uint HowTo = N.RegisterWindowMessage("Pinboard.Front"), QuitMsg = N.RegisterWindowMessage("Pinboard.Quit");
    // Per-user folder: settings, log, How-to choice. Nothing personal goes in the install folder.
    public static readonly string Data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);

    [STAThread]
    static int Main(string[] args)
    {
        N.SetDefaultDllDirectories(0x800); // LOAD_LIBRARY_SEARCH_SYSTEM32: setup runs as admin from Downloads
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        if (args.Contains("--uninstall")) return Setup.Remove(args);
        if (args.Contains("--install")) return Setup.Packaged() ? Setup.FromArgs(args) : 1;
        if (Setup.IsSetup(args)) return Setup.Packaged() ? Setup.Show() : 1;
        Directory.CreateDirectory(Data);
        if (args.Contains("--selftest")) return Geo.SelfTest(Path.Combine(Data, "selftest.txt"));
        using var one = new Mutex(true, "MVPinboard", out bool first);
        if (!first) { N.AllowSetForegroundWindow(-1); N.PostMessage(0xFFFF, HowTo, 0, 0); return 0; } // already running: it comes to the front
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log(e.Exception);
        var cfg = Cfg.Load();
        Help.AtStart();
        Application.Run(new Board(cfg));
        return 0;
    }

    // Log starts again past 1 MB.
    public static void Log(Exception e)
    {
        var p = Path.Combine(Data, "pinboard.log");
        try
        {
            if (File.Exists(p) && new FileInfo(p).Length > 1 << 20) File.Delete(p);
            File.AppendAllText(p, $"{DateTime.Now:s} {e}\n\n");
        }
        catch (IOException) { }
    }
}

// Settings file: Data\settings.json, written with defaults on first start. Out-of-range values are pulled into range.
class Cfg
{
    public int Boards { get; set; } = 4;           // 1-4
    public int SlideMs { get; set; } = 350;        // slide time [ms]; 0 = no slide
    public string Back { get; set; } = "#101014";  // board colour
    public int ArrowHideMs { get; set; } = 2000;   // arrows hide after the cursor stops [ms]
    public int HoldMs { get; set; } = 300;         // hold a dragged window over an arrow this long to carry it [ms]
    public bool Protect { get; set; } = true;      // video players show black in screen captures; timing windows stay visible
    public string Monitor { get; set; } = "";      // monitor boards show on, e.g. \\.\DISPLAY2; empty = main monitor
    public string Skip { get; set; } = "^(Home|[0-9]{4} .+) . MultiViewer$"; // titles Pinboard leaves alone (MultiViewer main window)

    public static string File => Path.Combine(Program.Data, "settings.json");

    public static Cfg Load()
    {
        Cfg c, d = new();
        try
        {
            var o = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }; // "+" stays "+"
            if (!System.IO.File.Exists(File)) System.IO.File.WriteAllText(File, JsonSerializer.Serialize(d, o));
            c = JsonSerializer.Deserialize<Cfg>(System.IO.File.ReadAllText(File)) ?? d;
        }
        catch (Exception e) { MessageBox.Show($"settings.json not read, defaults used.\n{e.Message}", Program.Name); c = d; }
        c.Boards = Math.Clamp(c.Boards, 1, 4);
        c.SlideMs = Math.Clamp(c.SlideMs, 0, 2000);
        c.HoldMs = Math.Clamp(c.HoldMs, 100, 3000);
        c.ArrowHideMs = Math.Clamp(c.ArrowHideMs, 500, 10000);
        c.Monitor ??= "";
        try { if (ColorTranslator.FromHtml(c.Back).IsEmpty) c.Back = d.Back; } catch { c.Back = d.Back; }
        try { _ = new Regex(c.Skip); } catch { c.Skip = d.Skip; }
        return c;
    }
}

class Board : Form
{
    const uint MoveOnly = 0x15; // SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE
    readonly Cfg cfg;
    readonly Regex skip;
    Rectangle scr, area;            // home monitor: the one boards show on
    Rectangle virt;                 // all monitors together
    Rectangle[] others = [];        // every monitor but home
    HashSet<uint> mv = [];          // MultiViewer process ids
    readonly HashSet<IntPtr> win = [], pinned = [], restored = [], hid = [];
    readonly N.ITaskbarList bar = (N.ITaskbarList)new N.TaskbarList();
    readonly Arrow left = new(false), right = new(true);
    readonly Dictionary<IntPtr, Shield> shields = [];
    readonly Dictionary<IntPtr, Place> at = []; // every window: its board and on-screen place
    readonly NotifyIcon tray = new();
    readonly ContextMenuStrip menu = Theme.Menu();
    readonly System.Windows.Forms.Timer tick = new() { Interval = 16 };
    readonly N.WinEvent onEvent;
    readonly IntPtr[] hooks = new IntPtr[3];
    Point lastCursor;
    IntPtr drag;
    long holdFrom;
    int cur, ticks, holdSide;
    bool busy, listPending, keys;

    public Board(Cfg c)
    {
        cfg = c;
        skip = new(c.Skip, RegexOptions.None, TimeSpan.FromMilliseconds(50));
        Text = Program.Name;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        BackColor = ColorTranslator.FromHtml(c.Back);
        Monitors();
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        bar.HrInit();

        left.Go = () => Slide(-1);
        right.Go = () => Slide(1);

        menu.Opening += (_, _) => Fill();
        Fill();
        tray.Icon = Icon; tray.Text = Program.Name; tray.ContextMenuStrip = menu; tray.Visible = true;
        tray.DoubleClick += (_, _) => Help.Open();

        onEvent = OnEvent;
        // 60 Hz while MultiViewer windows are open (edge arrows, drags); 2 Hz while waiting for them.
        tick.Tick += (_, _) =>
        {
            if (win.Count == 0 || ticks++ % 30 == 0) Scan();
            tick.Interval = win.Count == 0 ? 500 : 16;
            if (win.Count == 0) return;
            if (ticks % 60 == 0) N.DwmInvalidateIconicBitmaps(Handle); // preview refreshes when shown
            if (listPending && ModifierKeys == Keys.None) { listPending = false; N.SetForegroundWindow(Handle); menu.Show(Cursor.Position); }
            Back(); Hover(); Guard();
        };
        tick.Start();
        _ = Handle; // hotkeys and hooks need it before the frame first shows
        ShowHidden();
    }

    // MINIMIZEBOX | SYSMENU: borderless forms need them to minimize from the taskbar.
    // LAYERED: Windows keeps the board colour itself (Paint), so the board never shows the screen behind it.
    protected override CreateParams CreateParams
    { get { var p = base.CreateParams; p.Style |= 0x20000 | 0x80000; p.ExStyle |= 0x80000; return p; } }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Paint();
        int on = 1; // taskbar preview drawn by Pinboard: the board on screen
        N.DwmSetWindowAttribute(Handle, 7, ref on, 4);  // DWMWA_FORCE_ICONIC_REPRESENTATION
        N.DwmSetWindowAttribute(Handle, 10, ref on, 4); // DWMWA_HAS_ICONIC_BITMAP
        hooks[0] = N.SetWinEventHook(0x16, 0x17, 0, onEvent, 0, 0, 0); // MINIMIZESTART..END
        hooks[1] = N.SetWinEventHook(0x03, 0x03, 0, onEvent, 0, 0, 0); // FOREGROUND
        hooks[2] = N.SetWinEventHook(0x0A, 0x0B, 0, onEvent, 0, 0, 0); // MOVESIZESTART..END
    }

    void Paint()
    {
        if (!IsHandleCreated || area.IsEmpty) return;
        using var b = new Bitmap(area.Width, area.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(b)) g.Clear(BackColor);
        Layer.Draw(Handle, b, area.Location);
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
        if (!on) { for (int i = 1; i <= 5; i++) N.UnregisterHotKey(Handle, i); return; }
        const uint C = 0x4000 | 0x2, CS = C | 0x4; // NOREPEAT | CTRL (| SHIFT)
        Key(1, C, 0x25); Key(2, C, 0x27); Key(3, C, 0x26); // Ctrl + ← → ↑
        Key(4, CS, 0x25); Key(5, CS, 0x27);                // Ctrl+Shift + ← →
    }

    // A shortcut another app already holds cannot register; log it so it can be found.
    void Key(int id, uint mods, uint vk)
    {
        if (!N.RegisterHotKey(Handle, id, mods, vk)) Program.Log(new($"Shortcut {id} (key 0x{vk:X}) not registered: in use by another app"));
    }

    protected override void SetVisibleCore(bool v) => base.SetVisibleCore(v && win.Count > 0);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x312) // WM_HOTKEY
            switch ((int)m.WParam)
            {
                case 1: Slide(-1); break;
                case 2: Slide(1); break;
                case 3: listPending = true; break; // menu opens once Ctrl is up
                case 4: Send(-1); break;
                case 5: Send(1); break;
            }
        if (m.Msg == Program.HowTo) Front();
        if (m.Msg == Program.QuitMsg) Close(); // setup or uninstall asks
        if (m.Msg == 0x323) { Thumb((int)((long)m.LParam >> 16) & 0xFFFF, (int)(long)m.LParam & 0xFFFF); return; } // WM_DWMSENDICONICTHUMBNAIL
        base.WndProc(ref m);
    }

    // Minimized boards hide their windows; restore first so every window comes back.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        base.OnFormClosing(e);
    }

    // Exit: every window back on screen, owner link and taskbar button restored.
    protected override void OnFormClosed(FormClosedEventArgs e)
    {
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

    // ---- Windows and boards ----
    // Board k not on screen parks at a fixed place: its windows sit Park(k) px right of where they show.
    // Pinboard remembers each window's board and on-screen place, and puts back any parked window that moved.

    record struct Place(int Board, int X, int Y);

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
        // main window can change title; a window put on another monitor leaves the boards
        foreach (var h in win.Where(h => skip.IsMatch(N.Title(h)) || Elsewhere(h)).ToList()) Release(h);
        var fresh = new List<IntPtr>();
        N.EnumWindows((h, _) =>
        {
            N.GetWindowThreadProcessId(h, out var p);
            if (!mv.Contains(p) || !N.IsWindowVisible(h) || win.Contains(h)) return true;
            var t = N.Title(h);
            if (t.Length == 0 || skip.IsMatch(t) || Elsewhere(h)) return true;
            win.Add(h);
            fresh.Add(h);
            N.SetWindowLongPtr(h, -8, Handle);
            if (!N.IsIconic(h)) bar.DeleteTab(h); // a minimized window keeps its button
            // Overlays (e.g. track map) have no title bar; their topmost flag cannot be trusted, Pinboard itself drops it.
            if ((N.GetWindowLongPtr(h, -20) & 0x8) != 0 || (N.GetWindowLongPtr(h, -16) & 0xC00000) == 0) pinned.Add(h);
            return true;
        }, 0);
        var used = win.Where(h => !N.IsIconic(h)).Select(Zone).Where(k => k >= 0 && k < cfg.Boards).ToHashSet();
        int free = Enumerable.Range(0, cfg.Boards).Where(k => !used.Contains(k)).DefaultIfEmpty(-1).First();
        if (!was && win.Count > 0)
        {
            // first windows: the board on screen is the first board whose parking place is empty
            cur = Math.Max(0, free);
            Visible = true;
        }
        // A MultiViewer setup opens windows on screen for the board that showed at save, the rest in their parking places.
        // A window landing in the parking place of the board on screen: the setup was saved on another board, so take an empty one.
        else if (free >= 0 && fresh.Any(h => !N.IsIconic(h) && Zone(h) == cur))
        {
            foreach (var (h, p) in at.Where(a => a.Value.Board == cur).ToList()) at[h] = p with { Board = free };
            cur = free;
        }
        foreach (var h in fresh) Sort(h);
        if (!busy && drag == 0) foreach (var h in win.ToList()) Keep(h);
        if (was && win.Count == 0) Hide();
        Hotkeys(Mine(N.GetForegroundWindow()));
    }

    // New window: on screen joins the board on screen; in a parking place joins that board.
    void Sort(IntPtr h)
    {
        if (N.IsIconic(h)) return; // sorted when restored
        N.GetWindowRect(h, out var r);
        int k = Zone(h);
        if (k >= 0 && k < cfg.Boards && k != cur) { at[h] = new(k, r.L - Park(k), r.T); return; }
        int x = k >= 0 && k < cfg.Boards ? r.L - Park(k) : OnHome(r.L, r.R);
        at[h] = new(cur, x, r.T);
        if (x != r.L) N.SetWindowPos(h, 0, x, r.T, 0, 0, MoveOnly);
    }

    // x that puts a window back on home, keeping its place within one screen width.
    int OnHome(int l, int r)
    {
        int cx = (l + r) / 2, W = scr.Width;
        int k = (int)Math.Floor((cx - scr.Left) / (double)W);
        return l - k * W;
    }

    // Repair: on-screen windows update their place; parked ones go back to theirs.
    void Keep(IntPtr h)
    {
        if (hid.Contains(h) || N.IsIconic(h) || !at.TryGetValue(h, out var p)) return;
        N.GetWindowRect(h, out var r);
        bool home = Zone(h) == -1;
        if (p.Board == cur) { if (home) at[h] = p with { X = r.L, Y = r.T }; else Sort(h); return; }
        if (Full(h)) { Conceal(h); return; } // made full screen while parked: MultiViewer pulls it on screen
        if (home) { at[h] = new(cur, r.L, r.T); return; } // brought on screen (MultiViewer or user): it joins this board
        int x = p.X + Park(p.Board);
        if (r.L != x || r.T != p.Y) N.SetWindowPos(h, 0, x, p.Y, 0, 0, MoveOnly);
    }

    void Release(IntPtr h)
    {
        if (hid.Remove(h)) { N.ShowWindow(h, 4); SaveHidden(); }
        win.Remove(h); pinned.Remove(h); at.Remove(h);
        N.SetWindowLongPtr(h, -8, 0);
        bar.AddTab(h);
    }

    // Home monitor read again on every scan: a monitor plugged in, removed or rescaled moves the boards with it.
    void Monitors()
    {
        var home = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == cfg.Monitor) ?? Screen.PrimaryScreen!;
        if (home.Bounds != scr || home.WorkingArea != area)
        {
            (scr, area) = (home.Bounds, home.WorkingArea);
            Bounds = area; // full screen would hide the taskbar
            Paint();
        }
        others = Screen.AllScreens.Select(s => s.Bounds).Where(b => b != scr).ToArray();
        virt = others.Aggregate(scr, Rectangle.Union);
    }

    static Point Center(IntPtr h) { N.GetWindowRect(h, out var r); return new((r.L + r.R) / 2, (r.T + r.B) / 2); }

    // Centre on a monitor other than home: the user keeps it there, outside the boards. Parked windows never count.
    bool Elsewhere(IntPtr h) =>
        !N.IsIconic(h) && !(at.TryGetValue(h, out var p) && p.Board != cur) && others.Any(o => o.Contains(Center(h)));

    int Zone(IntPtr h) => Geo.Region(Center(h).X, scr, virt);

    int Park(int k) => Geo.Park(k, scr, virt);

    bool OnBoard(IntPtr h) => at.TryGetValue(h, out var p) && p.Board == cur && !N.IsIconic(h);

    // Full screen: covers the home monitor, no title bar. MultiViewer puts such a window back on screen
    // when it is moved, so while its board is away it hides instead of parking.
    bool Full(IntPtr h)
    {
        N.GetWindowRect(h, out var r);
        return (N.GetWindowLongPtr(h, -16) & 0xC00000) == 0 && r.L <= scr.Left && r.T <= scr.Top && r.R >= scr.Right && r.B >= scr.Bottom;
    }

    void Conceal(IntPtr h) { N.ShowWindow(h, 0); hid.Add(h); SaveHidden(); }   // SW_HIDE
    void Reveal(IntPtr h) { N.ShowWindow(h, 4); hid.Remove(h); SaveHidden(); }  // SW_SHOWNOACTIVATE

    // Hidden windows are written down: after a crash the next start shows them again.
    static string HidFile => Path.Combine(Program.Data, "hidden.txt");

    void SaveHidden()
    {
        try { if (hid.Count == 0) File.Delete(HidFile); else File.WriteAllLines(HidFile, hid.Select(h => ((long)h).ToString())); }
        catch (IOException) { }
    }

    void ShowHidden()
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

    // Put a window on the board on screen at its remembered place.
    void ToScreen(IntPtr h)
    {
        if (N.IsIconic(h)) return;
        N.GetWindowRect(h, out var r);
        var p = at.TryGetValue(h, out var q) ? q : new(cur, OnHome(r.L, r.R), r.T);
        at[h] = p with { Board = cur };
        N.SetWindowPos(h, 0, p.X, p.Y, 0, 0, MoveOnly);
    }

    int Wrap(int b) => ((b % cfg.Boards) + cfg.Boards) % cfg.Boards;

    // One board left (dir -1) or right (+1); past the last board comes the first.
    // Only the board leaving and the board arriving move; the rest stay parked.
    // keep: window that stays under the cursor and so lands on the new board.
    void Slide(int dir, IntPtr keep = 0)
    {
        if (busy || cfg.Boards < 2) return;
        busy = true;
        N.timeBeginPeriod(1); // 15 ms frames need a fine system timer, only while sliding
        int to = Wrap(cur + dir), W = scr.Width;
        var leaving = at.Where(a => a.Value.Board == cur && a.Key != keep && !N.IsIconic(a.Key)).ToArray();
        foreach (var (h, _) in leaving.Where(a => Full(a.Key))) Conceal(h);
        var from = leaving.Where(a => !hid.Contains(a.Key)).ToArray();
        var into = at.Where(a => a.Value.Board == to && !N.IsIconic(a.Key) && !hid.Contains(a.Key)).ToArray();
        // A monitor beside home would show the windows passing over it: then the board swaps without a slide.
        bool slide = cfg.SlideMs > 0 && Geo.CanSlide(scr, others);
        if (slide) foreach (var (h, p) in into) N.SetWindowPos(h, 0, p.X + dir * W, p.Y, 0, 0, MoveOnly); // line up beside the screen
        var sw = Stopwatch.StartNew();
        for (double t = slide ? 0 : 1; t < 1;)
        {
            t = Math.Min(1, sw.Elapsed.TotalMilliseconds / cfg.SlideMs);
            double e = t < .5 ? 4 * t * t * t : 1 - Math.Pow(2 - 2 * t, 3) / 2; // ease in-out cubic
            int dx = (int)Math.Round(-e * dir * W);
            var d = N.BeginDeferWindowPos(from.Length + into.Length);
            foreach (var (h, p) in from) if (d != 0) d = N.DeferWindowPos(d, h, 0, p.X + dx, p.Y, 0, 0, MoveOnly);
            foreach (var (h, p) in into) if (d != 0) d = N.DeferWindowPos(d, h, 0, p.X + dir * W + dx, p.Y, 0, 0, MoveOnly);
            if (d != 0) N.EndDeferWindowPos(d);
            Guard(); // shields ride along
            Thread.Sleep(15);
        }
        // MultiViewer answers fast moves late and can land a window a few px off; place every moved window once more.
        if (slide) Thread.Sleep(120);
        foreach (var (h, p) in from) N.SetWindowPos(h, 0, p.X + Park(cur), p.Y, 0, 0, MoveOnly);
        foreach (var (h, p) in into) N.SetWindowPos(h, 0, p.X, p.Y, 0, 0, MoveOnly);
        foreach (var h in hid.Where(h => at.TryGetValue(h, out var p) && p.Board == to).ToList()) Reveal(h);
        if (keep != 0 && at.TryGetValue(keep, out var kp)) at[keep] = kp with { Board = to };
        cur = to;
        Raise();
        N.timeEndPeriod(1);
        busy = false;
    }

    // Window in front goes to the board left (dir -1) or right (+1); the board on screen stays.
    void Send(int dir)
    {
        var h = N.GetForegroundWindow();
        if (busy || cfg.Boards < 2 || !OnBoard(h)) return;
        int to = Wrap(cur + dir);
        N.GetWindowRect(h, out var r);
        bool full = Full(h);
        at[h] = full ? at[h] with { Board = to } : new(to, r.L, r.T);
        if (full) Conceal(h); else N.SetWindowPos(h, 0, r.L + Park(to), r.T, 0, 0, MoveOnly);
        N.SetForegroundWindow(Handle); // shortcuts keep working
    }

    // Windows of the board on screen sit above every other board's windows, in their own order.
    void Raise()
    {
        var order = new List<IntPtr>();
        N.EnumWindows((h, _) => { if (win.Contains(h) && OnBoard(h)) order.Add(h); return true; }, 0);
        order.Reverse(); // bottom first, so the top one ends on top
        foreach (var h in order) N.SetWindowPos(h, 0, 0, 0, 0, 0, 0x13); // HWND_TOP; NOSIZE | NOMOVE | NOACTIVATE
        Pin(N.GetForegroundWindow());
    }

    // Started again while running: bring the boards to the front, as apps do.
    void Front()
    {
        if (win.Count == 0) { tray.ShowBalloonTip(3000, Program.Name, "Open a setup or stream in MultiViewer.", ToolTipIcon.None); return; }
        WindowState = FormWindowState.Normal;
        N.SetForegroundWindow(Handle);
        Raise();
    }

    // A restored window comes back on the board on screen and gives up its taskbar button.
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
                    case 0x0B: // dropped: on screen it takes its new place on the board on screen
                        drag = 0;
                        N.GetWindowRect(h, out var r);
                        if (Zone(h) == -1) at[h] = new(cur, r.L, r.T);
                        break;
                }
        }
        catch (Exception e) { Program.Log(e); }
    }

    bool Ours(IntPtr fg) => fg == Handle || win.Contains(fg) || fg == left.Handle || fg == right.Handle;

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

    // ---- Menu: every window with its board; click one to bring it here ----

    void Fill()
    {
        foreach (var i in menu.Items.Cast<ToolStripItem>().ToList()) i.Dispose();
        menu.Items.Clear();
        menu.Items.Add(Theme.Header("Players"));
        var list = win.Select(h => (h, k: N.IsIconic(h) || !at.TryGetValue(h, out var p) ? int.MaxValue : p.Board))
            .OrderBy(w => w.k).ThenBy(w => N.Title(w.h));
        foreach (var (h, k) in list)
            menu.Items.Add(Theme.Item(Short(N.Title(h)), k == int.MaxValue ? "Minimized" : $"Board {k + 1}", () => Bring(h)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Theme.Item("Previous board", "Ctrl+←", () => Slide(-1)));
        menu.Items.Add(Theme.Item("Next board", "Ctrl+→", () => Slide(1)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Theme.Item("How to use", "", Help.Open));
        menu.Items.Add(Theme.Item("Settings", "", () => Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Cfg.File}\"") { UseShellExecute = true })));
        menu.Items.Add(Theme.Item("Exit", "", Close));
    }

    static string Short(string title) => Regex.Replace(title, " . MultiViewer$", "");

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

    // Every video player gets a shield the size of the player, owned by it so Windows keeps the shield
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
            N.GetWindowRect(h, out var r);
            s.Cover(Rectangle.FromLTRB(r.L, r.T, r.R, r.B));
        }
    }

    // ---- Taskbar preview: the board on screen ----

    // MultiViewer players: caption (no caption in full screen), no system menu. Timing windows have a system menu; track map has no caption.
    bool IsPlayer(IntPtr h)
    {
        long s = N.GetWindowLongPtr(h, -16);
        return (s & 0x80000) == 0 && ((s & 0xC00000) == 0xC00000 || Full(h));
    }

    void Thumb(int maxW, int maxH)
    {
        using var shot = new Bitmap(area.Width, area.Height);
        using (var g = Graphics.FromImage(shot))
        {
            g.Clear(BackColor);
            var order = new List<IntPtr>();
            N.EnumWindows((h, _) => { if (win.Contains(h)) order.Add(h); return true; }, 0);
            order.Reverse(); // bottom of z-order first
            foreach (var h in order.Where(h => OnBoard(h) && !hid.Contains(h)))
            {
                N.GetWindowRect(h, out var r);
                var to = new Rectangle(r.L - area.X, r.T - area.Y, r.R - r.L, r.B - r.T);
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

    // ---- Edge arrows ----

    // Arrows show when the cursor moves at a screen edge, and hide after it stops.
    // While a window is dragged they stay up; hold the window over one for HoldMs and it rides to that board.
    void Hover()
    {
        var p = Cursor.Position;
        bool moved = p != lastCursor;
        lastCursor = p;
        int y = area.Y + area.Height / 2 - Arrow.H / 2;
        Point lp = new(scr.Left, y), rp = new(scr.Right - Arrow.W, y);
        bool can = cfg.Boards > 1; // boards loop
        if (drag != 0)
        {
            left.Hold(can, lp); right.Hold(can, rp);
            int side = can && left.Box.Contains(p) ? -1 : can && right.Box.Contains(p) ? 1 : 0;
            if (side != holdSide) { holdSide = side; holdFrom = Environment.TickCount64; }
            else if (side != 0 && Environment.TickCount64 - holdFrom >= cfg.HoldMs) { Slide(side, drag); holdFrom = Environment.TickCount64; }
            return;
        }
        bool ours = WindowState != FormWindowState.Minimized && Ours(N.GetForegroundWindow());
        left.Track(ours && can, lp, p, moved, p.X <= scr.Left + 3, cfg.ArrowHideMs);
        right.Track(ours && can, rp, p, moved, p.X >= scr.Right - 4, cfg.ArrowHideMs);
    }
}

// Board geometry, pure so it can be tested without real monitors.
// Board k parks (k + 1) home-widths right of home, past every monitor; on one monitor that is plain (k + 1) * width.
static class Geo
{
    public const int Gap = 64; // clear space past the last monitor so window edges never reach it [px]

    static int Extra(Rectangle home, Rectangle virt) => virt.Right > home.Right ? virt.Right - home.Right + Gap : 0;

    // x shift from on-screen place to parking place of board k.
    public static int Park(int k, Rectangle home, Rectangle virt) => Extra(home, virt) + (k + 1) * home.Width;

    // -1: centre on home; k >= 0: in parking place of board k; int.MinValue: anywhere else.
    public static int Region(int cx, Rectangle home, Rectangle virt)
    {
        if (cx >= home.Left && cx < home.Right) return -1;
        int k = (int)Math.Floor((cx - home.Left - Extra(home, virt)) / (double)home.Width) - 1;
        return cx >= home.Right && k >= 0 ? k : int.MinValue;
    }

    // Slide only when no monitor sits beside home: windows would pass over it.
    public static bool CanSlide(Rectangle home, Rectangle[] others) => !others.Any(o => o.Top < home.Bottom && o.Bottom > home.Top);

    // Checks every rule on made-up monitor layouts; writes a report, returns failures.
    public static int SelfTest(string report)
    {
        var layouts = new (string Name, Rectangle Home, Rectangle[] Others, bool Slide)[]
        {
            ("one monitor 1920", new(0, 0, 1920, 1080), [], true),
            ("one monitor 4K", new(0, 0, 3840, 2160), [], true),
            ("second monitor right", new(0, 0, 1920, 1080), [new(1920, 0, 2560, 1440)], false),
            ("second monitor left", new(0, 0, 1920, 1080), [new(-1920, 0, 1920, 1080)], false),
            ("second monitor above, offset", new(0, 0, 1920, 1080), [new(500, -1080, 1920, 1080)], true),
            ("left + right of 1440p home", new(0, 0, 2560, 1440), [new(-1920, 200, 1920, 1080), new(2560, 0, 1920, 1080)], false),
            ("home is the right one", new(1920, 0, 1920, 1080), [new(0, 0, 1920, 1080)], false),
        };
        // Window rects relative to home: MultiViewer windows reach 8 px past their board (invisible border).
        Rectangle[] wins = [new(-8, -2, 1260, 709), new(1238, 0, 690, 1040), new(500, 300, 400, 300), new(-8, -8, 1936, 1048)];
        var lines = new List<string>();
        int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; lines.Add($"{(ok ? "PASS" : "FAIL")}  {what}"); }
        foreach (var (name, home, others, slide) in layouts)
        {
            lines.Add($"-- {name}");
            var virt = others.Aggregate(home, Rectangle.Union);
            var mons = others.Append(home).ToArray();
            Check(CanSlide(home, others) == slide, $"slide {(slide ? "on" : "off")}");
            foreach (var w0 in wins)
            {
                var w = w0 with { X = w0.X + home.X, Y = w0.Y + home.Y };
                int cx = w.X + w.Width / 2;
                Check(Region(cx, home, virt) == -1, $"window {w0} on screen reads as on screen");
                for (int k = 0; k <= 3; k++)
                {
                    var h = w with { X = w.X + Park(k, home, virt) };
                    int hcx = h.X + h.Width / 2;
                    int seen = mons.Max(m => Rectangle.Intersect(m, h).Width);
                    Check(Region(hcx, home, virt) == k, $"  board {k + 1} parked: reads back as board {k + 1}");
                    Check(seen <= 8, $"  board {k + 1} parked: at most 8 px on any monitor (got {seen})");
                    Check(h.Right < 32000, $"  board {k + 1} parked: inside Windows' coordinate limit");
                }
            }
        }
        lines.Insert(0, $"Pinboard self-test: {fails} failed");
        File.WriteAllLines(report, lines);
        return fails;
    }
}

// MultiViewer's look: MUI dark theme, Segoe UI (first font of its stack on Windows), 14 / 12 px text.
static class Theme
{
    public static readonly Color Window = Hex("#111111"), Title = Hex("#202020"), Panel = Hex("#232323"), Hover = Hex("#333333"),
        Line = Hex("#3a3a3a"), Text = Color.White, Soft = Hex("#b3b3b3"), Muted = Hex("#8a8a8a"), Red = Hex("#e10600");
    public static readonly Font Body = new("Segoe UI", 10.5f), Small = new("Segoe UI", 9f), Strong = new("Segoe UI Semibold", 10.5f), Big = new("Segoe UI Semibold", 15f);

    static Color Hex(string h) => ColorTranslator.FromHtml(h);

    public static ContextMenuStrip Menu() => new()
    {
        Renderer = new R(), ShowImageMargin = false, ShowCheckMargin = false,
        BackColor = Panel, ForeColor = Text, Font = Body, Padding = new(0, 6, 0, 6),
    };

    public static ToolStripMenuItem Item(string text, string right, Action go) =>
        new(text, null, (_, _) => go()) { ShortcutKeyDisplayString = right, ForeColor = Text, Padding = new(6, 5, 6, 5) };

    public static ToolStripLabel Header(string text) => new(text) { ForeColor = Muted, Font = Small, Padding = new(10, 4, 0, 2) };

    // Dark title bar in MultiViewer's title colour.
    public static void DarkTitle(Form f)
    {
        int on = 1, cap = Title.R | Title.G << 8 | Title.B << 16, txt = 0xFFFFFF;
        N.DwmSetWindowAttribute(f.Handle, 20, ref on, 4);  // DWMWA_USE_IMMERSIVE_DARK_MODE
        N.DwmSetWindowAttribute(f.Handle, 35, ref cap, 4); // DWMWA_CAPTION_COLOR
        N.DwmSetWindowAttribute(f.Handle, 36, ref txt, 4); // DWMWA_TEXT_COLOR
    }

    class R() : ToolStripProfessionalRenderer(new C())
    {
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            bool key = e.Item is ToolStripMenuItem m && e.Text == m.ShortcutKeyDisplayString;
            e.TextColor = key || !e.Item.Enabled ? Muted : e.Item.ForeColor;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            using var b = new SolidBrush(e.Item.Selected && e.Item.Enabled ? Hover : Panel);
            e.Graphics.FillRectangle(b, new Rectangle(Point.Empty, e.Item.Size));
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var p = new Pen(Line);
            int y = e.Item.Height / 2;
            e.Graphics.DrawLine(p, 0, y, e.Item.Width, y);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var p = new Pen(Line);
            e.Graphics.DrawRectangle(p, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }
    }

    class C : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Panel;
        public override Color ImageMarginGradientBegin => Panel;
        public override Color ImageMarginGradientMiddle => Panel;
        public override Color ImageMarginGradientEnd => Panel;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
    }
}

// How to use card in MultiViewer's look: opens at start unless ticked off, any time from the menu.
static class Help
{
    static readonly (string, (string, string)[])[] Rows =
    [
        ("How to use", [
            ("1  Start", "Open Pinboard and MultiViewer. Pinboard waits in system tray. It shows in taskbar when your setup opens."),
            ("2  Change board", "Click arrow at left or right screen edge. Up to 4 boards."),
            ("3  Move player", "Drag player onto arrow and hold. Or press Ctrl+Shift+← / →: player in front goes to previous / next board."),
            ("4  Save", "Arrange boards, then save setup in MultiViewer. Setup opens with each player on its board."),
            ("5  Find player", "Player lost or minimized: press Ctrl+↑, or right-click tray icon. Click player: it comes back on this board."),
        ]),
        ("Shortcuts (Pinboard or MultiViewer in front)", [
            ("Ctrl+← / →", "Previous / next board"),
            ("Ctrl+Shift+← / →", "Send player to previous / next board"),
            ("Ctrl+↑", "Player list and menu"),
        ]),
        ("Settings", [
            ("MultiViewer", "No settings needed."),
            ("Windows", "Player snaps to half screen at arrow: turn off Settings > System > Multitasking > Snap windows."),
            ("Pinboard", "Tray icon > Settings. Restart Pinboard after a change."),
        ]),
    ];

    static Form? card;

    // Marked when the user ticks "Don't show at start". Menu > How to use opens the card any time.
    static readonly string HideMark = Path.Combine(Program.Data, "help-hidden");

    public static void AtStart() { if (!File.Exists(HideMark)) Open(); }

    public static void Open()
    {
        if (card is { IsDisposed: false }) { card.Activate(); return; }
        card = new Form
        {
            Text = "How to use", FormBorderStyle = FormBorderStyle.FixedSingle, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterScreen, BackColor = Theme.Window, ForeColor = Theme.Text,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new(24, 16, 24, 20), TopMost = true,
            KeyPreview = true, ShowInTaskbar = false, Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath),
        };
        var t = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, BackColor = Theme.Window };
        foreach (var (head, rows) in Rows)
        {
            t.Controls.Add(new Label { Text = head, Font = Theme.Big, ForeColor = Theme.Text, AutoSize = true, Margin = new(0, 12, 0, 6) });
            t.SetColumnSpan(t.Controls[^1], 2);
            foreach (var (k, v) in rows)
            {
                t.Controls.Add(new Label { Text = k, Font = Theme.Strong, ForeColor = Theme.Text, AutoSize = true, Margin = new(0, 4, 24, 4) });
                t.Controls.Add(new Label { Text = v, Font = Theme.Body, ForeColor = Theme.Soft, AutoSize = true, MaximumSize = new(560, 0), Margin = new(0, 4, 0, 4) });
            }
        }
        // Bottom row: start-up choice left, Close right.
        var hide = new CheckBox
        {
            Text = "Don't show at start", Checked = File.Exists(HideMark), AutoSize = true, Font = Theme.Body,
            ForeColor = Theme.Soft, FlatStyle = FlatStyle.Flat, Margin = new(0, 18, 24, 0), Anchor = AnchorStyles.Left,
        };
        hide.CheckedChanged += (_, _) => { if (hide.Checked) File.WriteAllText(HideMark, ""); else File.Delete(HideMark); };
        var close = new Button
        {
            Text = "Close", AutoSize = true, Font = Theme.Strong, ForeColor = Theme.Text, BackColor = Theme.Red,
            FlatStyle = FlatStyle.Flat, Padding = new(14, 4, 14, 4), Margin = new(0, 14, 0, 0), Anchor = AnchorStyles.Right,
        };
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => card.Close();
        t.Controls.Add(hide);
        t.Controls.Add(close);
        card.AcceptButton = close;
        card.Controls.Add(t);
        card.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) card.Close(); };
        card.HandleCreated += (_, _) => Theme.DarkTitle(card);
        card.FormClosed += (_, _) => card.Icon?.Dispose();
        card.Show();
    }
}

// Topmost window drawn with per-pixel alpha; never takes focus.
class Layer : Form
{
    public bool On { get; private set; }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams // POPUP; LAYERED | TOPMOST | TOOLWINDOW | NOACTIVATE
    {
        get
        {
            var p = base.CreateParams;
            p.Style = unchecked((int)0x80000000);
            p.ExStyle |= 0x80000 | 0x8 | 0x80 | 0x08000000;
            return p;
        }
    }

    // Only Pinboard's own exit closes a layer; outside close requests are ignored.
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        e.Cancel = e.CloseReason != CloseReason.ApplicationExitCall;
        base.OnFormClosing(e);
    }

    public void Put(Bitmap b, Point at)
    {
        Draw(Handle, b, at);
        if (!On) { N.ShowWindow(Handle, 4); On = true; } // SW_SHOWNOACTIVATE
    }

    // Hands a bitmap to a layered window; Windows keeps and shows it without asking the window to paint.
    public static void Draw(IntPtr h, Bitmap b, Point at)
    {
        var scr = N.GetDC(0);
        var mem = N.CreateCompatibleDC(scr);
        var hb = b.GetHbitmap(Color.FromArgb(0));
        var old = N.SelectObject(mem, hb);
        var size = new Size(b.Width, b.Height);
        var src = Point.Empty;
        var blend = new N.Blend { alpha = 255, format = 1 }; // AC_SRC_ALPHA
        N.UpdateLayeredWindow(h, scr, ref at, ref size, mem, ref src, 0, ref blend, 2);
        N.SelectObject(mem, old); N.DeleteObject(hb); N.DeleteDC(mem); N.ReleaseDC(0, scr);
    }

    public void Off() { if (On) { N.ShowWindow(Handle, 0); On = false; } }
}

// Near-clear, click-through window over a video player, shown on the monitor only (WDA_MONITOR):
// captures of the screen get black in its place. Owned by the player, so it stays right above it,
// hides with it and drops behind whatever covers the player.
class Shield : Form
{
    bool on;
    Rectangle at;

    public Shield(IntPtr player)
    {
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black; Opacity = 0.01; // 1 %: no visible change on screen
        _ = Handle;
        N.SetWindowLongPtr(Handle, -8, player); // owner = player
        N.SetWindowDisplayAffinity(Handle, 1);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams // LAYERED | TRANSPARENT (click-through) | TOOLWINDOW | NOACTIVATE
    { get { var p = base.CreateParams; p.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000; return p; } }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        e.Cancel = e.CloseReason != CloseReason.ApplicationExitCall;
        base.OnFormClosing(e);
    }

    public void Cover(Rectangle r)
    {
        if (on && r == at) return;
        at = r;
        N.SetWindowPos(Handle, 0, r.X, r.Y, r.Width, r.Height, 0x14 | (on ? 0u : 0x40)); // NOZORDER | NOACTIVATE (| SHOWWINDOW)
        on = true;
    }

    public void Off() { if (on) { N.ShowWindow(Handle, 0); on = false; } }
}

// Edge arrow: clear box, every pixel of it clickable, white chevron with dark edge.
class Arrow : Layer
{
    public const int W = 56, H = 160;
    public Action? Go;
    readonly Bitmap bmp = new(W, H, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
    long seen;
    Point at;

    public Arrow(bool right)
    {
        Cursor = Cursors.Hand;
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(1, 0, 0, 0)); // alpha 1: looks clear, still takes clicks
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int cx = W / 2, cy = H / 2, d = right ? 1 : -1;
            Point[] v = [new(cx - 8 * d, cy - 20), new(cx + 8 * d, cy), new(cx - 8 * d, cy + 20)];
            using var edge = new Pen(Color.FromArgb(150, 0, 0, 0), 9) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var face = new Pen(Color.White, 5) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(edge, v);
            g.DrawLines(face, v);
        }
        MouseDown += (_, _) => { seen = Environment.TickCount64; Go?.Invoke(); };
    }

    public Rectangle Box => new(at, new(W, H));

    // Shown without the hide timer (during a drag).
    public void Hold(bool can, Point pos)
    {
        if (!can) Off();
        else if (!On || pos != at) { at = pos; Put(bmp, pos); }
    }

    public void Track(bool can, Point pos, Point cursor, bool moved, bool atEdge, int hideMs)
    {
        bool inside = atEdge || new Rectangle(pos, new(W, H)).Contains(cursor);
        if (inside && moved) seen = Environment.TickCount64;
        bool show = can && inside && Environment.TickCount64 - seen < hideMs;
        if (show && (!On || pos != at)) { at = pos; Put(bmp, pos); }
        else if (!show) Off();
    }
}

static class N
{
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    public delegate void WinEvent(IntPtr hk, uint ev, IntPtr h, int o, int c, uint th, uint t);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] public struct Blend { public byte op, flags, alpha, format; }

    [ComImport, Guid("56FDF342-FD6D-11d0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITaskbarList { void HrInit(); void AddTab(IntPtr h); void DeleteTab(IntPtr h); void ActivateTab(IntPtr h); void SetActiveAlt(IntPtr h); }
    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")] public class TaskbarList;

    public static string Title(IntPtr h)
    {
        var s = new System.Text.StringBuilder(256);
        GetWindowText(h, s, 256);
        return s.ToString();
    }

    const string U = "user32.dll", G = "gdi32.dll", D = "dwmapi.dll";
    [DllImport(U, CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
    [DllImport(U)] public static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport(U)] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
    [DllImport(U)] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport(U)] public static extern bool IsWindow(IntPtr h);
    [DllImport(U)] public static extern bool IsIconic(IntPtr h);
    [DllImport(U)] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport(U)] public static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport(U)] public static extern long GetWindowLongPtr(IntPtr h, int i);
    [DllImport(U)] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int c, uint f);
    [DllImport(U)] public static extern IntPtr BeginDeferWindowPos(int n);
    [DllImport(U)] public static extern IntPtr DeferWindowPos(IntPtr d, IntPtr h, IntPtr a, int x, int y, int w, int c, uint f);
    [DllImport(U)] public static extern bool EndDeferWindowPos(IntPtr d);
    [DllImport(U)] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport(U)] public static extern IntPtr GetForegroundWindow();
    [DllImport(U)] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport(U)] public static extern bool AllowSetForegroundWindow(int pid);
    [DllImport(U)] public static extern bool RegisterHotKey(IntPtr h, int id, uint m, uint vk);
    [DllImport(U)] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport(U)] public static extern IntPtr SetWinEventHook(uint a, uint b, IntPtr m, WinEvent f, uint p, uint t, uint fl);
    [DllImport(U)] public static extern bool UnhookWinEvent(IntPtr h);
    [DllImport(U)] public static extern bool SetWindowDisplayAffinity(IntPtr h, uint a);
    [DllImport(U)] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
    [DllImport(U, CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string s);
    [DllImport(U)] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport(U)] public static extern IntPtr GetDC(IntPtr h);
    [DllImport(U)] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport(U)] public static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref Point at, ref Size sz, IntPtr src, ref Point srcAt, int key, ref Blend b, int flags);
    [DllImport(G)] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport(G)] public static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport(G)] public static extern bool DeleteObject(IntPtr o);
    [DllImport(G)] public static extern bool DeleteDC(IntPtr dc);
    [DllImport(D)] public static extern int DwmSetWindowAttribute(IntPtr h, int a, ref int v, int size);
    [DllImport(D)] public static extern int DwmSetIconicThumbnail(IntPtr h, IntPtr bmp, uint f);
    [DllImport(D)] public static extern int DwmInvalidateIconicBitmaps(IntPtr h);
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint p);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint p);
    [DllImport("kernel32.dll")] public static extern bool SetDefaultDllDirectories(uint f);
}
