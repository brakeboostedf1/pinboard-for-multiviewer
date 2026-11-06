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

