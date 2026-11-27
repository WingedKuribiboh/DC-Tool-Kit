using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace UefiRebuilder
{
    /// <summary>
    /// Rebuilds the EFI partition the same way you would by hand:
    ///   diskpart: list disk / sel disk N / list vol / sel vol M / assign letter=V / exit
    ///   format V: /FS:FAT32
    ///   bcdboot C:\Windows /s V: /f UEFI
    /// </summary>
    internal sealed class MainForm : Form
    {
        private const string PreferredLetter = "V";

        private readonly ComboBox _windowsCombo = new ComboBox();
        private readonly Button _browseButton = new Button();
        private readonly ComboBox _diskCombo = new ComboBox();
        private readonly Button _refreshButton = new Button();
        private readonly ComboBox _volumeCombo = new ComboBox();
        private readonly Button _rebuildButton = new Button();
        private readonly Button _clearButton = new Button();
        private readonly Button _saveLogButton = new Button();
        private readonly RichTextBox _terminal = new RichTextBox();
        private readonly Label _statusLabel = new Label();

        private readonly Dictionary<int, List<VolumeEntry>> _volumesByDisk = new Dictionary<int, List<VolumeEntry>>();
        private bool _busy;
        private readonly bool _inWinPE;
        private readonly string _systemRoot = (Environment.GetEnvironmentVariable("SystemRoot") ?? @"X:\Windows").TrimEnd('\\');

        public MainForm()
        {
            Text = "UEFI Partition Rebuilder";
            Width = 980;
            Height = 680;
            MinimumSize = new Size(780, 520);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);

            using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\MiniNT"))
                _inWinPE = key != null;

            BuildLayout();

            Load += async (s, e) => await InitialiseAsync();
            FormClosing += (s, e) =>
            {
                if (!_busy) return;
                e.Cancel = true;
                MessageBox.Show(this, "A rebuild is in progress. Please wait for it to finish.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
        }

        // ------------------------------------------------------------------ UI

        private void BuildLayout()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 6, Padding = new Padding(10) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            for (int i = 0; i < 4; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var mono = new Font("Consolas", 9.5f);

            // Row 0: Windows folder (bcdboot source)
            root.Controls.Add(MakeLabel("1. Windows folder:"), 0, 0);
            _windowsCombo.DropDownStyle = ComboBoxStyle.DropDown;
            _windowsCombo.Dock = DockStyle.Fill;
            _windowsCombo.Font = mono;
            _windowsCombo.SelectedIndexChanged += (s, e) => SelectDiskForWindows();
            root.Controls.Add(_windowsCombo, 1, 0);
            _browseButton.Text = "Browse...";
            _browseButton.AutoSize = true;
            _browseButton.Click += (s, e) => BrowseForWindows();
            root.Controls.Add(_browseButton, 2, 0);

            // Row 1: disk (diskpart "list disk")
            root.Controls.Add(MakeLabel("2. Boot disk:"), 0, 1);
            _diskCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _diskCombo.Dock = DockStyle.Fill;
            _diskCombo.Font = mono;
            _diskCombo.SelectedIndexChanged += (s, e) => ShowVolumesForSelectedDisk();
            root.Controls.Add(_diskCombo, 1, 1);
            _refreshButton.Text = "Refresh";
            _refreshButton.AutoSize = true;
            _refreshButton.Click += async (s, e) => await ScanDisksAsync();
            root.Controls.Add(_refreshButton, 2, 1);

            // Row 2: EFI volume (diskpart "list vol" for that disk)
            root.Controls.Add(MakeLabel("3. EFI partition:"), 0, 2);
            _volumeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _volumeCombo.Dock = DockStyle.Fill;
            _volumeCombo.Font = mono;
            root.Controls.Add(_volumeCombo, 1, 2);

            // Row 3: actions
            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            _rebuildButton.Text = "4. Format && Rebuild EFI Partition";
            _rebuildButton.AutoSize = true;
            _rebuildButton.Padding = new Padding(8, 4, 8, 4);
            _rebuildButton.BackColor = Color.FromArgb(192, 40, 40);
            _rebuildButton.ForeColor = Color.White;
            _rebuildButton.FlatStyle = FlatStyle.Flat;
            _rebuildButton.Font = new Font(Font, FontStyle.Bold);
            _rebuildButton.Click += async (s, e) => await RebuildAsync();
            _clearButton.Text = "Clear log";
            _clearButton.AutoSize = true;
            _clearButton.Margin = new Padding(12, 6, 3, 3);
            _clearButton.Click += (s, e) => _terminal.Clear();
            _saveLogButton.Text = "Save log...";
            _saveLogButton.AutoSize = true;
            _saveLogButton.Margin = new Padding(3, 6, 3, 3);
            _saveLogButton.Click += (s, e) => SaveLog();
            actions.Controls.AddRange(new Control[] { _rebuildButton, _clearButton, _saveLogButton });
            root.Controls.Add(actions, 0, 3);
            root.SetColumnSpan(actions, 3);

            // Row 4: terminal
            _terminal.Dock = DockStyle.Fill;
            _terminal.ReadOnly = true;
            _terminal.BackColor = Color.FromArgb(12, 12, 12);
            _terminal.ForeColor = Color.FromArgb(204, 204, 204);
            _terminal.Font = new Font("Consolas", 10f);
            _terminal.BorderStyle = BorderStyle.None;
            _terminal.WordWrap = false;
            _terminal.ScrollBars = RichTextBoxScrollBars.Both;
            var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6), BackColor = _terminal.BackColor };
            host.Controls.Add(_terminal);
            root.Controls.Add(host, 0, 4);
            root.SetColumnSpan(host, 3);

            // Row 5: status
            _statusLabel.AutoSize = true;
            _statusLabel.Text = "Ready.";
            root.Controls.Add(_statusLabel, 0, 5);
            root.SetColumnSpan(_statusLabel, 3);

            Controls.Add(root);
        }

        private static Label MakeLabel(string text) =>
            new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 6, 3) };

        private void SetBusy(bool busy, string status)
        {
            _busy = busy;
            foreach (Control c in new Control[] { _windowsCombo, _browseButton, _diskCombo, _refreshButton, _volumeCombo, _rebuildButton })
                c.Enabled = !busy;
            _statusLabel.Text = status;
            UseWaitCursor = busy;
        }

        // ------------------------------------------------------------ terminal

        private enum LogKind { Info, Command, Output, Error, Success, Warn, Step }

        private void Log(string text, LogKind kind = LogKind.Info)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => Log(text, kind))); return; }

            Color color;
            switch (kind)
            {
                case LogKind.Command: color = Color.FromArgb(97, 175, 239); break;
                case LogKind.Output: color = Color.FromArgb(204, 204, 204); break;
                case LogKind.Error: color = Color.FromArgb(240, 80, 80); break;
                case LogKind.Success: color = Color.FromArgb(110, 210, 110); break;
                case LogKind.Warn: color = Color.FromArgb(230, 190, 80); break;
                case LogKind.Step: color = Color.White; break;
                default: color = Color.FromArgb(150, 150, 150); break;
            }

            _terminal.SelectionStart = _terminal.TextLength;
            _terminal.SelectionLength = 0;
            _terminal.SelectionColor = color;
            _terminal.AppendText(text + Environment.NewLine);
            _terminal.SelectionColor = _terminal.ForeColor;
            _terminal.ScrollToCaret();
        }

        private void SaveLog()
        {
            using (var dlg = new SaveFileDialog { Filter = "Text file (*.txt)|*.txt", FileName = $"uefi-rebuild-{DateTime.Now:yyyyMMdd-HHmmss}.txt" })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    File.WriteAllText(dlg.FileName, _terminal.Text);
            }
        }

        // ------------------------------------------------------------ startup

        private async Task InitialiseAsync()
        {
            Log("UEFI Partition Rebuilder", LogKind.Success);
            Log($"Running on {Environment.OSVersion}   Firmware: {GetFirmwareTypeName()}   WinPE: {(_inWinPE ? "yes" : "no")}");
            Log("");
            LoadWindowsInstalls();
            await ScanDisksAsync();
        }

        [DllImport("kernel32.dll")]
        private static extern bool GetFirmwareType(out uint firmwareType);

        private static string GetFirmwareTypeName()
        {
            try
            {
                if (GetFirmwareType(out uint type))
                    return type == 2 ? "UEFI" : type == 1 ? "Legacy BIOS" : "Unknown";
            }
            catch (EntryPointNotFoundException) { }
            return "Unknown";
        }

        /// <summary>True for the Windows folder of a running WinPE / recovery environment (usually X:\Windows).</summary>
        private bool IsRunningPE(string winDir) =>
            _inWinPE && string.Equals(winDir.TrimEnd('\\'), _systemRoot, StringComparison.OrdinalIgnoreCase);

        private void LoadWindowsInstalls()
        {
            _windowsCombo.Items.Clear();
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                    string winDir = Path.Combine(drive.RootDirectory.FullName, "Windows");
                    if (!File.Exists(Path.Combine(winDir, "System32", "config", "SYSTEM"))) continue;
                    if (IsRunningPE(winDir))
                    {
                        Log($"Skipping {winDir}: that is the WinPE you booted from, not the installed Windows.", LogKind.Warn);
                        continue;
                    }
                    _windowsCombo.Items.Add(winDir);
                }
                catch { /* unreadable drive */ }
            }

            int c = _windowsCombo.Items.Cast<string>().ToList()
                .FindIndex(p => p.StartsWith("C:", StringComparison.OrdinalIgnoreCase));
            if (c >= 0) _windowsCombo.SelectedIndex = c;
            else if (_windowsCombo.Items.Count > 0) _windowsCombo.SelectedIndex = 0;

            if (_windowsCombo.Items.Count == 0)
                Log("No installed Windows found. If BitLocker is on, unlock the drive first, then use Browse.", LogKind.Warn);
            else
                Log("Installed Windows found: " + string.Join(", ", _windowsCombo.Items.Cast<string>()), LogKind.Success);
        }

        private void BrowseForWindows()
        {
            using (var dlg = new FolderBrowserDialog { Description = "Select the installed Windows folder (e.g. C:\\Windows)" })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    _windowsCombo.Text = dlg.SelectedPath;
                    SelectDiskForWindows();
                }
            }
        }

        // ------------------------------------------------- disks and volumes

        private async Task ScanDisksAsync()
        {
            if (_busy) return;
            SetBusy(true, "Reading disks with diskpart...");
            _diskCombo.Items.Clear();
            _volumeCombo.Items.Clear();
            _volumesByDisk.Clear();

            Log("");
            Log("Scanning disks (diskpart: list disk)", LogKind.Step);
            try
            {
            var output = await RunDiskpartAsync(new[] { "list disk" }, echoOutput: true);

            foreach (var cells in DiskpartTable.Parse(output, "Disk"))
            {
                int n = DiskpartTable.Number(cells[0]);
                if (n < 0) continue;
                string gpt = cells.Length > 5 && cells[5].Contains("*") ? "GPT" : "MBR";
                _diskCombo.Items.Add(new DiskEntry
                {
                    Number = n,
                    Line = $"{cells[0],-8} {Get(cells, 1),-10} {Get(cells, 2),9}   {gpt}",
                });
            }

            foreach (DiskEntry disk in _diskCombo.Items)
            {
                Log("");
                Log($"Reading volumes on Disk {disk.Number} (diskpart: sel disk {disk.Number} / detail disk)", LogKind.Step);
                var detail = await RunDiskpartAsync(new[] { $"select disk {disk.Number}", "detail disk" }, echoOutput: true);
                var vols = new List<VolumeEntry>();
                foreach (var cells in DiskpartTable.Parse(detail, "Volume"))
                {
                    int n = DiskpartTable.Number(cells[0]);
                    if (n < 0) continue;
                    vols.Add(new VolumeEntry
                    {
                        Number = n,
                        Letter = Get(cells, 1),
                        Label = Get(cells, 2),
                        Fs = Get(cells, 3),
                        Info = Get(cells, 7),
                        Line = $"{cells[0],-10} {Get(cells, 1),-3} {Get(cells, 2),-11} {Get(cells, 3),-5} {Get(cells, 5),8}  {Get(cells, 7)}",
                    });
                }
                _volumesByDisk[disk.Number] = vols;
            }
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex.Message, LogKind.Error);
            }

            Log("");
            if (_diskCombo.Items.Count == 0)
                Log("diskpart did not report any disks.", LogKind.Error);
            else
                Log($"Found {_diskCombo.Items.Count} disk(s).", LogKind.Success);

            SetBusy(false, "Ready.");
            SelectDiskForWindows();
            if (_diskCombo.SelectedIndex < 0 && _diskCombo.Items.Count > 0) _diskCombo.SelectedIndex = 0;
        }

        private static string Get(string[] cells, int i) => i < cells.Length ? cells[i] : "";

        /// <summary>Pre-selects the disk that holds the chosen Windows folder.</summary>
        private void SelectDiskForWindows()
        {
            string win = _windowsCombo.Text.Trim();
            if (win.Length < 2 || win[1] != ':') return;
            string letter = win.Substring(0, 1);
            for (int i = 0; i < _diskCombo.Items.Count; i++)
            {
                var disk = (DiskEntry)_diskCombo.Items[i];
                if (_volumesByDisk.TryGetValue(disk.Number, out var vols) &&
                    vols.Any(v => v.Letter.Equals(letter, StringComparison.OrdinalIgnoreCase)))
                {
                    _diskCombo.SelectedIndex = i;
                    return;
                }
            }
        }

        private void ShowVolumesForSelectedDisk()
        {
            _volumeCombo.Items.Clear();
            if (!(_diskCombo.SelectedItem is DiskEntry disk) || !_volumesByDisk.TryGetValue(disk.Number, out var vols)) return;
            foreach (var v in vols) _volumeCombo.Items.Add(v);
            int efi = vols.FindIndex(v => v.LooksLikeEfi);
            if (efi >= 0) _volumeCombo.SelectedIndex = efi;
            else if (vols.Count > 0) _volumeCombo.SelectedIndex = 0;
        }

        // ------------------------------------------------------------ rebuild

        private async Task RebuildAsync()
        {
            if (!(_diskCombo.SelectedItem is DiskEntry disk) || !(_volumeCombo.SelectedItem is VolumeEntry vol))
            {
                MessageBox.Show(this, "Select the boot disk and its EFI partition first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string winDir = _windowsCombo.Text.Trim().TrimEnd('\\');
            if (IsRunningPE(winDir))
            {
                MessageBox.Show(this, $"{winDir} is the WinPE environment you booted from, not the installed Windows.\n\n" +
                    "Pick the installed Windows folder (usually C:\\Windows).", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (!File.Exists(Path.Combine(winDir, "System32", "config", "SYSTEM")) || !Directory.Exists(Path.Combine(winDir, "Boot", "EFI")))
            {
                MessageBox.Show(this, $"\"{winDir}\" does not look like an installed Windows folder.", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string warn = $"This will ERASE and rebuild:\n\n    Disk {disk.Number}  ->  {vol.Line.Trim()}\n\n";
            if (!vol.LooksLikeEfi)
                warn += "WARNING: diskpart does not mark this volume as the EFI/System partition!\n\n";
            warn += $"Boot files will be created from:  {winDir}\n\n" +
                    "Any other OS boot loaders on this partition will be lost.\n\nContinue?";
            if (MessageBox.Show(this, warn, "Confirm format", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                Log("Cancelled.", LogKind.Warn);
                return;
            }

            SetBusy(true, "Rebuilding EFI partition...");
            string letter = null;
            bool weAssigned = false;
            bool ok = false;
            try
            {
                Log("");
                Log($"=== Rebuilding EFI partition: Disk {disk.Number}, Volume {vol.Number} ===", LogKind.Success);

                // --- Step 1: diskpart, assign a drive letter
                Log("");
                Log("[1/4] Assign a drive letter (diskpart)", LogKind.Step);
                if (vol.Letter.Length == 1 && Directory.Exists(vol.Letter + ":\\"))
                {
                    letter = vol.Letter.ToUpperInvariant();
                    Log($"  Volume {vol.Number} already has letter {letter}:, using it.");
                }
                else
                {
                    letter = FindFreeDriveLetter() ?? throw new InvalidOperationException("No free drive letter available.");
                    await RunDiskpartAsync(new[]
                    {
                        "list disk",
                        $"sel disk {disk.Number}",
                        "list vol",
                        $"sel vol {vol.Number}",
                        $"assign letter={letter}",
                    }, echoOutput: true);
                    weAssigned = true;
                    await WaitForDriveAsync(letter);
                    if (!Directory.Exists(letter + ":\\"))
                        throw new InvalidOperationException($"diskpart did not make {letter}: available.");
                }
                Log($"  EFI partition is mounted at {letter}:", LogKind.Success);

                // --- Step 2: format V: /FS:FAT32
                Log("");
                Log($"[2/4] Format {letter}: as FAT32", LogKind.Step);
                // format asks for the current label (if any) and then "Proceed with Format (Y/N)?".
                string answers = (vol.Label.Length > 0 ? vol.Label + "\r\n" : "") + "Y\r\nY\r\n";
                int fmt = await RunProcessAsync(SystemTool("format.com"),
                    $"{letter}: /FS:FAT32 /Q /V:SYSTEM /X /Y", echo: true, stdin: answers);
                if (fmt != 0) throw new InvalidOperationException($"format failed (exit code {fmt}).");
                await WaitForDriveAsync(letter);

                // --- Step 3: bcdboot C:\Windows /s V: /f UEFI
                Log("");
                Log("[3/4] Write boot files (bcdboot)", LogKind.Step);
                int bcd = await RunProcessAsync(SystemTool("bcdboot.exe"), $"\"{winDir}\" /s {letter}: /f UEFI", echo: true);
                if (bcd != 0) throw new InvalidOperationException($"bcdboot failed (exit code {bcd}).");

                // --- Step 4: verify
                Log("");
                Log("[4/4] Verify", LogKind.Step);
                bool allGood = true;
                foreach (string f in new[] { @"EFI\Microsoft\Boot\bootmgfw.efi", @"EFI\Microsoft\Boot\BCD", @"EFI\Boot\bootx64.efi" })
                {
                    string full = $"{letter}:\\{f}";
                    bool exists = File.Exists(full);
                    allGood &= exists;
                    Log($"  {(exists ? "OK     " : "MISSING")}  {full}", exists ? LogKind.Success : LogKind.Error);
                }
                if (!allGood) throw new InvalidOperationException("Some boot files are missing after bcdboot.");
                ok = true;
            }
            catch (Exception ex)
            {
                Log("ERROR: " + ex.Message, LogKind.Error);
            }
            finally
            {
                if (weAssigned && letter != null)
                {
                    Log("");
                    Log($"Removing temporary letter {letter}: (diskpart)", LogKind.Step);
                    try { await RunDiskpartAsync(new[] { $"sel vol {vol.Number}", $"remove letter={letter}" }, echoOutput: true); }
                    catch (Exception ex) { Log("Could not remove the letter: " + ex.Message, LogKind.Warn); }
                }
            }

            Log("");
            if (ok)
            {
                Log("=== Done: EFI partition rebuilt. Remove the USB and reboot. ===", LogKind.Success);
                SetBusy(false, "Rebuild completed successfully.");
            }
            else
            {
                Log("=== Rebuild FAILED. Use 'Save log...' and review the output above. ===", LogKind.Error);
                SetBusy(false, "Rebuild failed.");
            }
        }

        private static string FindFreeDriveLetter()
        {
            var used = new HashSet<char>(DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])));
            foreach (char c in PreferredLetter + "WSTUQRPYZ")
                if (!used.Contains(c)) return c.ToString();
            return null;
        }

        private static async Task WaitForDriveAsync(string letter)
        {
            for (int i = 0; i < 20 && !Directory.Exists(letter + ":\\"); i++)
                await Task.Delay(250);
        }

        // ------------------------------------------------------ process utils

        private static string SystemTool(string exe)
        {
            string sysnative = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Sysnative", exe);
            if (!Environment.Is64BitProcess && File.Exists(sysnative)) return sysnative;
            return Path.Combine(Environment.SystemDirectory, exe);
        }

        /// <summary>Runs diskpart with the given commands (plus "exit") and returns its output lines.</summary>
        private async Task<List<string>> RunDiskpartAsync(IEnumerable<string> commands, bool echoOutput)
        {
            var cmds = commands.Concat(new[] { "exit" }).ToList();
            string path = Path.Combine(Path.GetTempPath(), $"uefi-rebuild-{Guid.NewGuid():N}.txt");
            File.WriteAllText(path, string.Join("\r\n", cmds) + "\r\n", Encoding.ASCII);
            var output = new List<string>();
            try
            {
                Log("> diskpart", LogKind.Command);
                foreach (string c in cmds) Log("  DISKPART> " + c, LogKind.Command);
                int exit = await RunProcessAsync(SystemTool("diskpart.exe"), $"/s \"{path}\"", echo: false,
                    collect: output, showOutput: echoOutput);
                // In /s mode diskpart stops at the first failing command and returns non-zero.
                if (exit != 0) throw new InvalidOperationException($"diskpart failed (exit code {exit}).");
            }
            finally
            {
                try { File.Delete(path); } catch { }
            }
            return output;
        }

        private Task<int> RunProcessAsync(string fileName, string arguments, bool echo,
            List<string> collect = null, bool showOutput = true, string stdin = null)
        {
            var tcs = new TaskCompletionSource<int>();
            Encoding oem;
            try { oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
            catch { oem = Encoding.Default; }

            var psi = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = stdin != null,
                StandardOutputEncoding = oem,
                StandardErrorEncoding = oem,
            };

            if (echo) Log($"> {Path.GetFileNameWithoutExtension(fileName)} {arguments}", LogKind.Command);

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                lock (proc) collect?.Add(e.Data);
                string t = e.Data.Trim();
                if (!showOutput || t.Length == 0) return;
                // format/diskpart print progress with carriage returns; keep only the final 100%.
                if (t.IndexOf("percent completed", StringComparison.OrdinalIgnoreCase) >= 0 && !t.StartsWith("100")) return;
                Log("  " + e.Data.TrimEnd(), LogKind.Output);
            };
            proc.ErrorDataReceived += (s, e) =>
            {
                if (e.Data == null || e.Data.Trim().Length == 0) return;
                Log("  " + e.Data, LogKind.Error);
            };
            proc.Exited += (s, e) =>
            {
                proc.WaitForExit(); // flushes the async readers
                int code = proc.ExitCode;
                proc.Dispose();
                Log($"  (exit code {code})", code == 0 ? LogKind.Info : LogKind.Error);
                tcs.TrySetResult(code);
            };

            try
            {
                proc.Start();
                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();
                if (stdin != null)
                {
                    proc.StandardInput.Write(stdin);
                    proc.StandardInput.Close();
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to start {fileName}: {ex.Message}", LogKind.Error);
                tcs.TrySetResult(-1);
            }
            return tcs.Task;
        }
    }
}
