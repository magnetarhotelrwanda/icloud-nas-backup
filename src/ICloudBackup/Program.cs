using ICloudBackup.Core;
using ICloudBackup.UI;

namespace ICloudBackup;

internal static class Program
{
    public static string Version => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\ICloudBackup.SingleInstance", out var firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("iCloud Backup is already running - look for the cloud icon in the system tray.", "iCloud Backup",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        AppPaths.Ensure();

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("UI error", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error($"Fatal error: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Background task error", e.Exception);
            e.SetObserved();
        };

        var firstRun = !ConfigStore.Exists;
        var cfg = ConfigStore.Load();
        Log.Info($"iCloud Backup {Version} starting (PID {Environment.ProcessId}){(firstRun ? " - first run" : "")}");

        using var db = new StateDb(AppPaths.DbPath);
        using var engine = new BackupEngine(cfg, db);

        // On first run nothing starts until the settings have been reviewed and saved.
        var showWindow = firstRun || (!cfg.StartMinimized && !args.Contains("--minimized"));
        using var tray = new TrayContext(engine, showWindow, firstRun);
        if (!firstRun)
        {
            engine.Start();
            tray.AnnounceStart();
        }

        Application.Run(tray);
        Log.Info("iCloud Backup exited");
    }
}
