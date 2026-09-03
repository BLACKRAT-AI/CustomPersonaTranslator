using System;
using System.IO;

namespace CPT.Core.Diagnostics;

// Tiny append-only diagnostic log so we can debug runtime issues post-mortem.
// File: %LOCALAPPDATA%\CustomPersonaTranslator\cpt.log
public static class CptLog
{
    private static readonly object _lock = new();
    private static readonly string _path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CustomPersonaTranslator", "cpt.log");

    public static string FilePath => _path;

    private static readonly System.Text.Encoding _enc =
        new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static void Write(string line)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            lock (_lock)
            {
                // File.AppendAllText(path, str) on Windows defaults to UTF-8
                // *only on net5+*, but historically writes a BOM on first
                // create which then mojibakes em-dashes in editors that
                // sniff system codepage. Force UTF-8 without BOM explicitly
                // so log lines like "â€"" stop appearing in cpt.log.
                File.AppendAllText(_path,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {line}{Environment.NewLine}",
                    _enc);
            }
        }
        catch { }
    }
}
