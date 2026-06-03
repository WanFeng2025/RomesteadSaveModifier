using System;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Threading;

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
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Environment.ProcessId.ToString();
        _logFile = Path.Combine(logDir, stamp + ".log");
        SafeWriteAllText(_logFile, "Romestead Save Inspector GUI log" + Environment.NewLine);
        SafeWriteAllText(Path.Combine(logDir, "latest.log"), "Romestead Save Inspector GUI latest log" + Environment.NewLine);
        Info($"Log file: {_logFile}");
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

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
            SafeAppendAllText(_logFile, line + Environment.NewLine);
            var latest = Path.Combine(Path.GetDirectoryName(_logFile)!, "latest.log");
            SafeAppendAllText(latest, line + Environment.NewLine);
        }

        if (_showDebug)
        {
            try { LogWritten?.Invoke(line); } catch { /* ignore UI log sink errors */ }
        }
    }
    private static void SafeWriteAllText(string path, string text)
    {
        SafeFileIo(path, stream =>
        {
            stream.SetLength(0);
            var bytes = Encoding.UTF8.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        });
    }

    private static void SafeAppendAllText(string path, string text)
    {
        SafeFileIo(path, stream =>
        {
            stream.Seek(0, SeekOrigin.End);
            var bytes = Encoding.UTF8.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        });
    }

    private static void SafeFileIo(string path, Action<FileStream> action)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Exception? last = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                action(stream);
                return;
            }
            catch (IOException ex)
            {
                last = ex;
                Thread.Sleep(25 + attempt * 25);
            }
        }

        try { Debug.WriteLine(last?.ToString()); } catch { }
    }

}
