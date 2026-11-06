// MultiViewer setups: its own file, read only. A window saved in a parking place belongs to that board; a window saved
// on screen belongs to the board on screen at save time, which Pinboard notes when MultiViewer writes the file.
using System.Text.Json;

static class Setups
{
    public static readonly string MvFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MultiViewer", "setups.config.json");
    static string NoteFile => Path.Combine(Program.Data, "setup-boards.txt"); // one "setup id <tab> board" per line

    // Windows: their saved state as text, to see which setups a save changed. Rects: saved bounds in screen px.
    // Shown: board on screen at save, when known with the setup itself (last session).
    public record Setup(string Id, string Updated, string Windows, List<Rectangle> Rects, int? Shown = null);

    public static List<Setup>? Read() // null: not readable now (MultiViewer may be writing it)
    {
        if (!File.Exists(MvFile)) return [];
        try
        {
            using var d = JsonDocument.Parse(File.ReadAllText(MvFile));
            var list = new List<Setup>();
            foreach (var s in d.RootElement.GetProperty("setups").EnumerateArray())
            {
                // MultiViewer stores bounds in its own units: screen px / display scale
                var scale = new Dictionary<long, double>();
                if (s.TryGetProperty("displays", out var ds))
                    foreach (var x in ds.EnumerateArray()) scale[x.GetProperty("id").GetInt64()] = x.GetProperty("scaleFactor").GetDouble();
                var ws = s.GetProperty("windows");
                var rects = new List<Rectangle>();
                foreach (var w in ws.EnumerateArray())
                {
                    var st = w.GetProperty("windowState");
                    var b = st.GetProperty("bounds");
                    double f = st.TryGetProperty("displayId", out var id) && scale.TryGetValue(id.GetInt64(), out var v) ? v : 1;
                    int P(string k) => (int)Math.Round(b.GetProperty(k).GetDouble() * f);
                    rects.Add(new(P("x"), P("y"), P("width"), P("height")));
                }
                list.Add(new(s.GetProperty("id").GetString() ?? "", s.TryGetProperty("updatedAt", out var u) ? u.GetString() ?? "" : "", ws.GetRawText(), rects));
            }
            return list;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    // Last session: where each window was when MultiViewer closed (or the last window went), and the board on screen then.
    // MultiViewer opens its last session again when it starts, at those places, so it is one more setup to match.
    static string SessionFile => Path.Combine(Program.Data, "session.txt"); // board, then one "x y width height" per window

    public static Setup? Session()
    {
        try
        {
            var l = File.ReadAllLines(SessionFile);
            var rects = l.Skip(1).Select(s => s.Split(' ').Select(int.Parse).ToArray()).Where(a => a.Length == 4).Select(a => new Rectangle(a[0], a[1], a[2], a[3])).ToList();
            return new("session", File.GetLastWriteTimeUtc(SessionFile).ToString("o"), "", rects, int.Parse(l[0]));
        }
        catch (Exception e) when (e is IOException or FormatException or IndexOutOfRangeException) { return null; }
    }

    public static void ForgetSession() { try { File.Delete(SessionFile); } catch (IOException) { } }

    public static Setup SaveSession(List<Rectangle> rects, int board)
    {
        try { File.WriteAllLines(SessionFile, rects.Select(r => $"{r.X} {r.Y} {r.Width} {r.Height}").Prepend(board.ToString())); }
        catch (IOException e) { Program.Log(e); }
        return new("session", DateTime.UtcNow.ToString("o"), "", rects, board);
    }

    public static Dictionary<string, int> Notes()
    {
        var n = new Dictionary<string, int>();
        try { foreach (var l in File.ReadAllLines(NoteFile)) if (l.Split('\t') is [var id, var k] && int.TryParse(k, out var b)) n[id] = b; }
        catch (IOException) { }
        return n;
    }

    public static void Save(Dictionary<string, int> notes)
    {
        try { File.WriteAllLines(NoteFile, notes.Select(a => $"{a.Key}\t{a.Value}")); }
        catch (IOException e) { Program.Log(e); }
    }

    // Board on screen at save: as noted, else the first board with nothing parked (Pinboard was not running: board 1).
    public static int ScreenBoard(IEnumerable<int> zones, int boards, int? noted)
    {
        if (noted is { } n) return Math.Clamp(n, 0, boards - 1);
        var used = zones.Where(z => z >= 0).ToHashSet();
        for (int k = 0; k < boards; k++) if (!used.Contains(k)) return k;
        return 0;
    }

    // Saved window that r is (every side within tol px; MultiViewer can adjust a size for aspect ratio), nearest first; -1: none.
    public static int Match(Rectangle r, IList<Rectangle> saved, ISet<int> taken, int tol = 24)
    {
        int best = -1, bestD = int.MaxValue;
        for (int i = 0; i < saved.Count; i++)
        {
            if (taken.Contains(i)) continue;
            var s = saved[i];
            int dl = Math.Abs(r.Left - s.Left), dt = Math.Abs(r.Top - s.Top), dr = Math.Abs(r.Right - s.Right), db = Math.Abs(r.Bottom - s.Bottom);
            if (Math.Max(Math.Max(dl, dt), Math.Max(dr, db)) > tol) continue;
            if (dl + dt + dr + db < bestD) { best = i; bestD = dl + dt + dr + db; }
        }
        return best;
    }

    // Setup new windows come from: the one that matches most of them; equal: the one saved last. Null: none matches.
    public static Setup? Best(IList<Rectangle> arrived, IEnumerable<Setup> setups)
    {
        Setup? best = null; int most = 0;
        foreach (var s in setups.OrderByDescending(s => s.Updated, StringComparer.Ordinal))
        {
            var taken = new HashSet<int>();
            int n = arrived.Count(r => Match(r, s.Rects, taken) is var i and >= 0 && taken.Add(i));
            if (n > most) { most = n; best = s; }
        }
        return best;
    }

    // Checks the rules on a saved layout like a real one; lines go into the self-test report.
    public static int SelfTest(List<string> lines)
    {
        int fails = 0;
        void Check(bool ok, string what) { if (!ok) fails++; lines.Add($"{(ok ? "PASS" : "FAIL")}  setups: {what}"); }
        Check(ScreenBoard([0, 0, 1, 3, -1, -1], 4, null) == 2, "on-screen board = first board with nothing parked");
        Check(ScreenBoard([-1, -1], 4, null) == 0, "nothing parked: board 1");
        Check(ScreenBoard([0, -1], 4, 3) == 3, "noted board wins");
        Check(ScreenBoard([0, -1], 2, 3) == 1, "noted board past the board count: last board");
        Rectangle[] saved = [new(-2, 695, 619, 349), new(1922, -2, 1246, 701), new(5, 3, 1008, 568)];
        Check(Match(new(-2, 695, 620, 349), saved, new HashSet<int>()) == 0, "size adjusted by 1 px still matches");
        Check(Match(new(5, 3, 1008, 568), saved, new HashSet<int> { 2 }) == -1, "a saved window matches one window only");
        Check(Match(new(400, 400, 300, 200), saved, new HashSet<int>()) == -1, "unknown window: no match");
        var a = new Setup("a", "2026-01-01", "", [new(-2, 695, 619, 349), new(0, 0, 800, 600)]);
        var b = new Setup("b", "2026-02-01", "", [new(-2, 695, 619, 349), new(1922, -2, 1246, 701)]);
        Check(Best([new(-2, 695, 619, 349)], [a, b])?.Id == "b", "same match count: setup saved last");
        Check(Best([new(-2, 695, 619, 349), new(0, 0, 800, 600)], [a, b])?.Id == "a", "more matches wins");
        Check(Best([new(7, 7, 7, 7)], [a, b]) == null, "no match: no setup");
        return fails;
    }
}
