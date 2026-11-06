// Pinboard for MultiViewer: MultiViewer players on boards in one window; one board shows, the others park off screen.
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
        Setup.Legacy();
        using var one = new Mutex(true, "MVPinboard", out bool first);
        if (!first) { N.AllowSetForegroundWindow(-1); N.PostMessage(0xFFFF, HowTo, 0, 0); return 0; } // already running: it comes to the front
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log(e.Exception);
        var cfg = Cfg.Load();
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

// Settings file: Data\settings.json, edited in the Settings window. Out-of-range values are pulled into range.
class Cfg
{
    public int Boards { get; set; } = 4;                                         // 1-4
    public string[] Names { get; set; } = ["Board 1", "Board 2", "Board 3", "Board 4"];
    public int SlideMs { get; set; } = 350;                                      // slide time [ms]; 0 = no slide
#if PERSONAL
    public bool Protect { get; set; } = true;                                    // personal build only: video players black in screen captures, can be turned off
#else
    [System.Text.Json.Serialization.JsonIgnore] public bool Protect { get => true; set { } } // distributable: F1 TV video always black in screen captures, no setting
#endif
    public Dictionary<string, string> Shortcuts { get; set; } = Hotkey.All.ToDictionary(a => a.Id, a => a.Default);
    public string Back { get; set; } = "#101014";                                // board colour
    public int ArrowHideMs { get; set; } = 2000;                                 // arrows hide after the cursor stops [ms]
    public int HoldMs { get; set; } = 300;                                       // hold a dragged player over an arrow this long [ms]
    public int[] Window { get; set; } = [];                                      // board window x, y, w, h when not maximized [px]
    public bool Maximized { get; set; } = true;
    public string OnExit { get; set; } = "ask";                                  // ask, keep (players stay open), close (players close too)
    [System.Text.Json.Serialization.JsonIgnore] public bool AutoStart { get; set; } // start with Windows: lives in the registry, not in this file
    public string Skip { get; set; } = "^(Home|[0-9]{4} .+) . MultiViewer$";    // titles Pinboard leaves alone (MultiViewer main window)

    public static string File => Path.Combine(Program.Data, "settings.json");
    // Read and written by generated code (CfgJson), not reflection: works in the trimmed build.
    static readonly System.Text.Json.Serialization.Metadata.JsonTypeInfo<Cfg> Json = (System.Text.Json.Serialization.Metadata.JsonTypeInfo<Cfg>)
        new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, TypeInfoResolver = CfgJson.Default }.GetTypeInfo(typeof(Cfg));

    public string Name(int k) => string.IsNullOrWhiteSpace(Names.ElementAtOrDefault(k)) ? $"Board {k + 1}" : Names[k].Trim();

    public void Save()
    {
        try { System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this, Json)); }
        catch (IOException e) { Program.Log(e); }
    }

    public Cfg Copy() => JsonSerializer.Deserialize(JsonSerializer.Serialize(this, Json), Json)!;

    public static Cfg Load()
    {
        Cfg c, d = new();
        try
        {
            if (!System.IO.File.Exists(File)) d.Save();
            c = JsonSerializer.Deserialize(System.IO.File.ReadAllText(File), Json) ?? d;
        }
        catch (Exception e) { MessageBox.Show($"settings.json not read, defaults used.\n{e.Message}", Program.Name); c = d; }
        c.Boards = Math.Clamp(c.Boards, 1, 4);
        c.SlideMs = Math.Clamp(c.SlideMs, 0, 2000);
        c.HoldMs = Math.Clamp(c.HoldMs, 100, 3000);
        c.ArrowHideMs = Math.Clamp(c.ArrowHideMs, 500, 10000);
        c.Names = Enumerable.Range(0, 4).Select(k => c.Names?.ElementAtOrDefault(k) ?? d.Names[k]).ToArray();
        c.Shortcuts ??= d.Shortcuts;
        foreach (var a in Hotkey.All) if (!c.Shortcuts.ContainsKey(a.Id)) c.Shortcuts[a.Id] = a.Default;
        c.Window ??= [];
        if (c.OnExit is not ("ask" or "keep" or "close")) c.OnExit = d.OnExit;
        try { if (ColorTranslator.FromHtml(c.Back).IsEmpty) c.Back = d.Back; } catch { c.Back = d.Back; }
        try { _ = new Regex(c.Skip); } catch { c.Skip = d.Skip; }
        return c;
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(Cfg))] partial class CfgJson : System.Text.Json.Serialization.JsonSerializerContext;

// Shortcuts: stored as text ("Ctrl+Shift+Left"), registered with Windows only while Pinboard or MultiViewer is in front.
static class Hotkey
{
    public static readonly (string Id, string Label, string Default)[] All =
    [
        ("prev", "Previous board", "Ctrl+Left"),
        ("next", "Next board", "Ctrl+Right"),
        ("sendPrev", "Send player to previous board", "Ctrl+Shift+Left"),
        ("sendNext", "Send player to next board", "Ctrl+Shift+Right"),
        ("menu", "Player list and menu", "Ctrl+Up"),
        ("full", "Full screen", "F11"),
        ("board1", "Board 1", "Ctrl+1"), ("board2", "Board 2", "Ctrl+2"), ("board3", "Board 3", "Ctrl+3"), ("board4", "Board 4", "Ctrl+4"),
    ];

    // "Ctrl+Shift+Left" -> MOD_CONTROL | MOD_SHIFT, VK_LEFT. Empty or unknown: null (no shortcut).
    public static (uint Mods, uint Vk)? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        uint mods = 0; Keys key = Keys.None;
        foreach (var t in s.Split('+', StringSplitOptions.TrimEntries))
            switch (t.ToLowerInvariant())
            {
                case "ctrl": mods |= 0x2; break;
                case "alt": mods |= 0x1; break;
                case "shift": mods |= 0x4; break;
                case "win": mods |= 0x8; break;
                default:
                    if (t.Length == 1 && char.IsDigit(t[0])) key = Keys.D0 + (t[0] - '0');
                    else if (!Enum.TryParse(t, true, out key)) return null;
                    break;
            }
        return key == Keys.None || ((mods & 0x3) == 0 && key is < Keys.F1 or > Keys.F24) ? null : (mods, (uint)key); // F1-F24 need no Ctrl or Alt
    }

    // Key press from a KeyDown event as stored text; needs Ctrl or Alt so plain typing never triggers it.
    public static string? From(Keys data)
    {
        var k = data & Keys.KeyCode;
        if (k is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin or Keys.None) return null;
        if ((data & (Keys.Control | Keys.Alt)) == 0 && k is < Keys.F1 or > Keys.F24) return null;
        var name = k is >= Keys.D0 and <= Keys.D9 ? ((char)('0' + (k - Keys.D0))).ToString() : k.ToString();
        return string.Concat((data & Keys.Control) != 0 ? "Ctrl+" : "", (data & Keys.Alt) != 0 ? "Alt+" : "", (data & Keys.Shift) != 0 ? "Shift+" : "", name);
    }

    public static string Show(string? s) => string.IsNullOrEmpty(s) ? "" :
        s.Replace("Left", "←").Replace("Right", "→").Replace("Up", "↑").Replace("Down", "↓");
}
