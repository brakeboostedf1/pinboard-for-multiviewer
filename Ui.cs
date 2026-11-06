// Pinboard's own windows besides the board: popup lists, edge arrows, board dots, capture shields. MultiViewer's look.
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

// MultiViewer's look: MUI dark theme (accent #90caf9, dividers 12 % white), F1 red, Exo 2.
static class Theme
{
    public static readonly Color Title = Hex("#202020"), Side = Hex("#202020"), Main = Hex("#2a2a2a"), Hover = Hex("#333333"), Sel = Hex("#3a3a3a"),
        Line = Hex("#474747"), Outline = Hex("#5c5c5c"), Text = Color.White, Soft = Hex("#b3b3b3"), Muted = Hex("#8a8a8a"),
        Accent = Hex("#90caf9"), Red = Hex("#e10600");
    public const int Tint = 200; // board dots overlay colour, 0-255: lower shows more of the player behind it
    // Exo 2 (the font MultiViewer uses) ships inside Pinboard.exe; loaded for GDI (TextRenderer) and GDI+ (DrawString). Segoe UI if it fails.
    static readonly System.Drawing.Text.PrivateFontCollection fonts = new();
    public static readonly FontFamily Face = Load("Exo2-Medium.ttf", "Exo 2 Medium") ?? new("Segoe UI"), FaceBold = Load("Exo2-SemiBold.ttf", "Exo 2 SemiBold") ?? new("Segoe UI Semibold");
    public static readonly Font Body = new(Face, 10.5f), Small = new(Face, 9.5f), Strong = new(FaceBold, 10.5f), Head = new(FaceBold, 13f),
        Icon = new("Segoe MDL2 Assets", 12f);

    static FontFamily? Load(string file, string family)
    {
        try
        {
            using var s = typeof(Theme).Assembly.GetManifestResourceStream(file)!;
            var bytes = new byte[s.Length]; s.ReadExactly(bytes);
            var p = Marshal.AllocCoTaskMem(bytes.Length); // kept for the life of the app: both font tables point at it
            Marshal.Copy(bytes, 0, p, bytes.Length);
            fonts.AddMemoryFont(p, bytes.Length);
            uint n = 0; AddFontMemResourceEx(p, (uint)bytes.Length, 0, ref n);
            return fonts.Families.FirstOrDefault(f => f.Name == family);
        }
        catch (Exception e) { Program.Log(e); return null; }
    }
    [DllImport("gdi32.dll")] static extern IntPtr AddFontMemResourceEx(IntPtr font, uint size, IntPtr reserved, ref uint count);

    static Color Hex(string h) => ColorTranslator.FromHtml(h);

    // Grey smooth edges, stems snapped to the pixel grid: Medium weight drawn this way is the cleanest (compared side by side).
    // (Windows' GDI ClearType makes Exo 2 thin and jagged, so no TextRenderer or Label anywhere.)
    public const System.Drawing.Text.TextRenderingHint Hint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
    public static void Draw(Graphics g, string s, Font f, Color c, RectangleF r, StringAlignment along = StringAlignment.Near, bool wrap = false)
    {
        g.TextRenderingHint = Theme.Hint;
        using var b = new SolidBrush(c);
        using var sf = new StringFormat(StringFormat.GenericDefault) { Alignment = along, LineAlignment = wrap ? StringAlignment.Near : StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
        if (!wrap) sf.FormatFlags |= StringFormatFlags.NoWrap;
        g.DrawString(s, f, b, r, sf);
    }

    // Size of text drawn by Draw on a monitor of this dpi, wrapped at width (0: one line).
    public static Size Measure(string s, Font f, int width, int dpi)
    {
        using var bmp = new Bitmap(1, 1);
        bmp.SetResolution(dpi, dpi);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = Theme.Hint;
        var z = g.MeasureString(s.Length == 0 ? " " : s, f, width > 0 ? width : int.MaxValue, StringFormat.GenericDefault);
        return new((int)Math.Ceiling(z.Width), (int)Math.Ceiling(z.Height));
    }

    // Windows 11 round corners and dark scroll bars; older Windows ignores both.
    public static void Round(IntPtr h) { int r = 2; N.DwmSetWindowAttribute(h, 33, ref r, 4); } // DWMWA_WINDOW_CORNER_PREFERENCE = ROUND
    public static void DarkScroll(Control c) => SetWindowTheme(c.Handle, "DarkMode_Explorer", null);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr h, string app, string? id);

    public static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath(); float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    // Borderless window moves with Windows' own move loop; returns when the mouse button is up.
    public static void Move(Form f) { N.ReleaseCapture(); N.SendMessage(f.Handle, 0xA1, 2, 0); } // WM_NCLBUTTONDOWN, HTCAPTION
}

// Popup list in MultiViewer's card look: player menu and dropdown choices. Moves with click and drag anywhere on it;
// closes on a click elsewhere, Esc, or a choice. Empty text: a divider line.
class Popup : Form
{
    readonly bool remember;
    Point? placed; // where the user dragged it: it opens there again (player menu only)
    bool moving;

    public Popup(bool remember)
    {
        this.remember = remember;
        FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Line; Padding = new(1); KeyPreview = true; Text = "Pinboard menu";
        _ = Handle;
        Theme.Round(Handle);
    }

    int S(int px) => (int)Math.Round(px * DeviceDpi / 96f);

    public void Open(Point at, IEnumerable<(string Text, string Right, Action Go)> items, string? header = null, int minWidth = 240)
    {
        foreach (Control c in Controls.Cast<Control>().ToList()) c.Dispose();
        var list = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, BackColor = Theme.Main, Padding = new(0, S(6), 0, S(6)), Margin = Padding.Empty };
        var rows = (header == null ? items : items.Prepend((header, "", () => { }))).ToList();
        int w = S(minWidth);
        using (var g = CreateGraphics())
            foreach (var (t, r, _) in rows)
                w = Math.Max(w, Theme.Measure(t, Theme.Body, 0, DeviceDpi).Width + Theme.Measure(r, Theme.Small, 0, DeviceDpi).Width + S(64));
        for (int i = 0; i < rows.Count; i++) list.Controls.Add(new Row(this, rows[i].Text, rows[i].Right, rows[i].Go, header != null && i == 0, w));
        list.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) Drag(); };
        Controls.Add(list);
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var p = remember && placed is { } q ? q : at;
        var area = Screen.FromPoint(p).WorkingArea;
        var size = PreferredSize;
        Location = new(Math.Clamp(p.X, area.Left, Math.Max(area.Left, area.Right - size.Width)), Math.Clamp(p.Y, area.Top, Math.Max(area.Top, area.Bottom - size.Height)));
        Show();
        N.SetForegroundWindow(Handle);
        Activate();
    }

    public void Drag()
    {
        moving = true;
        Theme.Move(this);
        moving = false;
        placed = Location;
        Activate();
    }

    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); if (!moving) Hide(); }

    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (e.KeyCode == Keys.Escape) Hide(); }

    // One line: text left, board or shortcut right. Header: grey, not clickable.
    sealed class Row : Control
    {
        readonly Popup menu;
        readonly string right;
        readonly Action go;
        readonly bool header;
        Point down;
        bool hot;

        public Row(Popup m, string text, string right, Action go, bool header, int width)
        {
            menu = m; Text = text; this.right = right; this.go = go; this.header = header;
            Size = new(width, text.Length == 0 ? m.S(13) : m.S(header ? 28 : 34));
            Margin = Padding.Empty;
            DoubleBuffered = true;
            Cursor = Clickable ? Cursors.Hand : Cursors.SizeAll;
        }

        bool Clickable => Text.Length > 0 && !header;

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Main);
            if (Text.Length == 0) { using var p = new Pen(Theme.Line); g.DrawLine(p, 0, Height / 2, Width, Height / 2); return; }
            int pad = menu.S(16);
            if (hot && Clickable) { using var b = new SolidBrush(Theme.Sel); g.FillRectangle(b, 0, 0, Width, Height); }
            var box = new Rectangle(pad, 0, Width - 2 * pad, Height);
            Theme.Draw(g, Text, header ? Theme.Small : Theme.Body, header ? Theme.Muted : Theme.Text, box);
            Theme.Draw(g, right, Theme.Small, Theme.Muted, box, StringAlignment.Far);
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            down = e.Location;
            if (e.Button == MouseButtons.Left && !Clickable) menu.Drag();
        }

        // Moved past the drag distance with the button down: the menu moves instead of clicking.
        protected override void OnMouseMove(MouseEventArgs e)
        {
            var d = SystemInformation.DragSize;
            if (e.Button == MouseButtons.Left && Clickable && (Math.Abs(e.X - down.X) > d.Width || Math.Abs(e.Y - down.Y) > d.Height)) menu.Drag();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || !Clickable || !ClientRectangle.Contains(e.Location)) return;
            menu.Hide();
            go();
        }
    }
}

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

    public void Put(Bitmap b, Point at, byte alpha = 255)
    {
        Draw(Handle, b, at, alpha);
        if (!On) { N.ShowWindow(Handle, 4); On = true; } // SW_SHOWNOACTIVATE
    }

    // Hands a bitmap to a layered window; Windows keeps and shows it without asking the window to paint.
    public static void Draw(IntPtr h, Bitmap b, Point at, byte alpha = 255)
    {
        var scr = N.GetDC(0);
        var mem = N.CreateCompatibleDC(scr);
        var hb = b.GetHbitmap(Color.FromArgb(0));
        var old = N.SelectObject(mem, hb);
        var size = new Size(b.Width, b.Height);
        var src = Point.Empty;
        var blend = new N.Blend { alpha = alpha, format = 1 }; // AC_SRC_ALPHA; alpha fades the whole image
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

// Board dots: the board on screen is a white pill, the others soft dots (brighter under the cursor). Each in its slot.
static class Pips
{
    public static void Draw(Graphics g, int n, int cur, int hot, Func<int, Rectangle> slot, float u)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        for (int k = 0; k < n; k++)
        {
            var s = slot(k);
            float w = (k == cur ? 16 : 6) * u, h = 6 * u, x = s.X + (s.Width - w) / 2f, y = s.Y + (s.Height - h) / 2f;
            using var b = new SolidBrush(k == cur ? Color.White : Color.FromArgb(k == hot ? 220 : 110, Color.White));
            using var r = Theme.Rounded(new RectangleF(x, y, w, h), h / 2);
            g.FillPath(b, r);
        }
    }
}

// Edge arrow: clear box, every pixel of it clickable, white chevron with dark edge. Size set by the board.
class Arrow : Layer
{
    public Action? Go;
    public int W { get; private set; } = 56;
    public int H { get; private set; } = 160;
    readonly bool right;
    Bitmap bmp;
    long seen;
    Point at;

    public Arrow(bool right)
    {
        this.right = right;
        Cursor = Cursors.Hand;
        bmp = Chevron(W, H);
        MouseDown += (_, _) => { seen = Environment.TickCount64; Go?.Invoke(); };
    }

    public void SetSize(int w, int h)
    {
        if (w == W && h == H) return;
        (W, H) = (w, h);
        bmp.Dispose(); bmp = Chevron(w, h);
        if (On) Put(bmp, at);
    }

    Bitmap Chevron(int w, int h)
    {
        var b = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(b);
        g.Clear(Color.FromArgb(1, 0, 0, 0)); // alpha 1: looks clear, still takes clicks
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float u = w / 56f; // chevron scales with the arrow
        int cx = w / 2, cy = h / 2, d = right ? 1 : -1;
        PointF[] v = [new(cx - 8 * u * d, cy - 20 * u), new(cx + 8 * u * d, cy), new(cx - 8 * u * d, cy + 20 * u)];
        using var edge = new Pen(Color.FromArgb(150, 0, 0, 0), 9 * u) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var face = new Pen(Color.White, 5 * u) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(edge, v);
        g.DrawLines(face, v);
        return b;
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

// Board dots and name: see-through rounded overlay at the top centre of the board. Click a dot to go to that board.
// Full while nothing is under it; with a player under it, it fades out holdMs after it last woke.
class Dots : Layer
{
    const int FadeMs = 600;
    public Action<int>? Go;
    Bitmap bmp = new(1, 1);
    Point at;
    int n = -1, cur, dot, pad;
    string name = "";
    long woke = long.MinValue / 2;
    bool wasCovered;
    byte alpha;

    public Dots() { Cursor = Cursors.Hand; MouseDown += (_, e) => { int k = (e.X - pad) / dot; if (e.X >= pad && k < n) Go?.Invoke(k); }; }

    public Size Image => bmp.Size;

    public void Wake() => woke = Environment.TickCount64;

    // Redrawn only when boards, board on screen, name or scale change.
    public void Set(int boards, int current, string label, float scale)
    {
        int d = (int)(20 * scale);
        if (boards == n && current == cur && label == name && d == dot) return;
        (n, cur, name, dot, pad) = (boards, current, label, d, (int)(14 * scale));
        using var font = new Font(Theme.Face, 9.5f * scale);
        int tw = Theme.Measure(label, font, 0, 96).Width, h = (int)(32 * scale);
        var b = new Bitmap(pad + n * dot + pad / 2 + tw + pad, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = Theme.Hint;
            using (var tint = new SolidBrush(Color.FromArgb(Theme.Tint, Theme.Title)))
            using (var r = Theme.Rounded(new RectangleF(0, 0, b.Width, h), h / 2f)) g.FillPath(tint, r);
            Pips.Draw(g, n, cur, -1, k => new(pad + k * dot, 0, dot, h), scale);
            using var white = new SolidBrush(Theme.Text);
            g.DrawString(label, font, white, new RectangleF(pad + n * dot + pad / 2, 0, tw, h), new StringFormat { LineAlignment = StringAlignment.Center });
        }
        bmp.Dispose(); bmp = b;
        if (On) Put(bmp, at, alpha);
    }

    // Each tick: can = Pinboard or MultiViewer in front; covered = a player of the board on screen under it.
    public void Tick(bool can, Point pos, bool covered, Point cursor, int holdMs)
    {
        long now = Environment.TickCount64;
        if (covered && !wasCovered) woke = now; // a player just sat on it: it fades holdMs from now
        wasCovered = covered;
        if (On && new Rectangle(at, bmp.Size).Contains(cursor)) woke = now;
        double f = !covered ? 1 : Math.Clamp(1 - (now - woke - holdMs) / (double)FadeMs, 0, 1);
        byte a = (byte)Math.Round(f * f * 255); // eases out
        if (!can || a == 0) { Off(); return; }
        if (!On || pos != at || a != alpha) { at = pos; alpha = a; Put(bmp, pos, a); }
    }
}

// Text block drawn like MultiViewer's (Theme.Draw): wraps at its width, height set from the text.
class Txt : Control
{
    public Txt(string s, Font f, Color c, int width)
    {
        Font = f; ForeColor = c; DoubleBuffered = true; Width = width; Text = s; TabStop = false;
        Height = Theme.Measure(s, f, width, DeviceDpi).Height;
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Main);
        Theme.Draw(e.Graphics, Text, Font, ForeColor, ClientRectangle, StringAlignment.Near, true);
    }
}
