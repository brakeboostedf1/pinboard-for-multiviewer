// How to use and Settings in one window, laid out like MultiViewer's settings: sidebar, header, cards of rows.
// Every change applies at once and is saved; there is no Save button.
using System.Drawing.Drawing2D;

class Hub : Form
{
    static Hub? open;
    static readonly string HideMark = Path.Combine(Program.Data, "help-hidden"); // "Show at start" off

    public static void AtStart(Board b) { if (!File.Exists(HideMark)) Open(b, 0); }

    public static void Open(Board b, int section)
    {
        if (open is { IsDisposed: false }) { open.Select(section); open.Activate(); return; }
        open = new Hub(b);
        open.Select(section);
        open.Show();
        N.SetForegroundWindow(open.Handle);
    }

    readonly Board board;
    readonly Cfg c;                 // working copy; each change goes to the board as a new copy
    bool auto = Setup.AutoStarts;
    readonly Panel side, head, body;
    readonly Label title, subtitle;
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
        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.CenterScreen; ShowInTaskbar = false; TopMost = true;
        BackColor = Theme.Main; KeyPreview = true; DoubleBuffered = true;
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        _ = Handle;
        Size = new(S(980), S(700));
        Theme.Round(Handle);

        side = new Panel { Bounds = new(0, 0, S(260), Height), BackColor = Theme.Side };
        side.Controls.Add(new Label { Text = Program.Name, Font = Theme.Strong, ForeColor = Theme.Text, Bounds = new(S(20), S(20), S(230), S(28)) });
        for (int i = 0; i < Sections.Length; i++)
        {
            int k = i;
            var n = new Nav(Sections[i].Icon, Sections[i].Name) { Bounds = new(S(10), S(64) + i * S(44), S(240), S(40)) };
            n.Click += (_, _) => Select(k);
            navs.Add(n); side.Controls.Add(n);
        }
        side.Controls.Add(new Label
        {
            Text = $"v{Application.ProductVersion.Split('+')[0]} · Windows x64", Font = Theme.Small, ForeColor = Theme.Muted,
            Bounds = new(S(20), Height - S(40), S(230), S(24)),
        });
        side.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) Theme.Move(this); };

        head = new Panel { Bounds = new(S(260), 0, Width - S(260), S(64)), BackColor = Theme.Main };
        title = new Label { Font = Theme.Strong, ForeColor = Theme.Text, Bounds = new(S(24), S(12), S(500), S(24)) };
        subtitle = new Label { Font = Theme.Small, ForeColor = Theme.Soft, Bounds = new(S(24), S(34), S(560), S(20)) };
        var close = new Nav("", "") { Bounds = new(head.Width - S(56), S(12), S(40), S(40)) };
        close.Click += (_, _) => Close();
        head.Controls.AddRange([title, subtitle, close]);
        foreach (Control x in new Control[] { head, title, subtitle }) x.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) Theme.Move(this); };

        body = new Panel { Bounds = new(S(260), S(64), Width - S(260), Height - S(64)), BackColor = Theme.Main, AutoScroll = true };
        Controls.AddRange([side, head, body]);
        Theme.DarkScroll(body);
        FormClosed += (_, _) => Icon?.Dispose();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var p = new Pen(Theme.Line);
        e.Graphics.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
    }

    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Escape && capture == null) Close(); }

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
                    ("4  Resize", $"Drag Pinboard edges. Players scale with it. Too small: players are cut at the edge. {K("full")}: full screen.", null),
                    ("5  Save", "Arrange boards, then save setup in MultiViewer. Setup opens with each player on its board.", null),
                    ("6  Find player", $"Player lost or minimized: press {K("menu")}, or click tray icon. Click player: it comes back on this board. Drag menu to move it.", null));
                Heading("Settings needed");
                Card(
                    ("MultiViewer", "No settings needed.", null),
                    ("Windows", "Player snaps to half screen at an arrow: turn off Settings > System > Multitasking > Snap windows.", null));
                Heading("This page");
                var show = new Toggle(!File.Exists(HideMark));
                show.Changed += on => { if (on) File.Delete(HideMark); else File.WriteAllText(HideMark, ""); };
                Card(("Show at start", "Opens How to use each time Pinboard starts.", show));
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
        body.AutoScrollPosition = new(0, scroll);
    }

    int PageW => body.Width - SystemInformation.VerticalScrollBarWidth - S(48); // room for the scroll bar before it shows

    void Heading(string text)
    {
        var l = new Label { Text = text, Font = Theme.Head, ForeColor = Theme.Text, AutoSize = true, Location = new(S(24), y + S(16)) };
        body.Controls.Add(l);
        y += S(16) + l.PreferredHeight + S(10);
    }

    // Card: rounded outline; each row title, description below it, control at the right; dividers between rows.
    void Card(params (string Title, string Desc, Control? Ctl)[] rows)
    {
        int w = PageW, pad = S(16), yy = 0;
        var card = new Card { Location = new(S(24), y), BackColor = Theme.Main };
        foreach (var (t, d, ctl) in rows)
        {
            int textW = w - 2 * pad - (ctl == null ? 0 : ctl.Width + S(24));
            // Labels size themselves (wrapped at textW), so the height always fits what they draw.
            Label L(string s, Font f, Color c) => new() { Text = s, Font = f, ForeColor = c, BackColor = Theme.Main, AutoSize = true, MaximumSize = new(textW, 0), Padding = Padding.Empty, Margin = Padding.Empty };
            var tl = L(t, Theme.Strong, Theme.Text);
            var dl = d.Length == 0 ? null : L(d, Theme.Small, Theme.Soft);
            int th = tl.GetPreferredSize(new(textW, 0)).Height, dh = dl?.GetPreferredSize(new(textW, 0)).Height ?? 0, gap = dl == null ? 0 : S(2);
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

    // Outlined text box; the value is taken when the box loses focus or Enter is pressed.
    Control Field(string text, Action<string> done)
    {
        var box = new Outline("", false) { Size = new(S(260), S(40)), Cursor = Cursors.IBeam };
        var t = new TextBox { Text = text, BorderStyle = BorderStyle.None, BackColor = Theme.Main, ForeColor = Theme.Text, Font = Theme.Body, MaxLength = 30 };
        t.Bounds = new(S(14), (box.Height - t.PreferredHeight) / 2, box.Width - S(28), t.PreferredHeight);
        string last = text;
        void Take() { var s = t.Text.Trim(); if (s != last) { last = s; done(s); } }
        t.Leave += (_, _) => Take();
        t.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Take(); } };
        box.Controls.Add(t);
        box.Click += (_, _) => t.Focus();
        return box;
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

    public Nav(string icon, string text) { this.icon = icon; Text = text; DoubleBuffered = true; Cursor = Cursors.Hand; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent!.BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (selected || hot) { using var b = new SolidBrush(selected ? Theme.Sel : Theme.Hover); using var r = Theme.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), 4 * DeviceDpi / 96f); g.FillPath(b, r); }
        int s = (int)(DeviceDpi / 96f * 16);
        if (Text.Length == 0) { TextRenderer.DrawText(g, icon, Theme.Icon, ClientRectangle, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); return; }
        TextRenderer.DrawText(g, icon, Theme.Icon, new Rectangle(s / 2, 0, 2 * s, Height), Theme.Soft, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, Text, Theme.Body, new Rectangle(3 * s, 0, Width - 3 * s, Height), Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }

    protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); }
}

// MUI switch: track and round thumb, accent blue when on.
class Toggle : Control
{
    bool on;
    public event Action<bool>? Changed;

    public Toggle(bool on)
    {
        this.on = on; DoubleBuffered = true; Cursor = Cursors.Hand;
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

    protected override void OnClick(EventArgs e) { base.OnClick(e); on = !on; Invalidate(); Changed?.Invoke(on); }
}

// Outlined box like MUI's outlined select: text left, optional caret right; brighter outline on hover.
class Outline : Control
{
    readonly bool caret;
    bool hot;

    public Outline(string text, bool caret) { Text = text; this.caret = caret; DoubleBuffered = true; Cursor = Cursors.Hand; BackColor = Theme.Main; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent!.BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float u = DeviceDpi / 96f;
        using (var p = new Pen(hot ? Theme.Soft : Theme.Outline))
        using (var r = Theme.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 4 * u)) g.DrawPath(p, r);
        int pad = (int)(14 * u);
        TextRenderer.DrawText(g, Text, Theme.Body, new Rectangle(pad, 0, Width - 2 * pad - (caret ? pad : 0), Height), Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        if (!caret) return;
        float cx = Width - pad - 4 * u, cy = Height / 2f;
        using var b = new SolidBrush(Theme.Text);
        g.FillPolygon(b, new PointF[] { new(cx - 5 * u, cy - 2.5f * u), new(cx + 5 * u, cy - 2.5f * u), new(cx, cy + 2.5f * u) });
    }

    protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); }
}
