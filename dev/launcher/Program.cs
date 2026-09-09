using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
[assembly: AssemblyTitle("CustomPersonaTranslator")]
[assembly: AssemblyDescription("Launch the packaged CustomPersonaTranslator app")]
[assembly: AssemblyVersion("0.4.21.0")]
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dev", "app");
        var executable = Path.Combine(folder, "CPT.Shell.exe");
        try
        {
            if (!File.Exists(executable)) throw new FileNotFoundException("Keep CPT.exe alongside the dev folder. Download or clone the complete repository before launching.", executable);
            Process.Start(new ProcessStartInfo(executable, string.Join(" ", args.Select(Quote)))
            { WorkingDirectory = folder, UseShellExecute = false });
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "CustomPersonaTranslator", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c); slashes = 0;
        }
        result.Append('\\', slashes * 2); result.Append('"');
        return result.ToString();
    }
}
