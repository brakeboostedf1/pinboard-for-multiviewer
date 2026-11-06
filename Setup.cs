// Installer and uninstaller, built into Pinboard.exe.
// Setup (exe name contains "setup", or --setup): wizard runs as the user; files go to Program Files for all users
// through one admin prompt (--install). Uninstall (Windows Settings > Apps) runs "Pinboard.exe --uninstall".
// Only Pinboard's own files are ever deleted; a folder goes only when it is empty.
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

static class Setup
{
    const string Name = Program.Name, Exe = "Pinboard.exe";
    const string Entry = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PinboardForMultiViewer";
    const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Name);
    static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), Name + ".lnk");
    static string MenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Name + ".lnk");
    static string Version => Application.ProductVersion.Split('+')[0];

    // A development build needs Pinboard.dll beside it; installing it would leave a copy that cannot start.
    public static bool Packaged(bool say = true)
    {
        bool ok = !File.Exists(Path.Combine(AppContext.BaseDirectory, "Pinboard.dll"));
        if (!ok && say) MessageBox.Show("This is a development build and cannot install.\nUse the setup file.", Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return ok;
    }

    public static bool IsSetup(string[] args) =>
        args.Contains("--setup") || Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Contains("setup", StringComparison.OrdinalIgnoreCase);

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
            using (var small = Icon.ExtractIcon(Environment.ProcessPath!, 0, 48)!)
                head.Controls.Add(new PictureBox { Bounds = new(433, 5, 48, 48), Image = small.ToBitmap(), SizeMode = PictureBoxSizeMode.Zoom });
            p.Controls.Add(head);
            p.Controls.Add(new Label { Bounds = new(0, 58, 497, 2), BorderStyle = BorderStyle.Fixed3D });
            body = new Panel { Bounds = new(40, 72, 417, 230), BackColor = SystemColors.Control };
            p.Controls.Add(body);
            return p;
        }

        var welcome = Side($"Welcome to the {Name} Setup Wizard",
            $"This will install {Name} {Version} on your computer.\n\nPinboard holds MultiViewer windows on boards. One board shows at a time.\n\nClick Next to continue, or Cancel to exit Setup.");

        var where = Inner("Select Destination Location", $"Where should {Name} be installed?", out var b1);
        b1.Controls.Add(L("Setup will install Pinboard into the following folder.\n\nTo continue, click Next. If you would like to select a different folder, click Browse.", 0, 0, 417));
        var dir = new TextBox { Text = Installed() ?? DefaultDir, Bounds = new(0, 74, 330, 23) };
        var browse = new Button { Text = "Browse...", Bounds = new(342, 73, 75, 23), FlatStyle = FlatStyle.System };
        browse.Click += (_, _) =>
        {
            using var d = new FolderBrowserDialog { SelectedPath = dir.Text, UseDescriptionForTitle = true, Description = "Select install folder" };
            if (d.ShowDialog(f) == DialogResult.OK)
                dir.Text = d.SelectedPath.EndsWith(Name, StringComparison.OrdinalIgnoreCase) ? d.SelectedPath : Path.Combine(d.SelectedPath, Name);
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
            string d = dir.Text.Trim();
            switch (page)
            {
                case 1 when !Path.IsPathFullyQualified(d) || d.IndexOfAny(['"', '%']) >= 0 || Path.GetPathRoot(d) == d:
                    MessageBox.Show(f, "You must enter a full folder path with drive letter.", f.Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                case 3:
                    Go(4);
                    // Files and the all-users entry need admin rights: one admin prompt. Start with Windows is per user.
                    var args = $"--install \"{d}\"" + (desk.Checked ? " --desktop" : "") + (menu.Checked ? " --menu" : "");
                    var run = Task.Run(() => Admin(args));
                    while (!run.IsCompleted) { Application.DoEvents(); Thread.Sleep(30); }
                    if (run.Result != 0)
                    {
                        if (run.Result == 1223) MessageBox.Show(f, "Setup needs administrator rights to install Pinboard.", f.Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                        Go(1); return;
                    }
                    ForUser(Path.Combine(d, Exe), auto.Checked);
                    code = 0; Go(5); return;
                case 5:
                    if (launch.Checked) Process.Start(new ProcessStartInfo(Path.Combine(d, Exe)) { UseShellExecute = true, WorkingDirectory = d });
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
        Go(0);
        Application.Run(f);
        return code;
    }

    // Runs this exe again as administrator and waits. 1223: admin prompt declined.
    static int Admin(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, args) { UseShellExecute = true, Verb = "runas" })!;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Win32Exception e) { return e.NativeErrorCode; }
    }

    // Admin part of setup: files, shortcuts and uninstall entry for all users.
    public static int FromArgs(string[] a)
    {
        int i = Array.IndexOf(a, "--install");
        if (i + 1 >= a.Length) return 1;
        try { Install(a[i + 1], a.Contains("--desktop"), a.Contains("--menu")); return 0; }
        catch (Exception e)
        {
            MessageBox.Show($"Setup could not install Pinboard in this folder.\n\n{e.Message}", Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
    }

    static void Install(string dir, bool desktop, bool menu)
    {
        Quit();
        Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, Exe);
        if (!string.Equals(Path.GetFullPath(exe), Path.GetFullPath(Environment.ProcessPath!), StringComparison.OrdinalIgnoreCase))
            for (int n = 0; ; n++) // the old copy may take a moment to let go of its file
                try { File.Copy(Environment.ProcessPath!, exe, true); break; }
                catch (IOException) when (n < 20) { Thread.Sleep(250); }
        Link(DesktopLink, exe, desktop);
        Link(MenuLink, exe, menu);
        using var u = Registry.LocalMachine.CreateSubKey(Entry);
        u.SetValue("DisplayName", Name);
        u.SetValue("DisplayVersion", Version);
        u.SetValue("Publisher", Name);
        u.SetValue("DisplayIcon", exe);
        u.SetValue("InstallLocation", dir);
        u.SetValue("UninstallString", $"\"{exe}\" --uninstall");
        u.SetValue("EstimatedSize", (int)(new FileInfo(exe).Length / 1024), RegistryValueKind.DWord);
        u.SetValue("NoModify", 1, RegistryValueKind.DWord);
        u.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    // User part of setup: start with Windows, and Pinboard 1.0 (per user, named "Pinboard") removed.
    static void ForUser(string exe, bool autoStart)
    {
        using (var run = Registry.CurrentUser.CreateSubKey(Run))
        {
            if (autoStart) run.SetValue(Name, $"\"{exe}\""); else run.DeleteValue(Name, false);
            run.DeleteValue("Pinboard", false);
        }
        const string old = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Pinboard";
        using (var u = Registry.CurrentUser.OpenSubKey(old))
            if (u?.GetValue("InstallLocation") is string d && Directory.Exists(d))
            {
                Delete(d, "Pinboard.exe", "pinboard.json");
                foreach (var l in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Programs })
                {
                    var lnk = Path.Combine(Environment.GetFolderPath(l), "Pinboard.lnk");
                    if (File.Exists(lnk) && Target(lnk).StartsWith(d, StringComparison.OrdinalIgnoreCase)) File.Delete(lnk);
                }
            }
        Registry.CurrentUser.DeleteSubKeyTree(old, false);
        Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pinboard"), "pinboard.log", "help-hidden", "selftest.txt");
    }

    static string? Installed()
    {
        using var u = Registry.LocalMachine.OpenSubKey(Entry);
        return u?.GetValue("InstallLocation") as string;
    }

    static dynamic Shell() => Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;

    static string Target(string lnk) { try { return (string)Shell().CreateShortcut(lnk).TargetPath; } catch { return ""; } }

    static void Link(string at, string target, bool want)
    {
        if (!want) { File.Delete(at); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(at)!);
        dynamic l = Shell().CreateShortcut(at);
        l.TargetPath = target; l.WorkingDirectory = Path.GetDirectoryName(target); l.Description = Name; l.IconLocation = target + ",0";
        l.Save();
    }

    // Named files out of a folder, then the folder itself if nothing else is in it.
    static void Delete(string dir, params string[] files)
    {
        try
        {
            foreach (var f in files) File.Delete(Path.Combine(dir, f));
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // A running Pinboard exits cleanly (its windows come back on screen) so its file can be replaced or removed.
    // Nothing is killed: a Pinboard that does not exit makes the file copy fail with a message.
    static void Quit()
    {
        N.PostMessage(0xFFFF, Program.QuitMsg, 0, 0);
        foreach (var p in Process.GetProcessesByName("Pinboard").Where(p => p.Id != Environment.ProcessId))
            using (p) p.WaitForExit(10000);
    }

    // ---- Uninstall ----

    public static int Remove(string[] a)
    {
        if (a.Contains("--files")) return RemoveFiles();
        // Standard uninstall wording: ask, remove, confirm.
        string title = $"{Name} Uninstall";
        if (MessageBox.Show($"Are you sure you want to completely remove {Name} and all of its components?\n\nMultiViewer and its setups stay as they are.",
                title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 1;
        Quit();
        int r = Admin("--uninstall --files");
        if (r != 0)
        {
            MessageBox.Show(r == 1223 ? $"Uninstall needs administrator rights. {Name} was not removed." : $"{Name} could not be removed.",
                title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        using (var run = Registry.CurrentUser.OpenSubKey(Run, true)) run?.DeleteValue(Name, false);
        Delete(Program.Data, "settings.json", "pinboard.log", "help-hidden", "hidden.txt", "selftest.txt");
        MessageBox.Show($"{Name} was successfully removed from your computer.", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    // Admin part: only from the folder Setup recorded, only Pinboard's own file.
    static int RemoveFiles()
    {
        string me = Path.GetDirectoryName(Environment.ProcessPath!)!.TrimEnd('\\');
        if (!string.Equals(Installed()?.TrimEnd('\\'), me, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("Uninstall from Windows Settings > Apps.", Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 1;
        }
        Quit();
        File.Delete(DesktopLink);
        File.Delete(MenuLink);
        Registry.LocalMachine.DeleteSubKeyTree(Entry, false);
        // This exe is still running: a short-lived cmd deletes it once it has exited, then the folder if empty.
        string sys = Environment.SystemDirectory;
        Process.Start(new ProcessStartInfo(Path.Combine(sys, "cmd.exe"),
            $"/d /c {Path.Combine(sys, "PING.EXE")} -n 3 127.0.0.1 >nul & del /f /q \"{Path.Combine(me, Exe)}\" & rmdir \"{me}\"")
            { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = sys });
        return 0;
    }
}
