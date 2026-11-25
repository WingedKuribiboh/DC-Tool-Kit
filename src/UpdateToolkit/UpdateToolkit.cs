// Update Toolkit - GUI front end for the toolkit update modules (src\Scripts\Update\update_isos.py,
// update_tools.py). It runs the scripts, reads their progress output and shows it as progress bars.
// Targets .NET Framework 4.5+ / C# 5 so it builds with the csc.exe that ships with Windows.
//
//   UpdateToolkit.exe                 open the window
//   UpdateToolkit.exe all|isos|tools  open and start that run immediately
//   add /check for "check only" (downloads nothing), /force to re-download, /root:X:\ to override the drive root
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;

class Mod
{
    public string Label, Script;
    public Mod(string label, string script) { Label = label; Script = script; }
}

class MainForm : Form
{
    const string PyUrl = "https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-";

    static readonly Mod Isos = new Mod("ISOs", "update_isos.py");
    static readonly Mod Tools = new Mod("Tools", "update_tools.py");

    string root;
    string updDir;
    Button btnAll, btnIso, btnTools, btnCancel, btnLog, btnRoot;
    CheckBox chkCheck, chkForce;
    Label lblRoot, lblStatus, lblItem, lblSummary;
    ProgressBar pbItem, pbAll;
    RichTextBox log;

    Thread worker;
    volatile bool cancel;
    Process proc;
    int modIndex, modCount;
    int entIndex = 1, entCount = 1;
    int totalOk, totalFail;
    string modLabel = "";

    public MainForm(string[] args)
    {
        Text = "Toolkit Updater";
        ClientSize = new Size(760, 600);
        MinimumSize = new Size(640, 480);
        Font = new Font("Segoe UI", 9f);
        StartPosition = FormStartPosition.CenterScreen;

        Label title = new Label();
        title.Text = "Toolkit Updater";
        title.Font = new Font("Segoe UI", 16f, FontStyle.Bold);
        title.AutoSize = true; title.Location = new Point(14, 10);
        Controls.Add(title);

        lblRoot = new Label();
        lblRoot.Location = new Point(16, 50); lblRoot.Size = new Size(600, 18);
        lblRoot.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(lblRoot);
        btnRoot = Btn("Change...", 650, 46, 94, 24, delegate { PickRoot(); });
        btnRoot.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        btnAll = Btn("Update All", 16, 80, 150, 36, delegate { Start(new Mod[] { Isos, Tools }); });
        btnIso = Btn("Update ISOs", 176, 80, 150, 36, delegate { Start(new Mod[] { Isos }); });
        btnTools = Btn("Update Tools", 336, 80, 150, 36, delegate { Start(new Mod[] { Tools }); });
        btnAll.Font = new Font(Font, FontStyle.Bold);
        btnCancel = Btn("Cancel", 496, 80, 100, 36, delegate { DoCancel(); });
        btnCancel.Enabled = false;

        chkCheck = new CheckBox(); chkCheck.Text = "Check only (download nothing)";
        chkCheck.Location = new Point(18, 124); chkCheck.AutoSize = true; Controls.Add(chkCheck);
        chkForce = new CheckBox(); chkForce.Text = "Force re-download";
        chkForce.Location = new Point(260, 124); chkForce.AutoSize = true; Controls.Add(chkForce);

        lblStatus = new Label();
        lblStatus.Text = "Ready.";
        lblStatus.Font = new Font(Font, FontStyle.Bold);
        lblStatus.Location = new Point(16, 156); lblStatus.Size = new Size(728, 18);
        lblStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(lblStatus);

        pbAll = new ProgressBar(); pbAll.Minimum = 0; pbAll.Maximum = 1000;
        pbAll.Location = new Point(16, 178); pbAll.Size = new Size(728, 18);
        pbAll.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(pbAll);

        lblItem = new Label();
        lblItem.Location = new Point(16, 204); lblItem.Size = new Size(728, 18);
        lblItem.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(lblItem);

        pbItem = new ProgressBar(); pbItem.Minimum = 0; pbItem.Maximum = 1000;
        pbItem.Location = new Point(16, 226); pbItem.Size = new Size(728, 18);
        pbItem.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(pbItem);

        log = new RichTextBox();
        log.ReadOnly = true; log.BackColor = Color.White; log.Font = new Font("Consolas", 9f);
        log.Location = new Point(16, 256); log.Size = new Size(728, 288);
        log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        log.WordWrap = false; log.ScrollBars = RichTextBoxScrollBars.Both;
        Controls.Add(log);

        lblSummary = new Label();
        lblSummary.Location = new Point(16, 556); lblSummary.Size = new Size(480, 22);
        lblSummary.Font = new Font(Font, FontStyle.Bold);
        lblSummary.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        Controls.Add(lblSummary);

        btnLog = Btn("Open ver.log", 520, 556, 110, 28, delegate { OpenVerLog(); });
        btnLog.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        Button btnClose = Btn("Close", 640, 556, 104, 28, delegate { Close(); });
        btnClose.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

        string rootArg = null, run = null;
        foreach (string a in args)
        {
            string l = a.ToLowerInvariant();
            if (l == "/check" || l == "-check") chkCheck.Checked = true;
            else if (l == "/force" || l == "-force") chkForce.Checked = true;
            else if (l.StartsWith("/root:")) rootArg = a.Substring(6);
            else if (l == "all" || l == "isos" || l == "tools") run = l;
        }
        SetRoot(rootArg != null ? rootArg : FindRoot());
        if (root == null) AppendLog("Could not find the toolkit drive (src\\Scripts\\Update\\update_isos.py). Click \"Change...\" and pick the drive root.\n", Color.DarkRed);

        if (run != null && root != null)
        {
            Shown += delegate
            {
                if (run == "all") Start(new Mod[] { Isos, Tools });
                else if (run == "isos") Start(new Mod[] { Isos });
                else Start(new Mod[] { Tools });
            };
        }
    }

    Button Btn(string text, int x, int y, int w, int h, EventHandler click)
    {
        Button b = new Button();
        b.Text = text; b.Location = new Point(x, y); b.Size = new Size(w, h);
        b.Click += click;
        Controls.Add(b);
        return b;
    }

    // ---------------------------------------------------------------- root

    // The updater scripts live in <drive root>\src\Scripts\Update.
    static readonly string UpdSubDir = Path.Combine("src", Path.Combine("Scripts", "Update"));

    static bool IsRoot(string dir)
    {
        return File.Exists(Path.Combine(dir, Path.Combine(UpdSubDir, "update_isos.py")));
    }

    static string FindRoot()
    {
        string d = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(d))
        {
            if (IsRoot(d)) return d;
            DirectoryInfo p = Directory.GetParent(d);
            d = p == null ? null : p.FullName;
        }
        return null;
    }

    void SetRoot(string r)
    {
        if (r != null && IsRoot(r))
        {
            root = r.TrimEnd('\\', '/');
            if (root.Length == 2 && root[1] == ':') root += "\\";
            updDir = Path.Combine(root, UpdSubDir);
            lblRoot.Text = "Toolkit drive: " + root;
        }
        else
        {
            root = null; updDir = null;
            lblRoot.Text = "Toolkit drive: (not found)";
        }
    }

    void PickRoot()
    {
        using (FolderBrowserDialog f = new FolderBrowserDialog())
        {
            f.Description = "Select the toolkit drive (the folder that contains ISO, Tools and src).";
            if (f.ShowDialog(this) != DialogResult.OK) return;
            SetRoot(f.SelectedPath);
            if (root == null) MessageBox.Show(this, "That folder does not contain src\\Scripts\\Update\\update_isos.py.", Text);
        }
    }

    void OpenVerLog()
    {
        if (updDir == null) return;
        string p = Path.Combine(updDir, "ver.log");
        if (File.Exists(p)) Process.Start("notepad.exe", "\"" + p + "\"");
        else MessageBox.Show(this, "ver.log does not exist yet. It is created after the first run.", Text);
    }

    // ---------------------------------------------------------------- UI helpers

    void Ui(Action a)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { try { BeginInvoke(a); } catch (InvalidOperationException) { } }
        else a();
    }

    void AppendLog(string text, Color c)
    {
        log.SelectionStart = log.TextLength;
        log.SelectionLength = 0;
        log.SelectionColor = c;
        log.AppendText(text);
        log.SelectionColor = log.ForeColor;
        log.ScrollToCaret();
    }

    void SetBusy(bool busy)
    {
        btnAll.Enabled = btnIso.Enabled = btnTools.Enabled = btnRoot.Enabled = !busy;
        chkCheck.Enabled = chkForce.Enabled = !busy;
        btnCancel.Enabled = busy;
    }

    void ItemMarquee(bool on)
    {
        if (on)
        {
            if (pbItem.Style != ProgressBarStyle.Marquee) { pbItem.Style = ProgressBarStyle.Marquee; pbItem.MarqueeAnimationSpeed = 30; }
        }
        else if (pbItem.Style != ProgressBarStyle.Continuous) pbItem.Style = ProgressBarStyle.Continuous;
    }

    void SetOverall(double entryFraction)
    {
        double perMod = 1.0 / Math.Max(1, modCount);
        double inMod = ((entIndex - 1) + entryFraction) / Math.Max(1, entCount);
        double v = (modIndex + inMod) * perMod;
        pbAll.Value = (int)Math.Max(0, Math.Min(1000, v * 1000));
    }

    static string Human(long n)
    {
        string[] u = { "B", "KB", "MB", "GB" };
        double d = n; int i = 0;
        while (d >= 1024 && i < u.Length - 1) { d /= 1024; i++; }
        return i == 0 ? n + " B" : d.ToString("0.0") + " " + u[i];
    }

    // ---------------------------------------------------------------- run control

    void Start(Mod[] mods)
    {
        if (root == null) { PickRoot(); if (root == null) return; }
        if (worker != null && worker.IsAlive) return;
        cancel = false;
        log.Clear();
        lblSummary.Text = "";
        lblSummary.ForeColor = Color.Black;
        pbAll.Value = 0;
        totalOk = totalFail = 0;
        SetBusy(true);
        bool check = chkCheck.Checked, force = chkForce.Checked;
        worker = new Thread(delegate() { Work(mods, check, force); });
        worker.IsBackground = true;
        worker.Start();
    }

    void DoCancel()
    {
        cancel = true;
        btnCancel.Enabled = false;
        lblStatus.Text = "Cancelling...";
        Process p = proc;
        if (p != null)
        {
            try { Process.Start(new ProcessStartInfo("taskkill", "/PID " + p.Id + " /T /F") { CreateNoWindow = true, UseShellExecute = false }); }
            catch (Exception) { try { p.Kill(); } catch (Exception) { } }
        }
    }

    void Work(Mod[] mods, bool check, bool force)
    {
        try
        {
            Ui(delegate { lblStatus.Text = "Looking for Python..."; ItemMarquee(true); lblItem.Text = ""; });
            string py = EnsurePython();
            if (py == null) throw new Exception("Python is not available.");

            modCount = mods.Length;
            for (int m = 0; m < mods.Length && !cancel; m++)
            {
                modIndex = m; modLabel = mods[m].Label; entIndex = 1; entCount = 1;
                RunModule(py, mods[m], check, force);
            }
            Ui(delegate { Finish(); });
        }
        catch (Exception ex)
        {
            string msg = ex.Message;
            Ui(delegate
            {
                AppendLog("ERROR: " + msg + "\n", Color.DarkRed);
                lblStatus.Text = "Stopped.";
                lblSummary.Text = "Failed to run."; lblSummary.ForeColor = Color.DarkRed;
                ItemMarquee(false); pbItem.Value = 0; lblItem.Text = "";
                SetBusy(false);
            });
        }
    }

    void Finish()
    {
        ItemMarquee(false);
        pbItem.Value = 0; lblItem.Text = "";
        if (cancel)
        {
            lblStatus.Text = "Cancelled.";
            lblSummary.Text = "Cancelled. Partial downloads resume next time.";
            lblSummary.ForeColor = Color.DarkOrange;
            AppendLog("\nCancelled. Partial downloads (*.part) resume on the next run.\n", Color.DarkOrange);
        }
        else
        {
            pbAll.Value = 1000;
            lblStatus.Text = "Finished.";
            lblSummary.Text = totalOk + " ok, " + totalFail + " failed";
            lblSummary.ForeColor = totalFail == 0 ? Color.DarkGreen : Color.DarkRed;
        }
        SetBusy(false);
    }

    void RunModule(string py, Mod mod, bool check, bool force)
    {
        string script = Path.Combine(updDir, mod.Script);
        if (!File.Exists(script)) throw new Exception("Missing " + script);

        StringBuilder a = new StringBuilder();
        a.Append("-X utf8 -u -I ").Append(Quote(script)).Append(" --machine --root ").Append(Quote(root));
        if (check) a.Append(" --list");
        if (force) a.Append(" --force");

        ProcessStartInfo psi = new ProcessStartInfo(py, a.ToString());
        psi.WorkingDirectory = updDir;
        psi.UseShellExecute = false; psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8; psi.StandardErrorEncoding = Encoding.UTF8;

        Ui(delegate { AppendLog("=== " + mod.Label + (check ? " (check only)" : "") + " ===\n", Color.Black); });
        using (Process p = new Process())
        {
            p.StartInfo = psi;
            p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) { string l = e.Data; Ui(delegate { HandleLine(l, false); }); } };
            p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) { string l = e.Data; Ui(delegate { HandleLine(l, true); }); } };
            proc = p;
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            p.WaitForExit();
            p.WaitForExit();   // flush async readers
            proc = null;
        }
    }

    // Quote one command-line argument. Backslashes right before the closing quote must be doubled,
    // otherwise a drive root like D:\ turns the quote into a literal (D:\" --list).
    static string Quote(string s)
    {
        int n = 0;
        while (n < s.Length && s[s.Length - 1 - n] == '\\') n++;
        return "\"" + s + new string('\\', n) + "\"";
    }

    // ---------------------------------------------------------------- protocol

    void HandleLine(string line, bool isErr)
    {
        if (line.StartsWith("@@"))
        {
            string[] p = line.Substring(2).Split('|');
            if (p[0] == "ENTRY" && p.Length >= 5)
            {
                int.TryParse(p[1], out entIndex); int.TryParse(p[2], out entCount);
                lblStatus.Text = modLabel + " " + entIndex + " of " + entCount + ": " + p[4];
                lblItem.Text = "Checking latest version...";
                ItemMarquee(true);
                SetOverall(0);
            }
            else if (p[0] == "PROG" && p.Length >= 4)
            {
                long done = 0, total = 0;
                long.TryParse(p[2], out done); long.TryParse(p[3], out total);
                if (total > 0)
                {
                    ItemMarquee(false);
                    double f = Math.Min(1.0, (double)done / total);
                    pbItem.Value = (int)(f * 1000);
                    lblItem.Text = Cap(p[1]) + ": " + Human(done) + " / " + Human(total) + "  (" + (int)(f * 100) + "%)";
                    SetOverall(f);
                }
                else
                {
                    ItemMarquee(true);
                    lblItem.Text = Cap(p[1]) + ": " + Human(done);
                }
            }
            else if (p[0] == "DONE" && p.Length >= 3)
            {
                int ok = 0, fail = 0;
                int.TryParse(p[1], out ok); int.TryParse(p[2], out fail);
                totalOk += ok; totalFail += fail;
                SetOverall(1.0);
            }
            return;
        }

        if (line.Trim().Length == 0) { return; }
        Color c = Color.Black;
        string u = line.ToUpperInvariant();
        if (isErr || u.Contains("ERROR") || u.Contains("FAILED") || u.Contains("NOT ENOUGH SPACE") || u.Contains("TRACEBACK")) c = Color.DarkRed;
        else if (u.Contains("UP TO DATE") || u.Contains("SHA256 OK") || u.Contains("UPDATED ->") || u.Contains("RECORDED AS CURRENT") || u.Contains("-> LOGGED")) c = Color.DarkGreen;
        else if (u.Contains("WOULD DOWNLOAD")) c = Color.DarkBlue;
        AppendLog(line + "\n", c);
    }

    static string Cap(string s) { return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1); }

    // ---------------------------------------------------------------- Python

    static bool PythonOk(string exe)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, "-c \"import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)\"");
            psi.UseShellExecute = false; psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
                if (!p.WaitForExit(10000)) { try { p.Kill(); } catch (Exception) { } return false; }
                return p.ExitCode == 0;
            }
        }
        catch (Exception) { return false; }
    }

    string EnsurePython()
    {
        string portable = Path.Combine(updDir, Path.Combine("python", "python.exe"));
        if (File.Exists(portable)) return portable;
        foreach (string cand in new string[] { "py", "python" })
            if (PythonOk(cand)) return cand;

        string arch = "amd64";
        string a1 = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE") ?? "";
        string a2 = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432") ?? "";
        if (a1.ToUpperInvariant() == "ARM64" || a2.ToUpperInvariant() == "ARM64") arch = "arm64";

        Ui(delegate { AppendLog("No Python found; downloading portable Python (about 11 MB) from python.org...\n", Color.Black); lblStatus.Text = "Downloading portable Python"; });
        string zip = Path.Combine(Path.GetTempPath(), "toolkit-python.zip");
        try
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;   // TLS 1.2
            using (WebClient wc = new WebClient())
            {
                ManualResetEvent done = new ManualResetEvent(false);
                Exception err = null;
                wc.DownloadProgressChanged += delegate(object s, DownloadProgressChangedEventArgs e)
                {
                    long r = e.BytesReceived, t = e.TotalBytesToReceive;
                    Ui(delegate
                    {
                        if (t > 0) { ItemMarquee(false); pbItem.Value = (int)(1000.0 * r / t); lblItem.Text = "Python: " + Human(r) + " / " + Human(t); }
                    });
                };
                wc.DownloadFileCompleted += delegate(object s, System.ComponentModel.AsyncCompletedEventArgs e) { err = e.Error; done.Set(); };
                wc.DownloadFileAsync(new Uri(PyUrl + arch + ".zip"), zip);
                while (!done.WaitOne(200)) { if (cancel) { wc.CancelAsync(); throw new Exception("Cancelled"); } }
                if (err != null) throw err;
            }
            if (Directory.Exists(Path.Combine(updDir, "python"))) Directory.Delete(Path.Combine(updDir, "python"), true);
            ZipFile.ExtractToDirectory(zip, Path.Combine(updDir, "python"));
        }
        finally { try { File.Delete(zip); } catch (Exception) { } }

        if (!File.Exists(portable)) throw new Exception("Portable Python did not unpack correctly.");
        return portable;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (worker != null && worker.IsAlive)
        {
            if (MessageBox.Show(this, "An update is running. Cancel it and close?", Text, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            { e.Cancel = true; return; }
            DoCancel();
        }
        base.OnFormClosing(e);
    }
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(args));
    }
}
