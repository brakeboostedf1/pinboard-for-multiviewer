// Installer and uninstaller, built into Pinboard.exe. Run as "Pinboard-Setup.exe" (or with --setup) it installs;
// Windows Settings > Apps runs "Pinboard.exe --uninstall". Per-user by default: no admin prompt unless the folder needs it.
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;

static class Setup
{
    const string Name = "Pinboard for MultiViewer", Exe = "Pinboard.exe";
    const string Uninstall = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Pinboard";
    const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Pinboard");
    static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Pinboard.lnk");
    static string MenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Pinboard.lnk");
    static string Version => Application.ProductVersion.Split('+')[0];

    // A development build needs Pinboard.dll beside it; installing it would leave a copy that cannot start.
    public static bool Packaged(bool say = true)
    {
        bool ok = !File.Exists(Path.Combine(AppContext.BaseDirectory, "Pinboard.dll"));
        if (!ok && say) MessageBox.Show("This is a development build and cannot install.\nUse Pinboard-Setup.exe.", "Pinboard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return ok;
    }

    public static bool IsSetup(string[] args) =>
        args.Contains("--setup") || Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Contains("setup", StringComparison.OrdinalIgnoreCase);

    record Choice(string Dir, bool Desktop, bool Menu, bool AutoStart);

    // ---- Setup wizard ----

    // Classic Windows setup wizard: Welcome, Location, Tasks, Ready, Installing, Finish. System colours, fonts, buttons.
    public static int Show()
    {
        var icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!)!;
        var f = new Form
        {
            Text = $"Setup - {Name}", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterScreen, Font = SystemFonts.MessageBoxFont, Icon = icon,
            AutoScaleMode = AutoScaleMode.Dpi, AutoScaleDimensions = new(96, 96), ClientSize = new(497, 360),
        };
        var bold = new Font(f.Font, FontStyle.Bold);
        var big = new Font(f.Font.FontFamily, 12f, FontStyle.Bold);
        Label L(string s, int x, int y, int w, Font? font = null) => new() { Text = s, Location = new(x, y), AutoSize = true, MaximumSize = new(w, 0), Font = font ?? f.Font, BackColor = Color.Transparent };
        Button B(string s, int x) => new() { Text = s, Location = new(x, 327), Size = new(75, 23), FlatStyle = FlatStyle.System };

        // Welcome and Finish: banner left, white page right.
        Panel Side(string title, string text)
        {
            var p = new Panel { Bounds = new(0, 0, 497, 313), BackColor = SystemColors.Window };
            var banner = new PictureBox { Bounds = new(0, 0, 164, 313), BackColor = ColorTranslator.FromHtml("#111111"), SizeMode = PictureBoxSizeMode.CenterImage };
            using (var large = Icon.ExtractIcon(Environment.ProcessPath!, 0, 128)!) banner.Image = large.ToBitmap();
            p.Controls.Add(banner);
            p.Controls.Add(L(title, 176, 16, 305, big));
            p.Controls.Add(L(text, 176, 80, 305));
            return p;
        }
        // Inner pages: white header with title, subtitle and icon; grey body.
        Panel Inner(string title, string sub, out Panel body)
        {
            var p = new Panel { Bounds = new(0, 0, 497, 313), BackColor = SystemColors.Control };
            var head = new Panel { Bounds = new(0, 0, 497, 58), BackColor = SystemColors.Window };
            head.Controls.Add(L(title, 24, 10, 380, bold));
            head.Controls.Add(L(sub, 40, 28, 380));
            head.Controls.Add(new PictureBox { Bounds = new(433, 5, 48, 48), Image = Icon.ExtractIcon(Environment.ProcessPath!, 0, 48)!.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom });
            p.Controls.Add(head);
            p.Controls.Add(new Label { Bounds = new(0, 58, 497, 2), BorderStyle = BorderStyle.Fixed3D });
            body = new Panel { Bounds = new(40, 72, 417, 230), BackColor = SystemColors.Control };
            p.Controls.Add(body);
            return p;
        }

        var welcome = Side($"Welcome to the {Name} Setup Wizard",
            $"This will install {Name} {Version} on your computer.\n\nPinboard holds MultiViewer windows on boards. One board shows at a time.\n\nClick Next to continue, or Cancel to exit Setup.");

        var where = Inner("Select Destination Location", "Where should Pinboard be installed?", out var b1);
        b1.Controls.Add(L("Setup will install Pinboard into the following folder.\n\nTo continue, click Next. If you would like to select a different folder, click Browse.", 0, 0, 417));
        var dir = new TextBox { Text = Installed() ?? DefaultDir, Bounds = new(0, 74, 330, 23) };
        var browse = new Button { Text = "Browse...", Bounds = new(342, 73, 75, 23), FlatStyle = FlatStyle.System };
        browse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { SelectedPath = dir.Text, UseDescriptionForTitle = true, Description = "Select install folder" };
            if (d.ShowDialog(f) == DialogResult.OK)
                dir.Text = d.SelectedPath.EndsWith("Pinboard", StringComparison.OrdinalIgnoreCase) ? d.SelectedPath : Path.Combine(d.SelectedPath, "Pinboard");
        };
        b1.Controls.AddRange([dir, browse]);
        long mb = (new FileInfo(Environment.ProcessPath!).Length >> 20) + 1;
        b1.Controls.Add(L($"At least {mb} MB of free disk space is required.", 0, 210, 417));

        var tasks = Inner("Select Additional Tasks", "Which additional tasks should be performed?", out var b2);
        b2.Controls.Add(L("Select the additional tasks you would like Setup to perform while installing Pinboard, then click Next.", 0, 0, 417));
        CheckBox C(string s, bool on, int y) => new() { Text = s, Checked = on, Location = new(0, y), AutoSize = true, FlatStyle = FlatStyle.System };
        var desk = C("Create a desktop shortcut", true, 48);
        var menu = C("Create a Start menu entry", true, 72);
        var auto = C("Start Pinboard when Windows starts", false, 96);
        b2.Controls.AddRange([desk, menu, auto]);

        var ready = Inner("Ready to Install", "Setup is now ready to begin installing Pinboard on your computer.", out var b3);
        b3.Controls.Add(L("Click Install to continue with the installation, or click Back if you want to review or change any settings.", 0, 0, 417));
        var summary = new TextBox { Bounds = new(0, 44, 417, 180), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window, TabStop = false };
        b3.Controls.Add(summary);

        var busy = Inner("Installing", "Please wait while Setup installs Pinboard on your computer.", out var b4);
        b4.Controls.Add(L("Installing files...", 0, 0, 417));
        b4.Controls.Add(new ProgressBar { Bounds = new(0, 24, 417, 21), Style = ProgressBarStyle.Marquee });

        var done = Side($"Completing the {Name} Setup Wizard",
            "Setup has finished installing Pinboard on your computer. The application may be launched by selecting the installed shortcuts.\n\nClick Finish to exit Setup.");
        var launch = new CheckBox { Text = "Launch Pinboard", Checked = true, Location = new(176, 200), AutoSize = true, FlatStyle = FlatStyle.System, BackColor = SystemColors.Window };
        done.Controls.Add(launch);

        Panel[] pages = [welcome, where, tasks, ready, busy, done];
        f.Controls.AddRange(pages);
        f.Controls.Add(new Label { Bounds = new(0, 313, 497, 2), BorderStyle = BorderStyle.Fixed3D });
        var back = B("< Back", 246); var next = B("Next >", 321); var cancel = B("Cancel", 411);
        f.Controls.AddRange([back, next, cancel]);
        f.AcceptButton = next;

        int page = 0, code = 1;
        void Go(int i)
        {
            page = i;
            for (int k = 0; k < pages.Length; k++) pages[k].Visible = k == i;
            back.Visible = i is > 0 and < 4;
            next.Text = i == 3 ? "Install" : i == 5 ? "Finish" : "Next >";
            next.Enabled = i != 4;
            cancel.Enabled = i < 4;
            if (i == 3)
                summary.Text = $"Destination location:\r\n      {dir.Text}\r\n\r\nAdditional tasks:\r\n" +
                    string.Concat(new[] { desk, menu, auto }.Where(c => c.Checked).Select(c => $"      {c.Text}\r\n"));
            next.Focus();
        }
        back.Click += (_, _) => Go(page - 1);
        next.Click += (_, _) =>
        {
            switch (page)
            {
                case 1 when !Path.IsPathFullyQualified(dir.Text.Trim()):
                    MessageBox.Show(f, "You must enter a full path with drive letter.", f.Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                case 3:
                    Go(4); Application.DoEvents();
                    var ch = new Choice(dir.Text.Trim(), desk.Checked, menu.Checked, auto.Checked);
                    try { Install(ch); }
                    catch (UnauthorizedAccessException) when (Elevate(ch) == 0) { }
                    catch (Exception e)
                    {
                        MessageBox.Show(f, $"Setup could not install Pinboard in this folder.\n\n{e.Message}", f.Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Go(1); return;
                    }
                    code = 0; Go(5); return;
                case 5:
                    if (launch.Checked) Process.Start(new ProcessStartInfo(Path.Combine(dir.Text.Trim(), Exe)) { UseShellExecute = true, WorkingDirectory = dir.Text.Trim() });
                    f.Close(); return;
            }
            Go(page + 1);
        };
        cancel.Click += (_, _) => f.Close();
        f.FormClosing += (_, e) =>
        {
            if (page >= 4 || code == 0) return;
            e.Cancel = MessageBox.Show(f, "Setup is not complete. If you exit now, the program will not be installed.\n\nExit Setup?",
                "Exit Setup", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes;
        };
        Go(int.TryParse(Environment.GetEnvironmentVariable("PINBOARD_SETUP_PAGE"), out var p0) ? p0 : 0); // test aid: open on a page
        Application.Run(f);
        return code;
    }

    // Folder needs admin rights: run the same install again as administrator.
    static int Elevate(Choice c)
    {
        var a = $"--install \"{c.Dir}\"" + (c.Desktop ? " --desktop" : "") + (c.Menu ? " --menu" : "") + (c.AutoStart ? " --autostart" : "");
        try { using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, a) { UseShellExecute = true, Verb = "runas" })!; p.WaitForExit(); return p.ExitCode; }
        catch { return 1; } // admin prompt declined
    }

    // Silent install, used by the elevated run.
    public static int FromArgs(string[] a)
    {
        int i = Array.IndexOf(a, "--install");
        Install(new(a[i + 1], a.Contains("--desktop"), a.Contains("--menu"), a.Contains("--autostart")));
        return 0;
    }

    static void Install(Choice c)
    {
        Quit();
        Directory.CreateDirectory(c.Dir);
        string exe = Path.Combine(c.Dir, Exe);
        if (!string.Equals(Path.GetFullPath(exe), Path.GetFullPath(Environment.ProcessPath!), StringComparison.OrdinalIgnoreCase))
            for (int n = 0; ; n++) // the old copy may take a moment to let go of its file
                try { File.Copy(Environment.ProcessPath!, exe, true); break; }
                catch (IOException) when (n < 20) { Thread.Sleep(250); }
        string cfg = Path.Combine(c.Dir, "pinboard.json"); // keep a user's own settings on update
        if (!File.Exists(cfg)) File.WriteAllText(cfg, JsonSerializer.Serialize(new Cfg(), new JsonSerializerOptions { WriteIndented = true }));
        Link(DesktopLink, exe, c.Desktop);
        Link(MenuLink, exe, c.Menu);
        using (var run = Registry.CurrentUser.CreateSubKey(Run)) { if (c.AutoStart) run.SetValue("Pinboard", $"\"{exe}\""); else run.DeleteValue("Pinboard", false); }
        using (var u = Registry.CurrentUser.CreateSubKey(Uninstall))
        {
            u.SetValue("DisplayName", Name);
            u.SetValue("DisplayVersion", Version);
            u.SetValue("Publisher", "Pinboard");
            u.SetValue("DisplayIcon", exe);
            u.SetValue("InstallLocation", c.Dir);
            u.SetValue("UninstallString", $"\"{exe}\" --uninstall");
            u.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
            u.SetValue("NoModify", 1, RegistryValueKind.DWord);
            u.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
    }

    static string? Installed()
    {
        using var u = Registry.CurrentUser.OpenSubKey(Uninstall);
        return u?.GetValue("InstallLocation") as string;
    }

    static void Link(string at, string target, bool want)
    {
        if (!want) { File.Delete(at); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(at)!);
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic l = shell.CreateShortcut(at);
        l.TargetPath = target; l.WorkingDirectory = Path.GetDirectoryName(target); l.Description = Name; l.IconLocation = target + ",0";
        l.Save();
    }

    // A running Pinboard exits cleanly (its windows come back on screen) so its file can be replaced or removed.
    static void Quit()
    {
        N.PostMessage(0xFFFF, Program.QuitMsg, 0, 0);
        foreach (var p in Process.GetProcessesByName("Pinboard").Where(p => p.Id != Environment.ProcessId))
            using (p) if (!p.WaitForExit(5000)) p.Kill();
    }

    // ---- Uninstall ----

    public static int Remove()
    {
        if (MessageBox.Show($"Remove {Name}?\nMultiViewer and its setups stay as they are.", "Uninstall Pinboard",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return 1;
        Quit();
        string dir = AppContext.BaseDirectory.TrimEnd('\\');
        File.Delete(DesktopLink);
        File.Delete(MenuLink);
        using (var run = Registry.CurrentUser.OpenSubKey(Run, true)) run?.DeleteValue("Pinboard", false);
        Registry.CurrentUser.DeleteSubKeyTree(Uninstall, false);
        try { Directory.Delete(Program.Data, true); } catch (IOException) { }
        // This exe is still running: a short-lived cmd removes the folder once it has exited.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{dir}\"") { CreateNoWindow = true, UseShellExecute = false });
        return 0;
    }
}
