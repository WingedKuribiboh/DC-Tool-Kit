// Tools\ launcher stub. build.sh / build.bat compile one copy per line of launchers.txt,
// replacing @TARGET@ with the program's path relative to the toolkit root (e.g. programs\Rufus\Rufus.exe).
// The toolkit root is the first folder above this exe that contains programs\ (normally the stick root).
using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

static class Launcher
{
    const string Target = @"@TARGET@";

    [STAThread]
    static void Main()
    {
        try
        {
            string root = FindRoot();
            string exe = Path.GetFullPath(Path.Combine(root, Target));
            if (!File.Exists(exe))
            {
                MessageBox.Show("Program not found:\n" + exe +
                    "\n\nRun UpdateToolkit.exe (toolkit root) to download it, or copy it there by hand.",
                    "Launcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            ProcessStartInfo psi = new ProcessStartInfo(exe);
            psi.WorkingDirectory = Path.GetDirectoryName(exe);
            psi.UseShellExecute = true;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Launcher");
        }
    }

    static string FindRoot()
    {
        string d = AppDomain.CurrentDomain.BaseDirectory;
        while (!string.IsNullOrEmpty(d))
        {
            if (Directory.Exists(Path.Combine(d, "programs"))) return d;
            DirectoryInfo p = Directory.GetParent(d.TrimEnd('\\', '/'));
            d = p == null ? null : p.FullName;
        }
        return Path.GetPathRoot(AppDomain.CurrentDomain.BaseDirectory);
    }
}
