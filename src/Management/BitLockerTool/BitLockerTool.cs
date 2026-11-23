// BitLocker Unlock & Decrypt - GUI version of src\scripts\BitLocker\Unlock-And-Disable.ps1.
// Unlocks a BitLocker volume with its 48-digit recovery password, turns BitLocker off and shows
// decryption progress.
//
// Workflow: on another machine, paste the recovery password into key.txt at the ROOT of the toolkit
// stick (e.g. E:\key.txt). Plug the stick into the locked machine and run this. key.txt is wiped
// (overwritten, then emptied) as soon as the unlock step has run, pass or fail, and again on exit.
//
// Targets .NET Framework 4.x / C# 5 so it builds with the csc.exe that ships with Windows (build.bat).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main()
    {
        if (!IsAdmin())
        {
            // The release build has a requireAdministrator manifest; this covers builds without one.
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
            }
            catch (Exception)
            {
                MessageBox.Show("This tool needs administrator rights to unlock and decrypt drives.",
                    "BitLocker Unlock", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return;
        }
        try { SetProcessDPIAware(); } catch (Exception) { }
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm());
    }

    static bool IsAdmin()
    {
        try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
        catch (Exception) { return false; }
    }
}

static class KeyFile
{
    // key.txt lives at the root of the drive this program runs from (the toolkit stick).
    public static string PathOnStick()
    {
        string root = Path.GetPathRoot(AppDomain.CurrentDomain.BaseDirectory);
        return Path.Combine(root, "key.txt");
    }

    // A recovery password is 8 groups of 6 digits. Each group is divisible by 11 and below 720896.
    public static string Validate(string text, out string key)
    {
        key = null;
        if (text == null) return "key.txt not found at the root of the stick.";
        string t = Regex.Replace(text, @"\s+", "");
        if (t.Length == 0) return "key.txt is empty. Paste the 48-digit recovery password into it.";
        if (!Regex.IsMatch(t, @"^(\d{6}-){7}\d{6}$"))
            return "key.txt is not a recovery password (8 groups of 6 digits, separated by dashes).";
        string[] groups = t.Split('-');
        for (int i = 0; i < groups.Length; i++)
        {
            int n = int.Parse(groups[i], CultureInfo.InvariantCulture);
            if (n % 11 != 0 || n >= 720896)
                return "Group " + (i + 1) + " of the recovery password (" + groups[i] + ") is not valid. Check for a typo.";
        }
        key = t;
        return null;
    }

    public static string Read()
    {
        string p = PathOnStick();
        if (!File.Exists(p)) return null;
        try { return File.ReadAllText(p); } catch (Exception) { return null; }
    }

    // Overwrite the contents with zeros, then truncate to 0 bytes. The empty file is kept so the
    // next tech knows where to paste. Flash storage can't guarantee the old blocks are gone, so the
    // stick still has to stay physically controlled.
    public static bool Wipe()
    {
        string p = PathOnStick();
        if (!File.Exists(p)) return true;
        try
        {
            File.SetAttributes(p, FileAttributes.Normal);
            using (FileStream fs = new FileStream(p, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                long len = fs.Length;
                byte[] zeros = new byte[4096];
                for (long done = 0; done < len; done += zeros.Length)
                    fs.Write(zeros, 0, (int)Math.Min(zeros.Length, len - done));
                fs.Flush(true);
                fs.SetLength(0);
                fs.Flush(true);
            }
            return true;
        }
        catch (Exception) { return false; }
    }
}

class MainForm : Form
{
    ComboBox cboDrive;
    Button btnRefresh, btnBegin, btnClose;
    Label lblKey, lblStep;
    ProgressBar pb;
    TextBox log;
    string key;
    volatile bool running, keyUsed;

    public MainForm()
    {
        Text = "BitLocker Unlock & Decrypt";
        ClientSize = new Size(560, 400);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        Font = new Font("Segoe UI", 9f);

        Label lblDrive = new Label();
        lblDrive.Text = "Drive to unlock:";
        lblDrive.AutoSize = true;
        lblDrive.Location = new Point(12, 16);
        Controls.Add(lblDrive);

        cboDrive = new ComboBox();
        cboDrive.DropDownStyle = ComboBoxStyle.DropDownList;
        cboDrive.Location = new Point(120, 12);
        cboDrive.Width = 120;
        Controls.Add(cboDrive);

        btnRefresh = new Button();
        btnRefresh.Text = "Refresh";
        btnRefresh.Location = new Point(250, 11);
        btnRefresh.Width = 80;
        btnRefresh.Click += delegate { LoadDrives(); CheckKey(); };
        Controls.Add(btnRefresh);

        lblKey = new Label();
        lblKey.Location = new Point(12, 48);
        lblKey.Size = new Size(536, 36);
        Controls.Add(lblKey);

        btnBegin = new Button();
        btnBegin.Text = "Decrypt!";
        btnBegin.Location = new Point(12, 90);
        btnBegin.Size = new Size(120, 32);
        btnBegin.Click += delegate { Begin(); };
        Controls.Add(btnBegin);

        lblStep = new Label();
        lblStep.Text = "Ready.";
        lblStep.Location = new Point(144, 98);
        lblStep.Size = new Size(404, 20);
        Controls.Add(lblStep);

        pb = new ProgressBar();
        pb.Location = new Point(12, 132);
        pb.Size = new Size(536, 22);
        pb.Minimum = 0;
        pb.Maximum = 1000;
        Controls.Add(pb);

        log = new TextBox();
        log.Multiline = true;
        log.ReadOnly = true;
        log.ScrollBars = ScrollBars.Vertical;
        log.Font = new Font("Consolas", 9f);
        log.Location = new Point(12, 164);
        log.Size = new Size(536, 190);
        Controls.Add(log);

        btnClose = new Button();
        btnClose.Text = "Close";
        btnClose.Location = new Point(468, 362);
        btnClose.Size = new Size(80, 28);
        btnClose.Click += delegate { Close(); };
        Controls.Add(btnClose);

        FormClosing += OnClosing;
        LoadDrives();
        CheckKey();
        Log("key.txt location: " + KeyFile.PathOnStick());
    }

    void LoadDrives()
    {
        string stick = Path.GetPathRoot(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\').ToUpperInvariant();
        string prev = cboDrive.SelectedItem as string;
        cboDrive.Items.Clear();
        foreach (DriveInfo d in DriveInfo.GetDrives())
        {
            string letter = d.Name.TrimEnd('\\').ToUpperInvariant();
            if (letter == stick) continue;
            DriveType t;
            try { t = d.DriveType; } catch (Exception) { continue; }
            // Locked BitLocker volumes report as Fixed (or sometimes Unknown) and not ready.
            if (t == DriveType.Fixed || t == DriveType.Removable || t == DriveType.Unknown) cboDrive.Items.Add(letter);
        }
        if (prev != null && cboDrive.Items.Contains(prev)) cboDrive.SelectedItem = prev;
        else if (cboDrive.Items.Contains("C:")) cboDrive.SelectedItem = "C:";
        else if (cboDrive.Items.Count > 0) cboDrive.SelectedIndex = 0;
    }

    void CheckKey()
    {
        string err = KeyFile.Validate(KeyFile.Read(), out key);
        if (err == null)
        {
            lblKey.ForeColor = Color.DarkGreen;
            lblKey.Text = "Recovery password found in key.txt (ends in " + key.Substring(key.Length - 6) + ").";
        }
        else
        {
            lblKey.ForeColor = Color.DarkRed;
            lblKey.Text = err;
        }
        btnBegin.Enabled = err == null && !running && cboDrive.Items.Count > 0;
    }

    void Begin()
    {
        CheckKey();
        if (key == null) return;
        string drive = cboDrive.SelectedItem as string;
        if (drive == null) return;
        if (MessageBox.Show(this, "Unlock " + drive + " and turn BitLocker OFF (fully decrypt it)?\n\n" +
                "Decryption keeps running in the background if you close this window.\nkey.txt will be wiped after the unlock step.",
                Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;

        running = true;
        btnBegin.Enabled = btnRefresh.Enabled = cboDrive.Enabled = false;
        string k = key;
        key = null;
        Thread t = new Thread(delegate () { Run(drive, k); });
        t.IsBackground = true;
        t.Start();
    }

    void Run(string drive, string k)
    {
        try
        {
            Step(10, "Unlocking " + drive + "...");
            int rc;
            try { rc = Bde("-unlock " + drive + " -RecoveryPassword " + k, true); }
            finally
            {
                k = null;
                keyUsed = true;
                WipeKey();
            }
            if (rc != 0) Log("Unlock returned " + rc + ". The key may be wrong, or the drive was already unlocked. Continuing.");

            Step(30, "Turning BitLocker off on " + drive + "...");
            rc = Bde("-off " + drive, false);
            if (rc != 0)
            {
                Step(-1, "Could not start decryption (manage-bde exit code " + rc + "). See the log.");
                return;
            }

            Step(50, "Decrypting " + drive + "...");
            double last = -1;
            int unknown = 0;
            while (true)
            {
                string status;
                double enc = PercentEncrypted(drive, out status);
                if (enc < 0)
                {
                    if (++unknown >= 5) { Step(-1, "Could not read decryption progress. Check with: manage-bde -status " + drive); return; }
                }
                else
                {
                    unknown = 0;
                    int val = 50 + (int)((100.0 - enc) * 9.5);  // 5% .. 100% of the bar
                    if (enc != last) { Log("Encrypted: " + enc.ToString("0.0", CultureInfo.InvariantCulture) + "%   " + status); last = enc; }
                    SetBar(val, "Decrypting " + drive + ": " + (100.0 - enc).ToString("0.0", CultureInfo.InvariantCulture) + "% done");
                    if (enc <= 0.0 && status.IndexOf("Fully Decrypted", StringComparison.OrdinalIgnoreCase) >= 0) break;
                    if (enc <= 0.0 && status.Length == 0) break;
                }
                Thread.Sleep(3000);
            }
            Step(1000, drive + " is fully decrypted. BitLocker is off.");
        }
        catch (Exception e)
        {
            Step(-1, "Error: " + e.Message);
        }
        finally
        {
            if (!keyUsed) { keyUsed = true; WipeKey(); }
            running = false;
            Ui(delegate { btnRefresh.Enabled = cboDrive.Enabled = true; CheckKey(); });
        }
    }

    // Reads "Percentage Encrypted" and "Conversion Status" from manage-bde -status.
    // Returns -1 if it can't be read. Labels are English; other languages fall back to the first % value.
    double PercentEncrypted(string drive, out string status)
    {
        status = "";
        string outp = RunCapture("-status " + drive);
        if (outp == null) return -1;
        Match s = Regex.Match(outp, @"Conversion Status:\s*(.+)");
        if (s.Success) status = s.Groups[1].Value.Trim();
        Match m = Regex.Match(outp, @"Percentage Encrypted:\s*([\d.,]+)\s*%");
        if (!m.Success) m = Regex.Match(outp, @":\s*([\d.,]+)\s*%");
        if (!m.Success) return -1;
        double v;
        if (!double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return -1;
        return v;
    }

    static string BdePath()
    {
        string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
        // A 32-bit process on 64-bit Windows must use Sysnative to reach the real System32.
        string native = Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative"), "manage-bde.exe");
        if (!Environment.Is64BitProcess && File.Exists(native)) return native;
        return Path.Combine(sys, "manage-bde.exe");
    }

    // Runs manage-bde and logs its output. hideArgs keeps the recovery password out of the log.
    int Bde(string args, bool hideArgs)
    {
        Log("> manage-bde " + (hideArgs ? Regex.Replace(args, @"\d{6}(-\d{6}){7}", "******-...-******") : args));
        ProcessStartInfo psi = new ProcessStartInfo(BdePath(), args);
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        using (Process p = Process.Start(psi))
        {
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit();
            foreach (string line in o.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("BitLocker Drive Encryption:") || line.StartsWith("Copyright")) continue;
                Log("  " + line.Trim());
            }
            return p.ExitCode;
        }
    }

    string RunCapture(string args)
    {
        try
        {
            ProcessStartInfo psi = new ProcessStartInfo(BdePath(), args);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            using (Process p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                return o;
            }
        }
        catch (Exception) { return null; }
    }

    void WipeKey()
    {
        if (KeyFile.Wipe()) Log("key.txt wiped.");
        else Log("WARNING: could not wipe " + KeyFile.PathOnStick() + ". Delete its contents by hand.");
    }

    void OnClosing(object sender, FormClosingEventArgs e)
    {
        if (running && !keyUsed)
        {
            MessageBox.Show(this, "Wait for the unlock step to finish.", Text);
            e.Cancel = true;
            return;
        }
        if (running)
            MessageBox.Show(this, "Decryption will keep running in the background.\nCheck it with: manage-bde -status", Text);
    }

    // ---- UI helpers (safe from the worker thread) ----

    void Step(int bar, string text)
    {
        Log(text);
        Ui(delegate
        {
            lblStep.Text = text;
            lblStep.ForeColor = bar < 0 ? Color.DarkRed : (bar >= 1000 ? Color.DarkGreen : SystemColors.ControlText);
            if (bar >= 0) pb.Value = Math.Min(bar, pb.Maximum);
        });
    }

    void SetBar(int bar, string text)
    {
        Ui(delegate { pb.Value = Math.Max(0, Math.Min(bar, pb.Maximum)); lblStep.Text = text; });
    }

    void Log(string s)
    {
        Ui(delegate { log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + s + Environment.NewLine); });
    }

    void Ui(MethodInvoker a)
    {
        if (IsDisposed) return;
        try
        {
            if (InvokeRequired) BeginInvoke(a);
            else a();
        }
        catch (Exception) { }
    }
}
