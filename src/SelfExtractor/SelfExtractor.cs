// DC Tool Kit self-extractor (Windows). build.sh / build.ps1 append the toolkit zip to this exe:
//
//     [SelfExtractor.exe][payload.zip][int64 payload offset, little endian]["DCTKSFX1"]
//
// It asks for the target (default: first USB drive), extracts everything, then marks programs\, system\,
// ventoy\ and the drive icon files as hidden. An older toolkit on the target is removed first (only ISO\,
// programs\ and system\Update\ver.log are kept), so the updater remembers what it installed. When the target is a drive root, the drive is renamed to the label= in the
// packaged autorun.inf (which also gives the drive its icon, DCToolKit.ico).
//
// Command line:  DC-Tool-Kit-windows.exe [/target:E:\] [/silent] [/nolabel]
//   /target   extract here instead of asking     /silent  no window (needs /target); exit code 0 = ok
//   /nolabel  do not rename the drive
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
    public static readonly string[] HiddenDirs = { "programs", "system", "ventoy" };
    // Drive-root files hidden after extraction (drive icon for Windows / macOS).
    static readonly string[] HiddenFiles = { "autorun.inf", "DCToolKit.ico", ".VolumeIcon.icns" };
    // Files kept when they already exist on the target.
    static readonly string[] KeepIfExists = { "system/Update/ver.log" };

    // Old-version cleanup. Everything the toolkit has ever put at the drive root, in any layout. Deleted
    // before extracting so nothing stale is left behind; ISO\ and programs\ are never touched. Unknown
    // files are never deleted.
    static readonly string[] OldItems = {
        "Tools", "system", "ventoy", "src", "UpdateToolkit.exe", "UpdateToolkit.app", "UpdateToolkit.command",
        "ITToolkit.exe", "BitLocker Key Entry.app", "README.md", "autorun.inf", "DCToolKit.ico", ".VolumeIcon.icns",
        "build.sh", "build.ps1", "build.bat", ".gitignore", ".gitattributes" };
    // Where older layouts kept the updater's version log (newest first). It records which ISO / program
    // versions are installed, so it is carried over; without it everything would be downloaded again.
    static readonly string[] VerLogs = { "system/Update/ver.log", "src/UpdateToolkit/Update/ver.log", "src/Scripts/Update/ver.log" };
    // Markers of an existing toolkit; cleanup only runs when one is present.
    static readonly string[] ToolkitMarkers = {
        "system/Update/update_isos.py", "src/UpdateToolkit/Update/update_isos.py", "src/Scripts/Update/update_isos.py",
        "UpdateToolkit.exe", "ITToolkit.exe" };

    static string P(string root, string rel) { return Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)); }

    // Removes the files of an older toolkit version from root. Returns a line for the log, or null.
    public static string CleanOldVersion(string root)
    {
        bool isToolkit = false;
        foreach (string m in ToolkitMarkers) if (File.Exists(P(root, m))) { isToolkit = true; break; }
        if (!isToolkit) return null;
        if (Directory.Exists(P(root, ".git"))) return "Skipped cleanup: target is a git checkout.";

        byte[] verLog = null;
        foreach (string v in VerLogs) if (File.Exists(P(root, v))) { verLog = File.ReadAllBytes(P(root, v)); break; }

        // Very old layout: programs lived in src\Programs. Move them up instead of deleting them.
        string oldPrograms = P(root, "src/Programs"), programs = P(root, "programs");
        if (Directory.Exists(oldPrograms) && !Directory.Exists(programs)) Directory.Move(oldPrograms, programs);

        int removed = 0;
        foreach (string item in OldItems)
        {
            string p = P(root, item);
            if (Directory.Exists(p)) { ClearAttributes(new DirectoryInfo(p)); Directory.Delete(p, true); removed++; }
            else if (File.Exists(p)) { File.SetAttributes(p, FileAttributes.Normal); File.Delete(p); removed++; }
        }
        if (verLog != null)
        {
            Directory.CreateDirectory(P(root, "system/Update"));
            File.WriteAllBytes(P(root, "system/Update/ver.log"), verLog);
        }
        return removed == 0 ? null : "Removed the old toolkit version (" + removed + " items); kept ISO, programs and ver.log.";
    }

    // Read-only / hidden / system attributes make Directory.Delete fail.
    static void ClearAttributes(DirectoryInfo d)
    {
        foreach (FileSystemInfo i in d.GetFileSystemInfos("*", SearchOption.AllDirectories)) i.Attributes = FileAttributes.Normal;
        d.Attributes = FileAttributes.Directory;
    }

    [STAThread]
    static int Main(string[] args)
    {
        string target = null; bool silent = false; bool label = true;
        foreach (string a in args)
        {
            string l = a.ToLowerInvariant();
            if (l.StartsWith("/target:")) target = a.Substring(8).Trim(new char[] { '"' });
            else if (l == "/silent") silent = true;
            else if (l == "/nolabel") label = false;
        }
        if (silent)
        {
            if (target == null) { Console.Error.WriteLine("/silent needs /target:<folder>"); return 2; }
            try { Extract(target, null); if (label) RenameDrive(target, false); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
        }
        Application.EnableVisualStyles();
        Application.Run(new MainForm(target ?? DefaultTarget(), label));
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
        if (progress != null) progress(0, 1, "Removing the old toolkit version...");
        string cleaned = CleanOldVersion(root);
        if (cleaned != null && progress == null) Console.WriteLine(cleaned);
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
        foreach (string f in HiddenFiles)
        {
            string p = Path.Combine(root, f);
            if (File.Exists(p)) File.SetAttributes(p, File.GetAttributes(p) | FileAttributes.Hidden);
        }
    }

    // The drive label comes from label= in the packaged autorun.inf (build.sh --label / build.ps1 -Label).
    static string ReadLabel(string root)
    {
        string inf = Path.Combine(root, "autorun.inf");
        if (!File.Exists(inf)) return null;
        foreach (string line in File.ReadAllLines(inf))
        {
            string t = line.Trim();
            if (t.StartsWith("label=", StringComparison.OrdinalIgnoreCase)) return t.Substring(6).Trim();
        }
        return null;
    }

    // Renames the drive when target is a drive root. Returns a line for the user, or null if not a drive root.
    public static string RenameDrive(string target, bool allowElevate)
    {
        string full = Path.GetFullPath(target);
        string driveRoot = Path.GetPathRoot(full);
        if (!string.Equals(full.TrimEnd('\\', '/'), driveRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            return null;                                   // extracted into a folder, not a drive
        string label = ReadLabel(driveRoot);
        if (string.IsNullOrEmpty(label)) return null;
        DriveInfo drive = new DriveInfo(driveRoot);
        // FAT32 labels are at most 11 characters, exFAT 15, NTFS 32.
        string fs = drive.DriveFormat.ToUpperInvariant();
        int max = fs == "FAT32" || fs == "FAT" ? 11 : fs == "EXFAT" ? 15 : 32;
        if (label.Length > max) label = label.Substring(0, max);
        if (drive.VolumeLabel == label) return "Drive name: " + label;
        try
        {
            drive.VolumeLabel = label;
            return "Drive renamed to " + label + ".";
        }
        catch (Exception ex)
        {
            if (!allowElevate) return "Could not rename the drive: " + ex.Message;
        }
        // Renaming a drive can need admin rights: ask once through UAC (label.exe ships with Windows).
        try
        {
            System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo(
                Path.Combine(Environment.SystemDirectory, "label.exe"), driveRoot.Substring(0, 2) + " " + label);
            psi.Verb = "runas";
            psi.UseShellExecute = true;
            psi.WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden;
            using (System.Diagnostics.Process p = System.Diagnostics.Process.Start(psi))
            {
                p.WaitForExit();
                if (p.ExitCode == 0) return "Drive renamed to " + label + ".";
                return "Could not rename the drive (label.exe exit code " + p.ExitCode + ").";
            }
        }
        catch (Exception ex)
        {
            return "Drive not renamed: " + ex.Message;
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

    readonly bool renameDrive;

    public MainForm(string target, bool renameDrive)
    {
        this.renameDrive = renameDrive;
        try { Icon = Icon.ExtractAssociatedIcon(Assembly.GetExecutingAssembly().Location); } catch { }
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
        lblStatus.Text = "programs, system and ventoy are hidden after extraction; the drive is renamed.";
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
        string target = txtTarget.Text.Trim().Trim(new char[] { '"' });
        if (target.Length == 0) { Browse(); return; }
        if (target.Length == 2 && target[1] == ':') target += "\\";
        if (MessageBox.Show(this, "Extract the toolkit to " + target + " ?\n\nAn older toolkit there is removed first. Only the ISO and programs folders (and the update log) are kept.",
                Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

        btnExtract.Enabled = btnBrowse.Enabled = btnClose.Enabled = txtTarget.Enabled = false;
        Thread t = new Thread(delegate ()
        {
            string error = null, renamed = null;
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
            if (error == null && renameDrive)
            {
                BeginInvoke((MethodInvoker)delegate { lblStatus.Text = "Renaming the drive..."; });
                renamed = SelfExtractor.RenameDrive(target, true);
            }
            BeginInvoke((MethodInvoker)delegate { Finished(target, error, renamed); });
        });
        t.IsBackground = true;
        t.Start();
    }

    void Finished(string target, string error, string renamed)
    {
        btnExtract.Enabled = btnBrowse.Enabled = btnClose.Enabled = txtTarget.Enabled = true;
        if (error != null)
        {
            lblStatus.Text = "Failed: " + error;
            MessageBox.Show(this, "Extraction failed:\n\n" + error, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        lblStatus.Text = "Done.";
        if (MessageBox.Show(this, "The toolkit was extracted to " + target + "." +
                (renamed != null ? "\n" + renamed : "") + "\n\nRun UpdateToolkit.exe there to download the ISOs and programs.\n\nOpen the folder now?",
                Text, MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
        {
            try { System.Diagnostics.Process.Start("explorer.exe", "\"" + target + "\""); } catch { }
        }
        Close();
    }
}
