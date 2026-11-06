// How to use and Settings in one window, laid out like MultiViewer's settings: sidebar, header, cards of rows.
// Every change applies at once and is saved; there is no Save button.
using System.Drawing.Drawing2D;

class Hub : Form
{
    static Hub? open;
    static readonly string HideMark = Path.Combine(Program.Data, "help-hidden"); // "Don't show at start" on

    public static void AtStart(Board b) { if (!File.Exists(HideMark)) Open(b, 0); }

    public static void Open(Board b, int section)
    {
        if (open is { IsDisposed: false })
        {
            if (open.WindowState == FormWindowState.Minimized) open.WindowState = FormWindowState.Normal;
            open.Select(section); open.Activate(); return;
        }
        open = new Hub(b);
        open.Select(section);
        open.Show();
        N.SetForegroundWindow(open.Handle);
    }

    readonly Board board;
    readonly Cfg c;                 // working copy; each change goes to the board as a new copy
    bool auto = Setup.AutoStarts;
    readonly Panel side, head, body;
    readonly Txt title, subtitle;
    readonly List<Nav> navs = [];
    readonly Popup drop = new(false);
    string? capture;                // shortcut waiting for keys
    int section;

    static readonly (string Name, string Sub, string Icon)[] Sections =
    [
        ("How to use", "Boards, players and shortcuts", ""),
        ("Boards", "Number, names and slide", ""),
        ("Shortcuts", "Work while Pinboard or MultiViewer is in front", ""),
        ("Behaviour", "Start and exit", ""),
    ];

    int S(int px) => (int)Math.Round(px * DeviceDpi / 96f);

    Hub(Board b)
    {
        board = b;
        c = b.Settings.Copy();
        Text = $"{Program.Name} settings";
        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Main; KeyPreview = true; DoubleBuffered = true;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        _ = Handle;
        Size = new(S(980), S(700));
        Theme.Round(Handle);

        side = new Panel { Bounds = new(0, 0, S(260), Height), BackColor = Theme.Side };
        side.Controls.Add(new Txt(Program.Name, Theme.Strong, Theme.Text, S(230)) { Location = new(S(20), S(24)), BackColor = Theme.Side });
        for (int i = 0; i < Sections.Length; i++)
        {
            int k = i;
            var n = new Nav(Sections[i].Icon, Sections[i].Name) { Bounds = new(S(10), S(64) + i * S(44), S(240), S(40)) };
            n.Click += (_, _) => Select(k);
            navs.Add(n); side.Controls.Add(n);
        }
        var hide = new Toggle(File.Exists(HideMark)) { Location = new(S(14), Height - S(84)) };
        hide.Changed += on => { if (on) File.WriteAllText(HideMark, ""); else File.Delete(HideMark); };
        var hideText = new Txt("Don't show at start", Theme.Body, Theme.Text, S(190)) { Cursor = Cursors.Hand };
        hideText.Location = new(hide.Right + S(4), hide.Top + (hide.Height - hideText.Height) / 2);
        hideText.Click += (_, _) => hide.Flip();
        side.Controls.AddRange([hide, hideText]);
        side.Controls.Add(new Txt($"v{Application.ProductVersion.Split('+')[0]} · Windows x64", Theme.Small, Theme.Muted, S(230)) { Location = new(S(20), Height - S(40)) });
        side.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) Theme.Move(this); };

        head = new Panel { Bounds = new(S(260), 0, Width - S(260), S(64)), BackColor = Theme.Main };
        title = new Txt("", Theme.Strong, Theme.Text, S(500)) { Location = new(S(24), S(12)) };
        subtitle = new Txt("", Theme.Small, Theme.Soft, S(560)) { Location = new(S(24), S(36)) };
        var close = new Nav("", "") { Bounds = new(head.Width - S(56), S(12), S(40), S(40)) };
        close.Click += (_, _) => Close();
        var min = new Nav("", "") { Bounds = new(close.Left - S(44), S(12), S(40), S(40)) };
        min.Click += (_, _) => WindowState = FormWindowState.Minimized;
        head.Controls.AddRange([title, subtitle, min, close]);
        foreach (Control x in new Control[] { head, title, subtitle }) x.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) Theme.Move(this); };

        body = new Panel { Bounds = new(S(260), S(64), Width - S(260), Height - S(64)), BackColor = Theme.Main, AutoScroll = true };
        Controls.AddRange([side, head, body]);
        Theme.DarkScroll(body);
        FormClosed += (_, _) => Icon?.Dispose();
        Shown += (_, _) => { ActiveControl = null; body.AutoScrollPosition = Point.Empty; }; // no control takes focus, so the page opens at the top
    }

    // MINIMIZEBOX | SYSMENU: the taskbar button minimizes and restores it, as in any app.
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.Style |= 0x20000 | 0x80000; return p; } }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var p = new Pen(Theme.Line);
        e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
    }

    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Escape && capture == null && ActiveControl is not Edit) Close(); } // Esc in a text field: undo the typing

    // Keys reach a waiting shortcut before any control sees them (arrows, Tab, Esc included).
    protected override bool ProcessCmdKey(ref Message m, Keys data)
    {
        if (capture == null) return base.ProcessCmdKey(ref m, data);
        var k = data & Keys.KeyCode;
        if (k == Keys.Escape) { capture = null; Build(); return true; }
        if (k is Keys.Back or Keys.Delete) { c.Shortcuts[capture] = ""; capture = null; Apply(); Build(); return true; }
        if (Hotkey.From(data) is not { } s) return true; // wait for a key with Ctrl or Alt (F1-F24 alone are fine)
        foreach (var id in c.Shortcuts.Keys.Where(id => c.Shortcuts[id] == s).ToList()) c.Shortcuts[id] = ""; // one key, one action
        c.Shortcuts[capture] = s;
        capture = null; Apply(); Build();
        return true;
    }

    void Apply()
    {
        var x = c.Copy();
        x.AutoStart = auto;
        board.Apply(x);
    }

    void Select(int s)
    {
        section = s; capture = null;
        for (int i = 0; i < navs.Count; i++) navs[i].Selected = i == s;
        title.Text = Sections[s].Name; subtitle.Text = Sections[s].Sub;
        Build();
    }

    // ---- Page: section headings and cards of rows, laid out top to bottom ----

    int y;

    void Build()
    {
        int scroll = body.VerticalScroll.Value;
        body.SuspendLayout();
        foreach (Control x in body.Controls.Cast<Control>().ToList()) x.Dispose();
        body.AutoScrollPosition = Point.Empty;
        y = S(8);
        string K(string id) => Hotkey.Show(c.Shortcuts.GetValueOrDefault(id));
        switch (section)
        {
            case 0:
                Heading("Steps");
                Card(
                    ("1  Start", "Open Pinboard and MultiViewer. Pinboard waits in system tray. It opens when your setup opens, on board 1.", null),
                    ("2  Change board", "Click arrow at left or right edge, or click a dot in title bar. Full screen: dots are at the bottom.", null),
                    ("3  Move player", $"Drag player onto arrow and hold. Or press {K("sendPrev")} / {K("sendNext")}: player in front goes to previous / next board.", null),
                    ("4  Resize", $"Drag Pinboard edges. Players scale with it and stay inside. {K("full")}: full screen.", null),
                    ("5  Save", "Arrange boards, then save setup in MultiViewer. Setup opens with each player on its board.", null),
                    ("6  Find player", $"Player lost or minimized: press {K("menu")}, or click tray icon. Click player: it comes back on this board. Drag menu to move it.", null));
                Heading("Settings needed");
                Card(
                    ("MultiViewer", "No settings needed.", null),
                    ("Windows", "Player snaps to half screen at an arrow: turn off Settings > System > Multitasking > Snap windows.", null));
                break;

            case 1:
                Heading("Boards");
                var count = Choice(c.Boards.ToString(), ["1", "2", "3", "4"], i => { c.Boards = i + 1; Apply(); Build(); });
                var rows = new List<(string, string, Control?)> { ("Number of boards", "Up to 4. Players of a removed board go to the last board.", count) };
                for (int k = 0; k < c.Boards; k++)
                {
                    int b = k;
                    rows.Add(($"Board {k + 1} name", "Shows in title bar, menu and board dots.", Field(c.Name(k), s => { c.Names[b] = s; Apply(); })));
                }
                Card(rows.ToArray());
                Heading("Slide");
                int[] ms = [0, 200, 350, 600];
                string[] speed = ["Off", "Fast", "Normal", "Slow"];
                int at = Array.IndexOf(ms, c.SlideMs);
                Card(("Slide between boards", "How boards move when you change board.", Choice(at >= 0 ? speed[at] : $"{c.SlideMs} ms", speed, i => { c.SlideMs = ms[i]; Apply(); Build(); })));
                break;

            case 2:
                Heading("Shortcuts");
                Card(Hotkey.All.Select(a => (a.Label, a.Id == capture ? "Press the new keys. Backspace: none. Esc: keep." : "",
                    (Control?)Key(a.Id == capture ? "Press keys" : K(a.Id), () => { capture = a.Id; Build(); }))).ToArray());
                break;

            case 3:
                Heading("Start");
                var start = new Toggle(auto);
                start.Changed += on => { auto = on; Apply(); };
                Card(("Start with Windows", "Pinboard starts in system tray when you sign in.", start));
                Heading("Exit");
                string[] exits = ["ask", "keep", "close"], exitNames = ["Ask", "Close Pinboard only", "Close Pinboard and players"];
                Card(("On exit", "Players stay open in MultiViewer, or close with Pinboard.",
                    Choice(exitNames[Math.Max(0, Array.IndexOf(exits, c.OnExit))], exitNames, i => { c.OnExit = exits[i]; Apply(); Build(); })));
#if PERSONAL
                Heading("Capture (personal build)");
                var protect = new Toggle(c.Protect);
                protect.Changed += on => { c.Protect = on; Apply(); };
                Card(("Protect", "Video black in screen captures. The public build has no setting: always on.", protect));
#endif
                break;
        }
        body.Controls.Add(new Panel { Bounds = new(0, y, 1, S(16)) }); // bottom margin
        body.ResumeLayout();
        ActiveControl = null;
        body.AutoScrollPosition = new(0, scroll);
    }

    int PageW => body.Width - SystemInformation.VerticalScrollBarWidth - S(48); // room for the scroll bar before it shows

    void Heading(string text)
    {
        var l = new Txt(text, Theme.Head, Theme.Text, PageW) { Location = new(S(24), y + S(16)) };
        body.Controls.Add(l);
        y += S(16) + l.Height + S(10);
    }

    // Card: rounded outline; each row title, description below it, control at the right; dividers between rows.
    void Card(params (string Title, string Desc, Control? Ctl)[] rows)
    {
        int w = PageW, pad = S(16), yy = 0;
        var card = new Card { Location = new(S(24), y), BackColor = Theme.Main };
        foreach (var (t, d, ctl) in rows)
        {
            int textW = w - 2 * pad - (ctl == null ? 0 : ctl.Width + S(24));
            // Text blocks size themselves (wrapped at textW), so the height always fits what they draw.
            Txt L(string s, Font f, Color c) => new(s, f, c, textW);
            var tl = L(t, Theme.Strong, Theme.Text);
            var dl = d.Length == 0 ? null : L(d, Theme.Small, Theme.Soft);
            int th = tl.Height, dh = dl?.Height ?? 0, gap = dl == null ? 0 : S(2);
            int h = Math.Max(th + gap + dh, ctl?.Height ?? 0) + 2 * S(14);
            int ty = yy + (h - (th + gap + dh)) / 2;
            tl.Location = new(pad, ty); card.Controls.Add(tl);
            if (dl != null) { dl.Location = new(pad, ty + th + gap); card.Controls.Add(dl); }
            if (ctl != null) { ctl.Location = new(w - pad - ctl.Width, yy + (h - ctl.Height) / 2); card.Controls.Add(ctl); }
            yy += h;
            card.Lines.Add(yy);
        }
        card.Lines.RemoveAt(card.Lines.Count - 1);
        card.Size = new(w, yy);
        body.Controls.Add(card);
        y += yy + S(8);
    }

    // Outlined box with caret: opens a list of choices below it.
    Control Choice(string text, string[] items, Action<int> pick)
    {
        var o = new Outline(text, true) { Size = new(S(220), S(40)) };
        o.Click += (_, _) => drop.Open(o.PointToScreen(new(0, o.Height + S(2))), items.Select((s, i) => (s, "", (Action)(() => pick(i)))), null, 200);
        return o;
    }

    Control Key(string text, Action click)
    {
        var o = new Outline(text, false) { Size = new(S(180), S(40)) };
        o.Click += (_, _) => click();
        return o;
    }

    // Outlined text field drawn like the rest; the value is taken when it loses focus or Enter is pressed.
    Control Field(string text, Action<string> done)
    {
        var f = new Edit(text, 30) { Size = new(S(260), S(40)) };
        f.Done += done;
        return f;
    }
}

// Card outline with row dividers.
class Card : Panel
{
    public readonly List<int> Lines = [];

    public Card() { DoubleBuffered = true; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var p = new Pen(Theme.Line);
        foreach (var y in Lines) g.DrawLine(p, 1, y, Width - 2, y);
        using var r = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 4 * DeviceDpi / 96f);
        g.DrawPath(p, r);
    }
}

// Sidebar entry (icon and name; rounded highlight when selected) and the close button.
class Nav : Control
{
    readonly string icon;
    bool hot, selected;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Selected { get => selected; set { selected = value; Invalidate(); } }

    public Nav(string icon, string text) { this.icon = icon; Text = text; DoubleBuffered = true; Cursor = Cursors.Hand; TabStop = false; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent!.BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (selected || hot) { using var b = new SolidBrush(selected ? Theme.Sel : Theme.Hover); using var r = Theme.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), 4 * DeviceDpi / 96f); g.FillPath(b, r); }
        int s = (int)(DeviceDpi / 96f * 16);
        if (Text.Length == 0) { Theme.Draw(g, icon, Theme.Icon, Theme.Text, ClientRectangle, StringAlignment.Center); return; }
        Theme.Draw(g, icon, Theme.Icon, Theme.Soft, new Rectangle(s / 2, 0, 2 * s, Height), StringAlignment.Center);
        Theme.Draw(g, Text, Theme.Body, Theme.Text, new Rectangle(3 * s, 0, Width - 3 * s, Height));
    }

    protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); }
}

// MUI switch: track and round thumb, accent blue when on.
class Toggle : Control
{
    bool on;
    public bool On => on;
    public event Action<bool>? Changed;

    public Toggle(bool on)
    {
        this.on = on; DoubleBuffered = true; Cursor = Cursors.Hand; TabStop = false;
        Size = new((int)(52 * DeviceDpi / 96f), (int)(32 * DeviceDpi / 96f));
    }


    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent!.BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float u = DeviceDpi / 96f, th = 14 * u, d = 20 * u, cy = Height / 2f, x0 = 6 * u, x1 = Width - 6 * u;
        using (var track = new SolidBrush(on ? Color.FromArgb(128, Theme.Accent) : Color.FromArgb(77, Color.White)))
        using (var r = Theme.Rounded(new RectangleF(x0, cy - th / 2, x1 - x0, th), th / 2)) g.FillPath(track, r);
        float tx = on ? x1 - d + 2 * u : x0 - 2 * u;
        using var thumb = new SolidBrush(on ? Theme.Accent : ColorTranslator.FromHtml("#e0e0e0"));
        g.FillEllipse(thumb, tx, cy - d / 2, d, d);
    }

    public void Flip() { on = !on; Invalidate(); Changed?.Invoke(on); }

    protected override void OnClick(EventArgs e) { base.OnClick(e); Flip(); }
}

// Outlined box like MUI's outlined select: text left, optional caret right; brighter outline on hover.
class Outline : Control
{
    readonly bool caret;
    bool hot;

    public Outline(string text, bool caret) { Text = text; this.caret = caret; DoubleBuffered = true; Cursor = Cursors.Hand; BackColor = Theme.Main; TabStop = false; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent!.BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float u = DeviceDpi / 96f;
        using (var p = new Pen(hot ? Theme.Soft : Theme.Outline))
        using (var r = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 4 * u)) g.DrawPath(p, r);
        int pad = (int)(14 * u);
        Theme.Draw(g, Text, Theme.Body, Theme.Text, new Rectangle(pad, 0, Width - 2 * pad - (caret ? pad : 0), Height));
        if (!caret) return;
        float cx = Width - pad - 4 * u, cy = Height / 2f;
        using var b = new SolidBrush(Theme.Text);
        g.FillPolygon(b, new PointF[] { new(cx - 5 * u, cy - 2.5f * u), new(cx + 5 * u, cy - 2.5f * u), new(cx, cy + 2.5f * u) });
    }

    protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); }
}

// One-line text field drawn smooth like the rest (Windows' own text box draws thin and jagged Exo 2).
// Typing, caret, Backspace, Delete, arrows, Home, End, Ctrl+A/C/V. Enter or leaving keeps the text; Esc puts the last kept text back.
class Edit : Control
{
    public event Action<string>? Done;
    readonly int max;
    readonly System.Windows.Forms.Timer blinker = new() { Interval = 530 };
    string kept;
    int caret;
    bool all, hot, blink = true;

    public Edit(string text, int max)
    {
        Text = kept = text; this.max = max; caret = text.Length;
        DoubleBuffered = true; Cursor = Cursors.IBeam; TabStop = true; Font = Theme.Body;
        SetStyle(ControlStyles.Selectable, true);
        blinker.Tick += (_, _) => { blink = !blink; Invalidate(); };
        Disposed += (_, _) => blinker.Dispose();
    }

    int Pad => (int)(14 * DeviceDpi / 96f);

    // Width of the first n characters, measured the way they are drawn.
    float TextW(int n)
    {
        if (n == 0) return 0;
        using var g = CreateGraphics();
        g.TextRenderingHint = Theme.Hint;
        using var sf = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.MeasureTrailingSpaces };
        return g.MeasureString(Text[..n], Font, PointF.Empty, sf).Width;
    }

    int At(int x)
    {
        int best = 0; float d = float.MaxValue;
        for (int i = 0; i <= Text.Length; i++) { float e = Math.Abs(Pad + TextW(i) - x); if (e < d) { d = e; best = i; } }
        return best;
    }

    void Set(string s, int at) { Text = s.Length > max ? s[..max] : s; caret = Math.Clamp(at, 0, Text.Length); all = false; blink = true; Invalidate(); }

    void Insert(string s)
    {
        s = s.Replace("\r", "").Replace("\n", " ");
        if (all) Set(s, s.Length);
        else Set(Text[..caret] + s + Text[caret..], caret + s.Length);
    }

    void Keep() { var s = Text.Trim(); if (s != kept) { kept = s; Done?.Invoke(s); } }

    protected override bool IsInputKey(Keys k) => (k & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(k);

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); all = true; caret = Text.Length; blinker.Start(); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); blinker.Stop(); all = false; Keep(); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        bool had = Focused;
        Focus();
        if (had) Set(Text, At(e.X)); // first click selects all, the next places the caret
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        bool ctrl = e.Control;
        switch (e.KeyCode)
        {
            case Keys.Left: Set(Text, all ? 0 : caret - 1); break;
            case Keys.Right: Set(Text, all ? Text.Length : caret + 1); break;
            case Keys.Home: Set(Text, 0); break;
            case Keys.End: Set(Text, Text.Length); break;
            case Keys.Back: if (all) Set("", 0); else if (caret > 0) Set(Text.Remove(caret - 1, 1), caret - 1); break;
            case Keys.Delete: if (all) Set("", 0); else if (caret < Text.Length) Set(Text.Remove(caret, 1), caret); break;
            case Keys.Enter: Keep(); all = true; Invalidate(); break;
            case Keys.Escape: Set(kept, kept.Length); break;
            case Keys.A when ctrl: all = true; Invalidate(); break;
            case Keys.C when ctrl: if (Text.Length > 0) Clipboard.SetText(Text); break;
            case Keys.V when ctrl: if (Clipboard.ContainsText()) Insert(Clipboard.GetText()); break;
            default: return;
        }
        e.Handled = e.SuppressKeyPress = true;
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (char.IsControl(e.KeyChar)) return;
        Insert(e.KeyChar.ToString());
        e.Handled = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent!.BackColor);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = Theme.Hint;
        float u = DeviceDpi / 96f;
        using (var p = new Pen(Focused ? Theme.Accent : hot ? Theme.Soft : Theme.Outline, Focused ? 2 * u : 1))
        using (var r = Theme.Rounded(new RectangleF(1, 1, Width - 2, Height - 2), 4 * u)) g.DrawPath(p, r);
        using var sf = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap };
        float th = Font.GetHeight(g), y = (Height - th) / 2;
        if (all && Focused && Text.Length > 0) { using var sel = new SolidBrush(Color.FromArgb(90, Theme.Accent)); g.FillRectangle(sel, Pad, y, TextW(Text.Length), th); }
        using (var b = new SolidBrush(Theme.Text)) g.DrawString(Text, Font, b, Pad, y, sf);
        if (Focused && blink && !all) { using var c = new Pen(Theme.Text, u); float x = Pad + TextW(caret); g.DrawLine(c, x, y, x, y + th); }
    }
}

// MUI button: contained (accent fill, dark text) for the main choice, outlined for the rest.
class Btn : Control
{
    readonly bool main;
    bool hot;

    public Btn(string text, bool main) { Text = text; this.main = main; DoubleBuffered = true; Cursor = Cursors.Hand; Font = Theme.Strong; TabStop = false; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent!.BackColor);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var r = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 4 * DeviceDpi / 96f);
        if (main) { using var b = new SolidBrush(hot ? ColorTranslator.FromHtml("#b3dafb") : Theme.Accent); g.FillPath(b, r); }
        else
        {
            if (hot) { using var b = new SolidBrush(Color.FromArgb(20, Theme.Accent)); g.FillPath(b, r); }
            using var p = new Pen(Color.FromArgb(128, Theme.Accent)); g.DrawPath(p, r);
        }
        Theme.Draw(g, Text, Font, main ? ColorTranslator.FromHtml("#121212") : Theme.Accent, ClientRectangle, StringAlignment.Center);
    }

    protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); }
}

// Question in Pinboard's look: heading, text, "don't ask again" switch, choices (last one is the main choice) and Cancel.
static class Ask
{
    public static (string? Tag, bool Remember) Show(IWin32Window owner, string heading, string text, string check, params (string Label, string Tag)[] choices)
    {
        using var f = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.CenterScreen, ShowInTaskbar = false, TopMost = true, BackColor = Theme.Main, KeyPreview = true, Text = Program.Name };
        _ = f.Handle;
        int S(int px) => (int)Math.Round(px * f.DeviceDpi / 96f);
        int w = S(560), x = S(24);
        var h = new Txt(heading, Theme.Head, Theme.Text, w - 2 * x) { Location = new(x, S(22)) };
        var t = new Txt(text, Theme.Body, Theme.Soft, w - 2 * x) { Location = new(x, h.Bottom + S(8)) };
        var tog = new Toggle(false) { Location = new(x - S(6), t.Bottom + S(14)) };
        var tl = new Txt(check, Theme.Small, Theme.Soft, w - tog.Right - x) { Location = new(tog.Right + S(4), 0) };
        tl.Top = tog.Top + (tog.Height - tl.Height) / 2;
        tl.Click += (_, _) => tog.Flip();
        f.Controls.AddRange([h, t, tog, tl]);
        string? result = null;
        int by = tog.Bottom + S(20), right = w - x;
        var all = choices.Select((c, i) => (c.Label, (string?)c.Tag, Main: i == choices.Length - 1)).Prepend(("Cancel", null, false)).ToList();
        foreach (var (label, tag, main) in all.AsEnumerable().Reverse())
        {
            var b = new Btn(label, main);
            b.Size = new(Theme.Measure(label, Theme.Strong, 0, f.DeviceDpi).Width + S(32), S(38));
            b.Location = new(right - b.Width, by);
            right = b.Left - S(10);
            b.Click += (_, _) => { result = tag; f.Close(); };
            f.Controls.Add(b);
        }
        f.ClientSize = new(w, by + S(38) + S(22));
        f.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) f.Close(); };
        f.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) Theme.Move(f); };
        f.Paint += (_, e) => { using var p = new Pen(Theme.Line); e.Graphics.DrawRectangle(p, 0, 0, f.Width - 1, f.Height - 1); };
        Theme.Round(f.Handle);
        f.ShowDialog(owner);
        return (result, tog.On);
    }
}
