using System;
using System.IO;
using System.Text;

namespace RomesteadSaveInspector.WinUI;

internal static class AppLogger
{
    private static readonly object Sync = new();
    private static string _logFile = "";
    private static bool _showDebug;

    public static string LogFile => _logFile;
    public static bool ShowDebug => _showDebug;

    public static event Action<string>? LogWritten;

    public static void Initialize(string appRoot, bool showDebug)
    {
        _showDebug = showDebug;
        var logDir = Path.Combine(appRoot, "logs");
        Directory.CreateDirectory(logDir);
        _logFile = Path.Combine(logDir, DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log");
        File.WriteAllText(_logFile, "Romestead Save Inspector GUI log" + Environment.NewLine, Encoding.UTF8);
        File.WriteAllText(Path.Combine(logDir, "latest.log"), "Romestead Save Inspector GUI latest log" + Environment.NewLine, Encoding.UTF8);
        Info($"Log file: {_logFile}");
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? ex = null)
    {
        var text = ex == null ? message : message + Environment.NewLine + ex;
        Write("ERROR", text);
    }

    private static void Write(string level, string message)
    {
        if (string.IsNullOrWhiteSpace(_logFile)) return;
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";
        lock (Sync)
        {
            File.AppendAllText(_logFile, line + Environment.NewLine, Encoding.UTF8);
            var latest = Path.Combine(Path.GetDirectoryName(_logFile)!, "latest.log");
            File.AppendAllText(latest, line + Environment.NewLine, Encoding.UTF8);
        }

        if (_showDebug)
        {
            try { LogWritten?.Invoke(line); } catch { /* ignore UI log sink errors */ }
        }
    }
}
