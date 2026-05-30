using System;
using System.IO;
using System.Diagnostics;

namespace RomesteadSaveInspector.WinUI;

internal static class AppPaths
{
    public const string RootEnvironmentVariable = "ROMESTEAD_SAVE_INSPECTOR_ROOT";

    public static string AppRoot { get; } = ResolveAppRoot();

    public static string LibDir => Path.Combine(AppRoot, "lib");
    public static string InputDir => Path.Combine(AppRoot, "input");
    public static string OutputDir => Path.Combine(AppRoot, "output");
    public static string BackupDir => Path.Combine(AppRoot, "backup");
    public static string LogsDir => Path.Combine(AppRoot, "logs");
    public static string DataDir => Path.Combine(AppRoot, "data");

    public static void EnsureStandardDirectories()
    {
        Directory.CreateDirectory(AppRoot);
        Directory.CreateDirectory(LibDir);
        Directory.CreateDirectory(InputDir);
        Directory.CreateDirectory(OutputDir);
        Directory.CreateDirectory(BackupDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(DataDir);
    }

    private static string ResolveAppRoot()
    {
        // Source/debug runs set this variable from run_gui_debug.bat so the app uses the
        // project folder. Published EXE runs should not set it; they use the EXE folder.
        var env = Environment.GetEnvironmentVariable(RootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(env))
        {
            return Path.GetFullPath(env.Trim().Trim('\"'));
        }

        // Published WinUI / single-file apps can have AppContext.BaseDirectory point to an
        // extraction/runtime directory instead of the location users see. The process path
        // is the real executable path, so lib/input/output/backup/logs stay beside the EXE.
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            processPath = Process.GetCurrentProcess().MainModule?.FileName;
        }

        if (!string.IsNullOrWhiteSpace(processPath))
        {
            var exeDir = Path.GetDirectoryName(processPath);
            if (!string.IsNullOrWhiteSpace(exeDir))
            {
                return Path.GetFullPath(exeDir);
            }
        }

        // Last-resort fallback for unusual hosts.
        return Path.GetFullPath(AppContext.BaseDirectory);
    }
}
