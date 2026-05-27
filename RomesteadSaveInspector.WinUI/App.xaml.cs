using Microsoft.UI.Xaml;
using System;

namespace RomesteadSaveInspector.WinUI;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppPaths.EnsureStandardDirectories();
        AppLogger.Initialize(AppPaths.AppRoot, showDebug: true);
        AppLogger.Info($"WinUI application launched. App root: {AppPaths.AppRoot}");
        AppLogger.Info("GUI debug panel is always enabled. config.ini is not used.");
        m_window = new MainWindow();
        m_window.Activate();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        AppLogger.Error("Unhandled WinUI exception.", e.Exception);
    }

    private Window? m_window;
}
