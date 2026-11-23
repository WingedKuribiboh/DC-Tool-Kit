// IT Toolkit - GUI front end for DISM, SFC, CHKDSK and common diagnostics.
// Targets .NET Framework 4.x / C# 5 so it builds with the csc.exe that ships with Windows.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

class Tool
{
    public string Id, Group, Name, Warn;
    public string[] Cmds;
    public bool OnlineOnly, Confirm;
}

class Options
{
    public string Target = "", Source = "", Scratch = "", WinDir = "", Log = "", Run = "";
    public string Disk = "", Part = "", Esp = "S", EspSize = "260";
    public bool Exit, Help, List;

    public static Options Parse(string[] args)
    {
        Options o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            bool prefixed = a.StartsWith("/") || a.StartsWith("-");
            if (!prefixed) { if (o.Target == "") o.Target = a; continue; }   // bare "D:" = target
            string body = a.TrimStart('/', '-');
            string key = body, val = null;
            int p = body.IndexOfAny(new char[] { ':', '=' });
            if (p >= 0) { key = body.Substring(0, p); val = body.Substring(p + 1); }
            key = key.ToLowerInvariant();
            bool needsVal = key == "target" || key == "t" || key == "source" || key == "s" || key == "scratch" ||
                            key == "windir" || key == "log" || key == "run" || key == "r" ||
                            key == "disk" || key == "part" || key == "esp" || key == "espsize";
            if (needsVal && val == null && i + 1 < args.Length) val = args[++i];
            if (val != null) val = val.Trim('"');
            switch (key)
            {
                case "target": case "t": o.Target = val ?? ""; break;
                case "source": case "s": o.Source = val ?? ""; break;
                case "scratch": o.Scratch = val ?? ""; break;
                case "windir": o.WinDir = val ?? ""; break;
                case "log": o.Log = val ?? ""; break;
                case "disk": o.Disk = val ?? ""; break;
                case "part": o.Part = val ?? ""; break;
                case "esp": o.Esp = val ?? "S"; break;
                case "espsize": o.EspSize = val ?? "260"; break;
                case "run": case "r": o.Run = val ?? ""; break;
                case "exit": case "x": o.Exit = true; break;
                case "list": o.List = true; break;
                case "help": case "h": case "?": o.Help = true; break;
            }
        }
        return o;
    }
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(Options.Parse(args)));
    }
}

class MainForm : Form
{
    const string Usage =
@"IT Toolkit  -  ITToolkit.exe [options]

  /target:<D:|D:\mount>   Repair an OFFLINE Windows image/drive instead of the running OS
  /source:<path>          DISM repair source, e.g. wim:E:\sources\install.wim:1  (adds /LimitAccess)
  /scratch:<dir>          DISM scratch directory (use a disk with free space)
  /windir:<dir>           Override Windows folder of the target (default <target>\Windows)
  /run:<id>[,<id>...]     Run tools automatically (no prompts); see /list
  /disk:<n>               Disk number for the UEFI/boot tools (see 'List disks')
  /part:<n>               Existing EFI System Partition number on that disk
  /esp:<letter>           Drive letter to assign/use for the ESP (default S)
  /espsize:<MB>           Size when creating a new ESP (default 260)
  /log:<file>             Save the output log to this file when finished
  /exit                   Close when /run finishes (exit code = last non-zero tool code)
  /list                   Show tool ids
  /help                   This text

Examples:
  ITToolkit.exe /target:D:
  ITToolkit.exe /target:D: /disk:0 /part:1 /esp:S /run:esp-reformat
  ITToolkit.exe /target:D: /source:wim:E:\sources\install.wim:1 /scratch:E:\tmp /run:full-repair /log:E:\repair.log /exit";

    readonly Options opt;
    readonly List<Tool> tools = new List<Tool>();
    readonly Dictionary<Button, Tool> btnMap = new Dictionary<Button, Tool>();

    ComboBox cboTarget;
    TextBox txtSource, txtScratch, txtOut, txtCmd, txtDisk, txtPart, txtEsp, txtEspSize;
    Label lblMode;
    Button btnCancel, btnRun;
    ToolStripStatusLabel lblStatus;
    ToolStripProgressBar progress;
    System.Windows.Forms.Timer timer;

    // runner state
    readonly Queue<string> queue = new Queue<string>();
    readonly ConcurrentQueue<string> chunks = new ConcurrentQueue<string>();
    readonly StringBuilder buf = new StringBuilder();
    int lineStart = 0; bool cr = false, dirty = false;
    Process proc; Thread pump; DateTime started; string current = "";
    bool canceled, busy, autoMode; int lastFail = 0;

    static bool IsAdmin()
    {
        return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    }

    // ------------------------------------------------------------ catalogue
    void Add(string id, string group, string name, bool onlineOnly, bool confirm, string warn, params string[] cmds)
    {
        Tool t = new Tool();
        t.Id = id; t.Group = group; t.Name = name; t.OnlineOnly = onlineOnly; t.Confirm = confirm; t.Warn = warn; t.Cmds = cmds;
        tools.Add(t);
    }

    // Builds a one-shot diskpart invocation from script lines.
    static string Dp(params string[] lines)
    {
        string f = "\"%TEMP%\\ittk_dp.txt\"";
        return "(echo " + string.Join("& echo ", lines) + ") > " + f + " & diskpart /s " + f;
    }

    void BuildCatalogue()
    {
        const string R = "Windows Repair (DISM / SFC)";
        Add("full-repair", R, "FULL REPAIR: Check > Scan > Restore > SFC", false, true, "This can take a long time.",
            "{DISM}/Cleanup-Image /CheckHealth", "{DISM}/Cleanup-Image /ScanHealth",
            "{DISM}/Cleanup-Image /RestoreHealth{SRC}", "sfc /scannow{SFCOFF}");
        Add("dism-check", R, "DISM CheckHealth (quick)", false, false, null, "{DISM}/Cleanup-Image /CheckHealth");
        Add("dism-scan", R, "DISM ScanHealth", false, false, null, "{DISM}/Cleanup-Image /ScanHealth");
        Add("dism-restore", R, "DISM RestoreHealth", false, true, null, "{DISM}/Cleanup-Image /RestoreHealth{SRC}");
        Add("dism-cleanup", R, "DISM Component Cleanup", false, true, null, "{DISM}/Cleanup-Image /StartComponentCleanup");
        Add("dism-resetbase", R, "DISM Cleanup + ResetBase", false, true,
            "ResetBase permanently removes the ability to uninstall existing Windows updates.",
            "{DISM}/Cleanup-Image /StartComponentCleanup /ResetBase");
        Add("dism-packages", R, "DISM List packages", false, false, null, "{DISM}/Get-Packages /Format:Table");
        Add("dism-drivers", R, "DISM List drivers", false, false, null, "{DISM}/Get-Drivers /Format:Table");
        Add("dism-edition", R, "DISM Current edition", false, false, null, "{DISM}/Get-CurrentEdition");
        Add("sfc-scan", R, "SFC Scan and repair", false, true, null, "sfc /scannow{SFCOFF}");
        Add("sfc-verify", R, "SFC Verify only", false, false, null, "sfc /verifyonly{SFCOFF}");
        Add("sfc-log", R, "Show SFC lines from CBS.log", false, false, null,
            "findstr /c:\"[SR]\" \"{WINDIR}\\Logs\\CBS\\CBS.log\"");

        const string D = "Disk";
        Add("chkdsk", D, "CHKDSK (read-only)", false, false, null, "chkdsk {DRIVE}");
        Add("chkdsk-fix", D, "CHKDSK /F /R (repair)", false, true,
            "Repairs and scans for bad sectors. Can take hours; the volume is dismounted or checked on next boot.",
            "echo Y| chkdsk {DRIVE} /F /R");
        Add("disk-health", D, "Physical disk health", true, false, null,
            "powershell -NoProfile -Command \"Get-PhysicalDisk | Format-Table FriendlyName,MediaType,HealthStatus,OperationalStatus,Size -AutoSize | Out-String -Width 200\"");
        Add("volumes", D, "Volumes / free space", true, false, null,
            "powershell -NoProfile -Command \"Get-Volume | Format-Table DriveLetter,FileSystemLabel,FileSystem,HealthStatus,Size,SizeRemaining -AutoSize | Out-String -Width 200\"");
        Add("clean-temp", D, "Clean temp files", true, true, null,
            "powershell -NoProfile -Command \"foreach($p in @($env:TEMP,(Join-Path $env:windir 'Temp'))){ Write-Host ('Cleaning '+$p); Get-ChildItem $p -Recurse -Force -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue }; Write-Host Done\"");

        const string N = "Network";
        Add("ip-all", N, "IP configuration (all)", true, false, null, "ipconfig /all");
        Add("flush-dns", N, "Flush DNS cache", true, false, null, "ipconfig /flushdns");
        Add("renew-ip", N, "Release / renew IP", true, true, null, "ipconfig /release", "ipconfig /renew");
        Add("winsock", N, "Reset Winsock", true, true, "A reboot is required afterwards.", "netsh winsock reset");
        Add("tcpip", N, "Reset TCP/IP stack", true, true, "A reboot is required afterwards.", "netsh int ip reset");
        Add("ping", N, "Ping 8.8.8.8 / google.com", true, false, null, "ping -n 4 8.8.8.8", "ping -n 4 google.com");
        Add("tracert", N, "Trace route to 8.8.8.8", true, false, null, "tracert -d -h 20 8.8.8.8");
        Add("netstat", N, "Active connections", true, false, null, "netstat -ano");
        Add("net-fix", N, "NETWORK FIX: DNS + renew + Winsock", true, true, "A reboot is required afterwards.",
            "ipconfig /flushdns", "ipconfig /release", "ipconfig /renew", "netsh winsock reset");

        const string B = "UEFI / Boot";
        Add("dp-list", B, "List disks / volumes", false, false, null,
            Dp("list disk", "list volume"));
        Add("dp-parts", B, "List partitions of Disk #", false, false, null,
            Dp("select disk {DISK}", "detail disk", "list partition"));
        Add("fw-mode", B, "Firmware mode (UEFI / BIOS)", true, false, null,
            "powershell -NoProfile -Command \"if($env:firmware_type){$env:firmware_type}else{'Unknown (try: bcdedit | findstr path)'}\"");
        Add("bcd-online", B, "BCD: show running OS entries", true, false, null, "bcdedit /enum all");
        Add("reagent", B, "Recovery environment status", true, false, null, "reagentc /info");
        Add("esp-mount", B, "Mount existing ESP (Disk #, Part #)", false, false, null,
            Dp("select disk {DISK}", "select partition {PART}", "assign letter={ESP}"));
        Add("boot-check", B, "CHECK boot files on ESP", false, false, null,
            "dir \"{ESP}:\\EFI\\Microsoft\\Boot\\bootmgfw.efi\"",
            "dir \"{ESP}:\\EFI\\Boot\\bootx64.efi\"",
            "dir \"{ESP}:\\EFI\\Microsoft\\Boot\\BCD\"",
            "dir \"{WINDIR}\\System32\\winload.efi\"",
            "bcdedit /store \"{ESP}:\\EFI\\Microsoft\\Boot\\BCD\" /enum all");
        Add("bcd-backup", B, "Backup BCD store on ESP", false, false, null,
            "copy /y \"{ESP}:\\EFI\\Microsoft\\Boot\\BCD\" \"{ESP}:\\EFI\\Microsoft\\Boot\\BCD.bak\"");
        Add("bcdboot", B, "Rebuild boot files (bcdboot)", false, true,
            "Copies fresh boot files from the target Windows folder to the ESP and merges the BCD.",
            "bcdboot \"{WINDIR}\" /s {ESP}: /f UEFI");
        Add("bcd-rebuild", B, "Rebuild BCD from scratch", false, true,
            "Renames the existing BCD to BCD.old on the ESP and writes a new one.",
            "attrib -s -h -r \"{ESP}:\\EFI\\Microsoft\\Boot\\BCD\"",
            "ren \"{ESP}:\\EFI\\Microsoft\\Boot\\BCD\" BCD.old",
            "bcdboot \"{WINDIR}\" /s {ESP}: /f UEFI");
        Add("esp-reformat", B, "RECREATE ESP: reformat existing + rebuild", false, true,
            "Formats the existing EFI System Partition (Disk #, Part #) as FAT32, ERASING everything on it, then rebuilds boot files. Verify the partition number with 'List partitions' first.",
            Dp("select disk {DISK}", "select partition {PART}", "format fs=fat32 quick label=System override", "assign letter={ESP}"),
            "bcdboot \"{WINDIR}\" /s {ESP}: /f UEFI");
        Add("esp-create", B, "RECREATE ESP: create NEW + rebuild", false, true,
            "Creates a new EFI partition of the given size from UNALLOCATED space at the end of the disk (disk must be GPT; shrink a volume first if needed), formats it FAT32 and rebuilds boot files. Verify the disk number first.",
            Dp("select disk {DISK}", "create partition efi size={ESPSIZE}", "format fs=fat32 quick label=System override", "assign letter={ESP}"),
            "bcdboot \"{WINDIR}\" /s {ESP}: /f UEFI");
        Add("bootrec-scan", B, "Bootrec /scanos", false, false, null, "bootrec /scanos");
        Add("bootrec-rebuild", B, "Bootrec /rebuildbcd", false, true, null, "bootrec /rebuildbcd");
        Add("bootrec-fixboot", B, "Bootrec /fixboot (writes boot sector)", false, true,
            "On UEFI systems this often fails with 'Access is denied'; use the bcdboot tools instead.", "bootrec /fixboot");

        const string S = "System";
        Add("sysinfo", S, "System information", true, false, null, "systeminfo");
        Add("hotfixes", S, "Installed updates (hotfixes)", true, false, null,
            "powershell -NoProfile -Command \"Get-HotFix | Sort-Object InstalledOn -Descending | Format-Table HotFixID,Description,InstalledOn,InstalledBy -AutoSize | Out-String -Width 200\"");
        Add("sys-errors", S, "Recent system errors (50)", true, false, null,
            "powershell -NoProfile -Command \"Get-WinEvent -FilterHashtable @{LogName='System';Level=1,2} -MaxEvents 50 -ErrorAction SilentlyContinue | Format-List TimeCreated,ProviderName,Id,Message | Out-String -Width 200\"");
        Add("battery", S, "Battery report (to Desktop)", true, false, null,
            "powercfg /batteryreport /output \"%USERPROFILE%\\Desktop\\battery-report.html\"");
        Add("startup", S, "Startup programs", true, false, null,
            "powershell -NoProfile -Command \"Get-CimInstance Win32_StartupCommand | Format-Table Name,Command,Location,User -AutoSize | Out-String -Width 200\"");
        Add("gpupdate", S, "Force Group Policy update", true, false, null, "gpupdate /force");
        Add("wu-reset", S, "Reset Windows Update services", true, true,
            "Stops update services and renames SoftwareDistribution and catroot2.",
            "net stop wuauserv", "net stop bits", "net stop cryptsvc",
            "ren %windir%\\SoftwareDistribution SoftwareDistribution.old",
            "ren %windir%\\System32\\catroot2 catroot2.old",
            "net start cryptsvc", "net start bits", "net start wuauserv");
        Add("memdiag", S, "Schedule Memory Diagnostic", true, true, null, "start mdsched.exe");
    }

    // ------------------------------------------------------------ target logic
    static string SysRoot()
    {
        return Path.GetPathRoot(Environment.SystemDirectory).TrimEnd('\\').ToUpperInvariant();
    }

    // Image path with trailing backslash, or "" for online
    string ImagePath()
    {
        string t = cboTarget.Text.Trim().Trim('"');
        if (t.Length == 0) return "";
        if (t.Length == 1 && char.IsLetter(t[0])) t = t + ":";
        t = t.TrimEnd('\\') + "\\";
        string root = Path.GetPathRoot(t).TrimEnd('\\').ToUpperInvariant();
        bool isRootOnly = t.Length <= 3;
        if (isRootOnly && root == SysRoot()) return "";   // same as running OS
        return t;
    }

    string EspLetter()
    {
        string e = txtEsp.Text.Trim().TrimEnd(':', '\\');
        return (e.Length == 1 && char.IsLetter(e[0])) ? e.ToUpperInvariant() : "";
    }

    static bool IsNum(string t) { int n; return int.TryParse(t.Trim(), out n) && n >= 0; }

    // Returns a description of any required field that is empty/invalid, else null.
    string Missing(Tool t)
    {
        string all = string.Join(" ", t.Cmds);
        if (all.Contains("{DISK}") && !IsNum(txtDisk.Text)) return "Disk # (a number - use 'List disks / volumes' to find it)";
        if (all.Contains("{PART}") && !IsNum(txtPart.Text)) return "Partition # (a number - use 'List partitions of Disk #')";
        if (all.Contains("{ESP}") && EspLetter().Length == 0) return "ESP letter (a single drive letter, e.g. S)";
        if (all.Contains("{ESPSIZE}") && !IsNum(txtEspSize.Text)) return "ESP size in MB (a number, e.g. 260)";
        return null;
    }

    bool Offline() { return ImagePath().Length > 0; }

    string Expand(string cmd)
    {
        string img = ImagePath();
        bool off = img.Length > 0;
        string scratch = txtScratch.Text.Trim().Trim('"');
        string dism = "DISM " + (off ? "/Image:" + img + " " : "/Online ") +
                      (scratch.Length > 0 ? "/ScratchDir:\"" + scratch + "\" " : "");
        string drive = off ? img.Substring(0, 2) : SysRoot();
        string windir = opt.WinDir.Length > 0 ? opt.WinDir : (off ? img + "Windows" : Environment.GetEnvironmentVariable("windir"));
        windir = windir.TrimEnd('\\');
        string sfcoff = off ? " /offbootdir=" + img + " /offwindir=" + windir : "";
        string src = txtSource.Text.Trim().Trim('"');
        src = src.Length > 0 ? " /Source:\"" + src + "\" /LimitAccess" : "";
        return cmd.Replace("{DISM}", dism).Replace("{SFCOFF}", sfcoff).Replace("{DRIVE}", drive)
                  .Replace("{WINDIR}", windir).Replace("{SRC}", src)
                  .Replace("{DISK}", txtDisk.Text.Trim()).Replace("{PART}", txtPart.Text.Trim())
                  .Replace("{ESP}", EspLetter()).Replace("{ESPSIZE}", txtEspSize.Text.Trim());
    }

    void RefreshMode()
    {
        string img = ImagePath();
        bool off = img.Length > 0;
        lblMode.Text = off ? "Mode: OFFLINE  ->  " + img : "Mode: ONLINE (running OS " + SysRoot() + ")";
        lblMode.ForeColor = off ? Color.DarkOrange : Color.ForestGreen;
        if (!busy) foreach (KeyValuePair<Button, Tool> kv in btnMap) kv.Key.Enabled = !(off && kv.Value.OnlineOnly);
    }

    // ------------------------------------------------------------ UI
    public MainForm(Options o)
    {
        opt = o;
        BuildCatalogue();

        Text = "IT Toolkit - DISM / SFC / Diagnostics";
        Size = new Size(1200, 800);
        MinimumSize = new Size(950, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9);

        // output (Fill must be added first so it docks last)
        txtOut = new TextBox();
        txtOut.Multiline = true; txtOut.ReadOnly = true; txtOut.ScrollBars = ScrollBars.Both; txtOut.WordWrap = false;
        txtOut.Dock = DockStyle.Fill; txtOut.BackColor = Color.FromArgb(12, 12, 12); txtOut.ForeColor = Color.Gainsboro;
        txtOut.Font = new Font("Consolas", 10);
        Controls.Add(txtOut);

        // left tool list
        Panel left = new Panel(); left.Dock = DockStyle.Left; left.Width = 275; left.AutoScroll = true;
        FlowLayoutPanel flow = new FlowLayoutPanel();
        flow.FlowDirection = FlowDirection.TopDown; flow.WrapContents = false; flow.AutoSize = true; flow.Dock = DockStyle.Top;
        flow.Padding = new Padding(4);
        left.Controls.Add(flow);
        ToolTip tip = new ToolTip();
        string lastGroup = null;
        foreach (Tool t in tools)
        {
            if (t.Group != lastGroup)
            {
                Label h = new Label(); h.Text = t.Group; h.AutoSize = false; h.Width = 240; h.Height = 24;
                h.TextAlign = ContentAlignment.BottomLeft; h.Font = new Font("Segoe UI", 9, FontStyle.Bold);
                h.ForeColor = Color.FromArgb(21, 101, 192); h.Margin = new Padding(0, 8, 0, 2);
                flow.Controls.Add(h); lastGroup = t.Group;
            }
            Button b = new Button(); b.Text = t.Name; b.Width = 240; b.Height = 28; b.TextAlign = ContentAlignment.MiddleLeft;
            if (t.Id == "full-repair" || t.Id == "net-fix") { b.BackColor = Color.FromArgb(227, 242, 253); b.FlatStyle = FlatStyle.Flat; b.Height = 32; }
            tip.SetToolTip(b, t.Id + "\n" + string.Join("\n", t.Cmds));
            b.Click += ToolClicked;
            btnMap[b] = t; flow.Controls.Add(b);
        }
        Controls.Add(left);

        // settings (target / source / scratch)
        Panel top = new Panel(); top.Dock = DockStyle.Top; top.Height = 134;
        top.Controls.Add(MkLabel("Target disk:", 8, 12));
        cboTarget = new ComboBox(); cboTarget.Location = new Point(85, 8); cboTarget.Width = 150; cboTarget.DropDownStyle = ComboBoxStyle.DropDown;
        cboTarget.TextChanged += delegate { RefreshMode(); };
        top.Controls.Add(cboTarget);
        Button btnDetect = MkBtn("Detect Windows installs", 245, 7, 160);
        btnDetect.Click += delegate { DetectInstalls(); };
        top.Controls.Add(btnDetect);
        lblMode = new Label(); lblMode.Location = new Point(415, 12); lblMode.AutoSize = true;
        lblMode.Font = new Font("Segoe UI", 9, FontStyle.Bold); top.Controls.Add(lblMode);

        top.Controls.Add(MkLabel("DISM source:", 8, 42));
        txtSource = new TextBox(); txtSource.Location = new Point(85, 39); txtSource.Width = 700; txtSource.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        top.Controls.Add(txtSource);
        Button bs = MkBtn("Browse...", 795, 38, 80); bs.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        bs.Click += delegate
        {
            OpenFileDialog d = new OpenFileDialog(); d.Filter = "Install image (*.wim;*.esd)|*.wim;*.esd|All files|*.*";
            if (d.ShowDialog() == DialogResult.OK)
            {
                string ext = Path.GetExtension(d.FileName).TrimStart('.').ToUpperInvariant();
                txtSource.Text = (ext == "WIM" || ext == "ESD") ? ext + ":" + d.FileName + ":1" : d.FileName;
            }
        };
        top.Controls.Add(bs);
        Label hint = MkLabel("e.g. wim:E:\\sources\\install.wim:1 (index editable) - used by RestoreHealth with /LimitAccess", 885, 42);
        hint.ForeColor = Color.Gray; hint.Anchor = AnchorStyles.Top | AnchorStyles.Right; top.Controls.Add(hint);

        top.Controls.Add(MkLabel("Scratch dir:", 8, 72));
        txtScratch = new TextBox(); txtScratch.Location = new Point(85, 69); txtScratch.Width = 700; txtScratch.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        top.Controls.Add(txtScratch);
        Button bsc = MkBtn("Browse...", 795, 68, 80); bsc.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        bsc.Click += delegate
        {
            FolderBrowserDialog d = new FolderBrowserDialog();
            if (d.ShowDialog() == DialogResult.OK) txtScratch.Text = d.SelectedPath;
        };
        top.Controls.Add(bsc);
        top.Controls.Add(MkLabel("Boot / ESP:", 8, 102));
        txtDisk = MkSmall(top, "Disk #", 85, 50);
        txtPart = MkSmall(top, "ESP part #", 205, 50);
        txtEsp = MkSmall(top, "ESP letter", 345, 40);
        txtEspSize = MkSmall(top, "New ESP MB", 485, 60);
        Controls.Add(top);

        // admin banner
        Label banner = new Label(); banner.Dock = DockStyle.Top; banner.Height = 24; banner.TextAlign = ContentAlignment.MiddleCenter;
        banner.Font = new Font("Segoe UI", 9, FontStyle.Bold); banner.ForeColor = Color.White;
        if (IsAdmin()) { banner.Text = "Running as Administrator"; banner.BackColor = Color.FromArgb(46, 125, 50); }
        else { banner.Text = "NOT running as Administrator - DISM, SFC and most repairs will fail"; banner.BackColor = Color.FromArgb(198, 40, 40); }
        Controls.Add(banner);

        // bottom bar
        Panel bottom = new Panel(); bottom.Dock = DockStyle.Bottom; bottom.Height = 44;
        btnCancel = MkBtn("Cancel running", 6, 8, 110); btnCancel.Enabled = false;
        btnCancel.Click += delegate { CancelRun(); };
        Button btnSave = MkBtn("Save log...", 122, 8, 90);
        btnSave.Click += delegate
        {
            SaveFileDialog d = new SaveFileDialog(); d.Filter = "Text log (*.txt)|*.txt";
            d.FileName = "ITToolkit-" + Environment.MachineName + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt";
            if (d.ShowDialog() == DialogResult.OK) File.WriteAllText(d.FileName, txtOut.Text);
        };
        Button btnCopy = MkBtn("Copy", 218, 8, 60);
        btnCopy.Click += delegate { if (txtOut.Text.Length > 0) Clipboard.SetText(txtOut.Text); };
        Button btnClear = MkBtn("Clear", 284, 8, 60);
        btnClear.Click += delegate { buf.Length = 0; lineStart = 0; cr = false; dirty = true; };
        bottom.Controls.Add(MkLabel("Custom cmd:", 365, 13));
        txtCmd = new TextBox(); txtCmd.Location = new Point(440, 10); txtCmd.Width = 500; txtCmd.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        txtCmd.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; RunCustom(); } };
        btnRun = MkBtn("Run", 950, 8, 70); btnRun.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        btnRun.Click += delegate { RunCustom(); };
        bottom.Controls.AddRange(new Control[] { btnCancel, btnSave, btnCopy, btnClear, txtCmd, btnRun });
        Controls.Add(bottom);

        StatusStrip ss = new StatusStrip();
        lblStatus = new ToolStripStatusLabel("Ready"); lblStatus.Spring = true; lblStatus.TextAlign = ContentAlignment.MiddleLeft;
        progress = new ToolStripProgressBar(); progress.Style = ProgressBarStyle.Marquee; progress.MarqueeAnimationSpeed = 0; progress.Width = 160;
        ss.Items.Add(lblStatus); ss.Items.Add(progress);
        Controls.Add(ss);

        txtOut.BringToFront();

        timer = new System.Windows.Forms.Timer(); timer.Interval = 150; timer.Tick += Tick; timer.Start();

        txtDisk.Text = opt.Disk; txtPart.Text = opt.Part; txtEsp.Text = opt.Esp; txtEspSize.Text = opt.EspSize;
        cboTarget.Text = opt.Target;
        txtSource.Text = opt.Source;
        txtScratch.Text = opt.Scratch;
        RefreshMode();
        AppendText("IT Toolkit - " + Environment.MachineName + " - " + DateTime.Now + "\r\nAdministrator: " + IsAdmin() + "\r\n");
        Shown += OnShown;
        FormClosing += OnClosing;
    }

    static TextBox MkSmall(Control parent, string caption, int x, int w)
    {
        // caption sits to the left of the box (x is the caption's left edge); box follows it
        Label l = MkLabel(caption, x, 102); parent.Controls.Add(l);
        TextBox t = new TextBox(); t.Width = w; t.Location = new Point(x + TextRenderer.MeasureText(caption, new Font("Segoe UI", 9)).Width + 6, 99);
        parent.Controls.Add(t); return t;
    }

    static Label MkLabel(string t, int x, int y) { Label l = new Label(); l.Text = t; l.Location = new Point(x, y); l.AutoSize = true; return l; }
    static Button MkBtn(string t, int x, int y, int w) { Button b = new Button(); b.Text = t; b.Location = new Point(x, y); b.Size = new Size(w, 28); return b; }

    void DetectInstalls()
    {
        cboTarget.Items.Clear();
        foreach (DriveInfo d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.IsReady && File.Exists(Path.Combine(d.RootDirectory.FullName, @"Windows\System32\config\SYSTEM")))
                    cboTarget.Items.Add(d.Name.Substring(0, 2));
            }
            catch { }
        }
        if (cboTarget.Items.Count == 0) { MessageBox.Show("No Windows installations found on any drive."); return; }
        cboTarget.DroppedDown = true;
    }

    // ------------------------------------------------------------ startup / args
    void OnShown(object sender, EventArgs e)
    {
        if (opt.Help) { MessageBox.Show(Usage, "IT Toolkit - help"); }
        if (opt.List)
        {
            StringBuilder sb = new StringBuilder();
            foreach (Tool t in tools) sb.AppendLine(t.Id.PadRight(16) + t.Name + (t.OnlineOnly ? "   [online only]" : ""));
            MessageBox.Show(sb.ToString(), "Tool ids for /run");
        }
        if (opt.Run.Length > 0)
        {
            List<string> cmds = new List<string>();
            foreach (string id in opt.Run.Split(new char[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                Tool t = tools.Find(delegate(Tool x) { return string.Equals(x.Id, id.Trim(), StringComparison.OrdinalIgnoreCase); });
                if (t == null) { AppendText("Unknown tool id: " + id + "\r\n"); lastFail = 87; continue; }
                string miss = Missing(t);
                if (miss != null) { AppendText("Skipped " + id + " - missing " + miss + "\r\n"); lastFail = 87; continue; }
                if (Offline() && t.OnlineOnly) { AppendText("Skipped (online only): " + id + "\r\n"); continue; }
                cmds.AddRange(t.Cmds);
            }
            autoMode = true;
            if (cmds.Count > 0) StartCommands(cmds); else Finish();
        }
    }

    // ------------------------------------------------------------ running
    void ToolClicked(object sender, EventArgs e)
    {
        Tool t = btnMap[(Button)sender];
        string miss = Missing(t);
        if (miss != null) { MessageBox.Show("This tool needs: " + miss, t.Name, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        if (t.Confirm)
        {
            string msg = "Run:\n\n" + string.Join("\n", Array.ConvertAll(t.Cmds, new Converter<string, string>(Expand)));
            if (t.Warn != null) msg += "\n\nWARNING: " + t.Warn;
            if (MessageBox.Show(msg, t.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        }
        StartCommands(new List<string>(t.Cmds));
    }

    void RunCustom()
    {
        string c = txtCmd.Text.Trim();
        if (c.Length > 0 && !busy) StartCommands(new List<string>(new string[] { c }));
    }

    void StartCommands(List<string> cmds)
    {
        if (busy) return;
        if (!autoMode && !IsAdmin() &&
            MessageBox.Show("Not running as Administrator. Most commands will fail. Continue anyway?", "Not elevated",
                            MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        canceled = false; lastFail = 0;
        foreach (string c in cmds) queue.Enqueue(Expand(c));
        SetBusy(true);
        StartNext();
    }

    void SetBusy(bool b)
    {
        busy = b;
        btnCancel.Enabled = b; btnRun.Enabled = !b; cboTarget.Enabled = !b;
        progress.MarqueeAnimationSpeed = b ? 30 : 0;
        if (b) foreach (Button x in btnMap.Keys) x.Enabled = false; else RefreshMode();
    }

    void StartNext()
    {
        if (queue.Count == 0) { Finish(); return; }
        current = queue.Dequeue();
        AppendText("\r\n=== [" + DateTime.Now.ToString("HH:mm:ss") + "] " + current + " ===\r\n");
        lblStatus.Text = "Running: " + current;
        started = DateTime.Now;
        ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c " + current + " 2>&1");
        psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardOutput = true;
        psi.StandardOutputEncoding = Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
        try { proc = Process.Start(psi); }
        catch (Exception ex) { AppendText("Failed to start: " + ex.Message + "\r\n"); lastFail = 1; proc = null; StartNext(); return; }
        StreamReader r = proc.StandardOutput;
        pump = new Thread(delegate()
        {
            char[] b = new char[1024]; int n;
            try { while ((n = r.Read(b, 0, b.Length)) > 0) chunks.Enqueue(new string(b, 0, n)); } catch { }
        });
        pump.IsBackground = true; pump.Start();
    }

    void Finish()
    {
        SetBusy(false);
        lblStatus.Text = (canceled ? "Canceled" : "Finished") + " at " + DateTime.Now.ToString("HH:mm:ss");
        if (opt.Log.Length > 0)
        {
            Flush();
            try { File.WriteAllText(opt.Log, buf.ToString()); } catch { }
        }
        if (autoMode && opt.Exit) { Environment.ExitCode = lastFail; Close(); }
    }

    void Tick(object sender, EventArgs e)
    {
        if (proc != null && !pump.IsAlive && proc.HasExited)
        {
            Drain();
            int code = proc.ExitCode;
            if (code != 0 && !canceled) lastFail = code;
            AppendText(string.Format("\r\n--- exit code {0}, {1:mm\\:ss} elapsed ---\r\n", code, DateTime.Now - started));
            proc.Dispose(); proc = null;
            if (canceled) queue.Clear();
            Flush();
            StartNext();
            return;
        }
        Drain(); Flush();
        if (proc != null) lblStatus.Text = string.Format("Running ({0:mm\\:ss}): {1}", DateTime.Now - started, current);
    }

    void Drain()
    {
        string s;
        while (chunks.TryDequeue(out s)) AppendText(s);
    }

    // Handles lone CR (DISM/SFC progress bars) by overwriting the current line; strips NULs (sfc is UTF-16).
    void AppendText(string s)
    {
        foreach (char c in s)
        {
            if (c == '\0') continue;
            if (c == '\r') { cr = true; continue; }
            if (c == '\n') { buf.Append("\r\n"); lineStart = buf.Length; cr = false; continue; }
            if (cr) { buf.Length = lineStart; cr = false; }
            buf.Append(c);
        }
        dirty = true;
    }

    void Flush()
    {
        if (!dirty) return;
        dirty = false;
        txtOut.Text = buf.ToString();
        txtOut.SelectionStart = txtOut.Text.Length;
        txtOut.ScrollToCaret();
    }

    void CancelRun()
    {
        canceled = true; queue.Clear();
        if (proc != null && !proc.HasExited)
        {
            try
            {
                ProcessStartInfo k = new ProcessStartInfo("taskkill", "/T /F /PID " + proc.Id);
                k.CreateNoWindow = true; k.UseShellExecute = false;
                Process.Start(k);
            }
            catch { }
            AppendText("\r\n*** Canceled by user ***\r\n");
        }
    }

    void OnClosing(object sender, FormClosingEventArgs e)
    {
        if (proc != null && !proc.HasExited)
        {
            if (!autoMode && MessageBox.Show("A command is still running. Cancel it and exit?", "Exit",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) { e.Cancel = true; return; }
            CancelRun();
        }
    }
}
