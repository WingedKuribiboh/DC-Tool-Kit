// DC Tool Kit self-extractor (Windows). build.sh / build.ps1 append the toolkit zip to this exe:
//
//     [SelfExtractor.exe][payload.zip][int64 payload offset, little endian]["DCTKSFX1"]
//
// It asks for the target (default: first USB drive), extracts everything, then marks programs\ and
// system\ as hidden. An existing system\Update\ver.log is kept so the updater remembers what it installed.
//
// Command line:  DC-Tool-Kit-windows.exe [/target:E:\] [/silent]
//   /target  extract here instead of asking     /silent  no window (needs /target); exit code 0 = ok
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class SelfExtractor
{
    const string Magic = "DCTKSFX1";
    public const string Title = "DC Tool Kit Installer";
    // Folders hidden after extraction (relative to the target root).
    public static readonly string[] HiddenDirs = { "programs", "system" };
    // Files kept when they already exist on the target.
    static readonly string[] KeepIfExists = { "system/Update/ver.log" };

    [STAThread]
    static int Main(string[] args)
    {
        string target = null; bool silent = false;
        foreach (string a in args)
        {
            string l = a.ToLowerInvariant();
            if (l.StartsWith("/target:")) target = a.Substring(8).Trim('"');
            else if (l == "/silent") silent = true;
        }
        if (silent)
        {
            if (target == null) { Console.Error.WriteLine("/silent needs /target:<folder>"); return 2; }
            try { Extract(target, null); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
        Application.EnableVisualStyles();
        Application.Run(new MainForm(target ?? DefaultTarget()));
        return 0;
    }

    static string DefaultTarget()
    {
        foreach (DriveInfo d in DriveInfo.GetDrives())
        {
            try { if (d.DriveType == DriveType.Removable && d.IsReady) return d.RootDirectory.FullName; }
            catch { }
        }
        return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
    }

    // Opens the zip appended to this exe.
    static ZipArchive OpenPayload(out Stream file)
    {
        string self = Assembly.GetExecutingAssembly().Location;
        file = new FileStream(self, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] tail = new byte[16];
        file.Seek(-16, SeekOrigin.End);
        ReadExactly(file, tail);
        if (Encoding.ASCII.GetString(tail, 8, 8) != Magic)
            throw new InvalidDataException("No toolkit payload found in this exe. Build it with build.sh or build.ps1.");
        long offset = BitConverter.ToInt64(tail, 0);
        Stream payload = new SubStream(file, offset, file.Length - 16 - offset);
        return new ZipArchive(payload, ZipArchiveMode.Read);
    }

    static void ReadExactly(Stream s, byte[] buf)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = s.Read(buf, got, buf.Length - got);
            if (n <= 0) throw new EndOfStreamException();
            got += n;
        }
    }

    // progress(done, total, currentName); progress may be null.
    public static void Extract(string target, Action<int, int, string> progress)
    {
        string root = Path.GetFullPath(target);
        if (!root.EndsWith(Path.DirectorySeparatorChar.ToString())) root += Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        Stream file;
        using (ZipArchive zip = OpenPayload(out file))
        using (file)
        {
            int total = zip.Entries.Count, done = 0;
            byte[] buf = new byte[1 << 20];
            foreach (ZipArchiveEntry e in zip.Entries)
            {
                string name = e.FullName.Replace('\\', '/');
                if (progress != null) progress(done, total, name);
                string dest = Path.GetFullPath(Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar)));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Unsafe path in package: " + name);
                if (name.EndsWith("/"))
                {
                    Directory.CreateDirectory(dest);
                }
                else if (!(File.Exists(dest) && Array.IndexOf(KeepIfExists, name) >= 0))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    // A hidden or read-only file cannot be overwritten with FileMode.Create.
                    if (File.Exists(dest)) File.SetAttributes(dest, FileAttributes.Normal);
                    using (Stream src = e.Open())
                    using (FileStream dst = new FileStream(dest, FileMode.Create, FileAccess.Write))
                    {
                        int n;
                        while ((n = src.Read(buf, 0, buf.Length)) > 0) dst.Write(buf, 0, n);
                    }
                    try { File.SetLastWriteTime(dest, e.LastWriteTime.DateTime); } catch { }
                }
                done++;
            }
            if (progress != null) progress(total, total, "");
        }
        foreach (string d in HiddenDirs)
        {
            string p = Path.Combine(root, d);
            if (Directory.Exists(p))
            {
                DirectoryInfo di = new DirectoryInfo(p);
                di.Attributes |= FileAttributes.Hidden;
            }
        }
    }
}

// Read-only window [offset, offset+length) of another stream.
class SubStream : Stream
{
    readonly Stream inner; readonly long offset, length; long pos;
    public SubStream(Stream inner, long offset, long length) { this.inner = inner; this.offset = offset; this.length = length; }
    public override bool CanRead { get { return true; } }
    public override bool CanSeek { get { return true; } }
    public override bool CanWrite { get { return false; } }
    public override long Length { get { return length; } }
    public override long Position { get { return pos; } set { pos = value; } }
    public override void Flush() { }
    public override int Read(byte[] buffer, int off, int count)
    {
        if (pos >= length) return 0;
        if (count > length - pos) count = (int)(length - pos);
        inner.Seek(offset + pos, SeekOrigin.Begin);
        int n = inner.Read(buffer, off, count);
        pos += n;
        return n;
    }
    public override long Seek(long o, SeekOrigin origin)
    {
        if (origin == SeekOrigin.Begin) pos = o;
        else if (origin == SeekOrigin.Current) pos += o;
        else pos = length + o;
        return pos;
    }
    public override void SetLength(long value) { throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int off, int count) { throw new NotSupportedException(); }
}

class MainForm : Form
{
    readonly TextBox txtTarget = new TextBox();
    readonly ProgressBar bar = new ProgressBar();
    readonly Label lblStatus = new Label();
    readonly Button btnBrowse = new Button(), btnExtract = new Button(), btnClose = new Button();

    public MainForm(string target)
    {
        Text = SelfExtractor.Title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 170);
        Font = new Font("Segoe UI", 9f);

        Label lbl = new Label();
        lbl.Text = "Extract the DC Tool Kit to (pick the root of the Ventoy USB stick):";
        lbl.Location = new Point(12, 12); lbl.AutoSize = true;
        Controls.Add(lbl);

        txtTarget.Text = target;
        txtTarget.Location = new Point(12, 36); txtTarget.Width = 400;
        Controls.Add(txtTarget);

        btnBrowse.Text = "Browse..."; btnBrowse.Location = new Point(420, 34); btnBrowse.Width = 88;
        btnBrowse.Click += delegate { Browse(); };
        Controls.Add(btnBrowse);

        bar.Location = new Point(12, 72); bar.Width = 496;
        Controls.Add(bar);

        lblStatus.Location = new Point(12, 100); lblStatus.Size = new Size(496, 20);
        lblStatus.AutoEllipsis = true;
        lblStatus.Text = "The programs and system folders are hidden after extraction.";
        Controls.Add(lblStatus);

        btnExtract.Text = "Extract"; btnExtract.Location = new Point(332, 130); btnExtract.Width = 88;
        btnExtract.Click += delegate { Run(); };
        Controls.Add(btnExtract);
        AcceptButton = btnExtract;

        btnClose.Text = "Close"; btnClose.Location = new Point(420, 130); btnClose.Width = 88;
        btnClose.Click += delegate { Close(); };
        Controls.Add(btnClose);
    }

    void Browse()
    {
        using (FolderBrowserDialog f = new FolderBrowserDialog())
        {
            f.Description = "Select the USB stick (or folder) to extract the toolkit to.";
            if (Directory.Exists(txtTarget.Text)) f.SelectedPath = txtTarget.Text;
            if (f.ShowDialog(this) == DialogResult.OK) txtTarget.Text = f.SelectedPath;
        }
    }

    void Run()
    {
        string target = txtTarget.Text.Trim().Trim('"');
        if (target.Length == 0) { Browse(); return; }
        if (target.Length == 2 && target[1] == ':') target += "\\";
        if (MessageBox.Show(this, "Extract the toolkit to " + target + " ?\n\nExisting toolkit files there are overwritten.",
                Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

        btnExtract.Enabled = btnBrowse.Enabled = btnClose.Enabled = txtTarget.Enabled = false;
        Thread t = new Thread(delegate ()
        {
            string error = null;
            try
            {
                SelfExtractor.Extract(target, delegate (int done, int total, string name)
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        bar.Maximum = Math.Max(total, 1); bar.Value = Math.Min(done, bar.Maximum);
                        lblStatus.Text = name.Length > 0 ? name : "Hiding programs and system...";
                    });
                });
            }
            catch (Exception ex) { error = ex.Message; }
            BeginInvoke((MethodInvoker)delegate { Finished(target, error); });
        });
        t.IsBackground = true;
        t.Start();
    }

    void Finished(string target, string error)
    {
        btnExtract.Enabled = btnBrowse.Enabled = btnClose.Enabled = txtTarget.Enabled = true;
        if (error != null)
        {
            lblStatus.Text = "Failed: " + error;
            MessageBox.Show(this, "Extraction failed:\n\n" + error, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        lblStatus.Text = "Done.";
        if (MessageBox.Show(this, "The toolkit was extracted to " + target + ".\n\nRun UpdateToolkit.exe there to download the ISOs and programs.\n\nOpen the folder now?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
        {
            try { System.Diagnostics.Process.Start("explorer.exe", "\"" + target + "\""); } catch { }
        }
        Close();
    }
}
