// Net User Manager - WinForms front end for the Windows `net user` command.
// Targets .NET Framework 4.8 (built into Windows 10/11), so the exe runs with no installs.
// Build: src/net-user-gui/build.sh (Mac) or build.bat (Windows) -> Tools/NetUserManager.exe

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Net User Manager")]
[assembly: System.Reflection.AssemblyProduct("Net User Manager")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]

namespace NetUserManager
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct USER_INFO_0 { public string usri0_name; }

        [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
        static extern int NetUserSetInfo(string server, string userName, int level, ref USER_INFO_0 buf, out int parmErr);

        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass,
            out IntPtr buffer, out int bytes);

        [DllImport("wtsapi32.dll")]
        static extern void WTSFreeMemory(IntPtr memory);

        // `net user` cannot rename an account, so call the API it is built on.
        // Returns null on success, otherwise an error message.
        public static string RenameUser(string oldName, string newName)
        {
            var info = new USER_INFO_0 { usri0_name = newName };
            int parmErr;
            int rc = NetUserSetInfo(null, oldName, 0, ref info, out parmErr);
            switch (rc)
            {
                case 0: return null;
                case 5: return "Access denied. Run as administrator.";
                case 2221: return "The user '" + oldName + "' was not found.";
                case 2224: return "An account named '" + newName + "' already exists.";
                case 1316: return "An account or group named '" + newName + "' already exists.";
                default: return new System.ComponentModel.Win32Exception(rc).Message + " (error " + rc + ")";
            }
        }

        // The user signed in to this desktop session. Unlike Environment.UserName this stays
        // correct when a standard user elevates the app with a different admin account.
        public static string SessionUserName()
        {
            const int WTSUserName = 5, WTS_CURRENT_SESSION = -1;
            IntPtr buf;
            int bytes;
            try
            {
                if (WTSQuerySessionInformation(IntPtr.Zero, WTS_CURRENT_SESSION, WTSUserName, out buf, out bytes))
                {
                    try
                    {
                        string name = Marshal.PtrToStringUni(buf);
                        if (!string.IsNullOrEmpty(name)) return name;
                    }
                    finally { WTSFreeMemory(buf); }
                }
            }
            catch { }
            return Environment.UserName;
        }
    }

    static class Net
    {
        // null if valid, otherwise why not.
        public static string UserNameError(string name)
        {
            if (name.Length == 0) return "Enter a username.";
            if (name.Length > 20 || Regex.IsMatch(name, @"[""/\\\[\]:;|=,+*?<>@]") || name.Trim('.', ' ').Length == 0)
                return "Usernames can be up to 20 characters, cannot be only dots or spaces, and cannot contain:\n\" / \\ [ ] : ; | = , + * ? < > @";
            return null;
        }

        static readonly Lazy<string> adminGroup = new Lazy<string>(() =>
        {
            // Resolve the built-in Administrators group by SID so it works on non-English Windows.
            try
            {
                string full = new SecurityIdentifier("S-1-5-32-544")
                    .Translate(typeof(NTAccount)).Value;
                return full.Substring(full.LastIndexOf('\\') + 1);
            }
            catch { return "Administrators"; }
        });

        public static string AdminGroup { get { return adminGroup.Value; } }

        // Quote one argument using the standard Windows command-line rules.
        static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in arg)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') sb.Append('\\', slashes * 2 + 1);
                else sb.Append('\\', slashes);
                slashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', slashes * 2);
            return sb.Append('"').ToString();
        }

        public static bool Run(out string output, params string[] args)
        {
            int oem = CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
            var psi = new ProcessStartInfo("net.exe", string.Join(" ", args.Select(Quote)))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.GetEncoding(oem),
                StandardErrorEncoding = Encoding.GetEncoding(oem),
            };
            using (var p = Process.Start(psi))
            {
                var errTask = p.StandardError.ReadToEndAsync();
                string stdout = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                output = (stdout + "\n" + errTask.Result).Trim();
                return p.ExitCode == 0;
            }
        }

        // Lines between the dashed separator and "The command completed..."
        static IEnumerable<string> ListBody(string output)
        {
            bool inList = false;
            foreach (string line in output.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                if (Regex.IsMatch(line, "^-{5,}")) { inList = true; continue; }
                if (!inList || line.Trim().Length == 0) continue;
                if (line.StartsWith("The command completed", StringComparison.OrdinalIgnoreCase)) yield break;
                yield return line;
            }
        }

        public static List<string> ListUsers()
        {
            string output;
            if (!Run(out output, "user")) throw new Exception(output);
            var users = new List<string>();
            // `net user` prints names in fixed 25-char columns (keeps names with spaces intact).
            foreach (string line in ListBody(output))
                for (int i = 0; i < line.Length; i += 25)
                {
                    string name = line.Substring(i, Math.Min(25, line.Length - i)).Trim();
                    if (name.Length > 0) users.Add(name);
                }
            users.Sort(StringComparer.OrdinalIgnoreCase);
            return users;
        }

        public static HashSet<string> ListAdmins()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string output;
            if (!Run(out output, "localgroup", AdminGroup)) return set;
            foreach (string line in ListBody(output))
            {
                string m = line.Trim();
                set.Add(m.Substring(m.LastIndexOf('\\') + 1));
            }
            return set;
        }
    }

    class NewUserForm : Form
    {
        readonly TextBox txtUser = new TextBox { Width = 200 };
        readonly TextBox txtPw = new TextBox { Width = 200, UseSystemPasswordChar = true };
        readonly TextBox txtPw2 = new TextBox { Width = 200, UseSystemPasswordChar = true };
        readonly CheckBox chkAdmin = new CheckBox { Text = "Make administrator", AutoSize = true };

        public string UserName { get { return txtUser.Text.Trim(); } }
        public string Password { get { return txtPw.Text; } }
        public bool MakeAdmin { get { return chkAdmin.Checked; } }

        public NewUserForm()
        {
            Text = "Create New User";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);

            var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
            AddRow(t, "Username:", txtUser);
            AddRow(t, "Password:", txtPw);
            AddRow(t, "Confirm:", txtPw2);
            t.Controls.Add(new Label(), 0, 3);
            t.Controls.Add(chkAdmin, 1, 3);

            var ok = new Button { Text = "Create", DialogResult = DialogResult.None };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) => { if (ValidateInput()) { DialogResult = DialogResult.OK; } };
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            t.Controls.Add(buttons, 0, 4);
            t.SetColumnSpan(buttons, 2);

            Controls.Add(t);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        static void AddRow(TableLayoutPanel t, string label, Control c)
        {
            int row = t.Controls.Count / 2;
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            t.Controls.Add(c, 1, row);
        }

        bool ValidateInput()
        {
            string err = Net.UserNameError(UserName);
            if (err != null)
            {
                MessageBox.Show(this, err, "Invalid username", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            if (txtPw.Text != txtPw2.Text)
            {
                MessageBox.Show(this, "Passwords do not match.", "Mismatch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            return true;
        }
    }

    class RenameForm : Form
    {
        readonly TextBox txtName = new TextBox { Width = 200 };
        readonly string oldName;

        public string NewName { get { return txtName.Text.Trim(); } }

        public RenameForm(string oldName)
        {
            this.oldName = oldName;
            Text = "Rename User";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(12);

            var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
            t.Controls.Add(new Label { Text = "Current name:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            t.Controls.Add(new Label { Text = oldName, AutoSize = true, Anchor = AnchorStyles.Left, Font = new Font(Font, FontStyle.Bold) }, 1, 0);
            t.Controls.Add(new Label { Text = "New name:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            txtName.Text = oldName;
            t.Controls.Add(txtName, 1, 1);

            var ok = new Button { Text = "Rename" };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) => { if (ValidateInput()) DialogResult = DialogResult.OK; };
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill };
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(ok);
            t.Controls.Add(buttons, 0, 2);
            t.SetColumnSpan(buttons, 2);

            Controls.Add(t);
            AcceptButton = ok;
            CancelButton = cancel;
            Shown += (s, e) => txtName.SelectAll();
        }

        bool ValidateInput()
        {
            string err = Net.UserNameError(NewName);
            if (err != null)
            {
                MessageBox.Show(this, err, "Invalid username", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            if (NewName == oldName)
            {
                DialogResult = DialogResult.Cancel;
                return false;
            }
            return true;
        }
    }

    class MainForm : Form
    {
        readonly ComboBox cboUser = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 220 };
        readonly TextBox txtPw = new TextBox { Width = 220, UseSystemPasswordChar = true };
        readonly TextBox txtPw2 = new TextBox { Width = 220, UseSystemPasswordChar = true };
        readonly CheckBox chkAdmin = new CheckBox { Text = "Administrator", AutoSize = true };
        readonly ToolStripStatusLabel status = new ToolStripStatusLabel("Ready");
        HashSet<string> admins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public MainForm()
        {
            Text = "Net User Manager";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Font = new Font("Segoe UI", 9F);

            var t = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Padding = new Padding(12), Dock = DockStyle.Fill };

            var btnRefresh = new Button { Text = "⟳", Width = 30 };
            btnRefresh.Click += (s, e) => RefreshUsers(null);
            Row(t, 0, "User:", cboUser, btnRefresh);
            Row(t, 1, "New password:", txtPw, null);
            Row(t, 2, "Confirm:", txtPw2, null);

            var btnPw = Wide("Change Password", ChangePassword);
            t.Controls.Add(btnPw, 1, 3);

            t.Controls.Add(chkAdmin, 1, 4);
            t.Controls.Add(Wide("Apply Admin Setting", ApplyAdmin), 1, 5);
            t.Controls.Add(Wide("Rename User...", RenameUser), 1, 6);

            var sep = new Label { BorderStyle = BorderStyle.Fixed3D, Height = 2, Dock = DockStyle.Fill, Margin = new Padding(0, 10, 0, 10) };
            t.Controls.Add(sep, 0, 7);
            t.SetColumnSpan(sep, 3);

            var btnNew = Wide("Create New User...", CreateUser);
            t.Controls.Add(btnNew, 0, 8);
            t.SetColumnSpan(btnNew, 3);

            var strip = new StatusStrip { SizingGrip = false };
            strip.Items.Add(status);

            Controls.Add(t);
            Controls.Add(strip);

            cboUser.SelectedIndexChanged += (s, e) => OnUserSelected();
            Load += (s, e) => RefreshUsers(null);
        }

        static void Row(TableLayoutPanel t, int row, string label, Control c, Control extra)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left }, 0, row);
            t.Controls.Add(c, 1, row);
            if (extra != null) t.Controls.Add(extra, 2, row);
        }

        static Button Wide(string text, Action onClick)
        {
            var b = new Button { Text = text, Dock = DockStyle.Fill, Height = 28 };
            b.Click += (s, e) => onClick();
            return b;
        }

        string SelectedUser()
        {
            string u = cboUser.SelectedItem as string;
            if (string.IsNullOrEmpty(u))
                MessageBox.Show(this, "Select a user first.", "No user", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return u;
        }

        void Report(bool ok, string output, string successMsg)
        {
            if (ok)
            {
                status.Text = successMsg;
                MessageBox.Show(this, successMsg, "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                status.Text = "Command failed";
                MessageBox.Show(this, string.IsNullOrEmpty(output) ? "Unknown error" : output, "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void RefreshUsers(string select)
        {
            List<string> users;
            try { users = Net.ListUsers(); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Error listing users", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            admins = Net.ListAdmins();
            // First load defaults to the signed-in user.
            string current = select ?? cboUser.SelectedItem as string ?? Native.SessionUserName();
            cboUser.Items.Clear();
            cboUser.Items.AddRange(users.ToArray());
            int idx = current == null ? -1 : users.FindIndex(u => u.Equals(current, StringComparison.OrdinalIgnoreCase));
            cboUser.SelectedIndex = idx >= 0 ? idx : (users.Count > 0 ? 0 : -1);
            OnUserSelected();
            status.Text = "Loaded " + users.Count + " users";
        }

        void OnUserSelected()
        {
            string u = cboUser.SelectedItem as string;
            chkAdmin.Checked = u != null && admins.Contains(u);
            txtPw.Clear();
            txtPw2.Clear();
        }

        void ChangePassword()
        {
            string user = SelectedUser();
            if (user == null) return;
            if (txtPw.Text != txtPw2.Text)
            {
                MessageBox.Show(this, "Passwords do not match.", "Mismatch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (txtPw.Text.Length == 0 && MessageBox.Show(this, "Set a BLANK password for '" + user + "'?",
                    "Blank password", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            string output;
            bool ok = Net.Run(out output, "user", user, txtPw.Text);
            Report(ok, output, "Password changed for '" + user + "'");
            if (ok) { txtPw.Clear(); txtPw2.Clear(); }
        }

        void ApplyAdmin()
        {
            string user = SelectedUser();
            if (user == null) return;
            bool want = chkAdmin.Checked, have = admins.Contains(user);
            if (want == have)
            {
                status.Text = "'" + user + "' is already " + (have ? "an admin" : "not an admin");
                return;
            }
            string output;
            bool ok = Net.Run(out output, "localgroup", Net.AdminGroup, user, want ? "/add" : "/delete");
            Report(ok, output, "'" + user + "' " + (want ? "added to " : "removed from ") + Net.AdminGroup);
            RefreshUsers(user);
        }

        void RenameUser()
        {
            string user = SelectedUser();
            if (user == null) return;
            using (var dlg = new RenameForm(user))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string newName = dlg.NewName;
                bool isSelf = user.Equals(Native.SessionUserName(), StringComparison.OrdinalIgnoreCase);
                if (isSelf && MessageBox.Show(this,
                        "'" + user + "' is the account you are signed in with. The new name takes full effect after " +
                        "signing out. The profile folder (C:\\Users\\" + user + ") keeps its old name.\n\nRename anyway?",
                        "Rename current user", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
                string err = Native.RenameUser(user, newName);
                Report(err == null, err, "Renamed '" + user + "' to '" + newName + "'");
                RefreshUsers(err == null ? newName : user);
            }
        }

        void CreateUser()
        {
            using (var dlg = new NewUserForm())
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string output;
                if (!Net.Run(out output, "user", dlg.UserName, dlg.Password, "/add"))
                {
                    Report(false, output, null);
                    return;
                }
                string msg = "User '" + dlg.UserName + "' created";
                if (dlg.MakeAdmin)
                {
                    if (Net.Run(out output, "localgroup", Net.AdminGroup, dlg.UserName, "/add"))
                        msg += " as administrator";
                    else
                        MessageBox.Show(this, output, "Admin step failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                Report(true, null, msg);
                RefreshUsers(dlg.UserName);
            }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
