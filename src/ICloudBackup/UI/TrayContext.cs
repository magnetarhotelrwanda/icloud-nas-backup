using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ICloudBackup.Core;
using Microsoft.Win32;

namespace ICloudBackup.UI;

public sealed class TrayContext : ApplicationContext
{
    private readonly BackupEngine _engine;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _integrityItem;
    private readonly Dictionary<string, Icon> _icons;
    private MainForm? _form;
    private bool _exiting;

    public TrayContext(BackupEngine engine, bool showWindow, bool firstRun)
    {
        _engine = engine;
        _icons = new()
        {
            ["idle"] = Icons.Cloud(Color.FromArgb(46, 160, 67)),
            ["busy"] = Icons.Cloud(Color.FromArgb(0, 120, 212)),
            ["paused"] = Icons.Cloud(Color.FromArgb(150, 150, 150)),
            ["problem"] = Icons.Cloud(Color.FromArgb(209, 52, 56)),
        };

        _pauseItem = new ToolStripMenuItem("Pause backups", null, (_, _) => TogglePause());
        _integrityItem = new ToolStripMenuItem("Run integrity check", null, (_, _) => ToggleIntegrity());
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("Open iCloud Backup", null, (_, _) => ShowMain()) { Font = new Font(menu.Font, FontStyle.Bold) });
        menu.Items.Add(new ToolStripMenuItem("Sync now", null, (_, _) => _engine.RequestSync("manual")));
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_integrityItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, async (_, _) => await ExitAsync()));
        menu.Opening += (_, _) =>
        {
            _pauseItem.Text = _engine.UserPaused ? "Resume backups" : "Pause backups";
            _integrityItem.Text = _engine.Integrity.Running ? "Cancel integrity check" : "Run integrity check";
        };

        _tray = new NotifyIcon
        {
            Icon = _icons["idle"],
            Text = "iCloud Backup",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowMain();

        _timer = new System.Windows.Forms.Timer { Interval = 2000 };
        _timer.Tick += (_, _) => UpdateTray();
        _timer.Start();

        if (showWindow) ShowMain(firstRun ? ICloudBackup.UI.MainForm.SettingsTab : null, firstRun);
    }

    public BackupEngine Engine => _engine;

    public void AnnounceStart()
    {
        if (_engine.Config.NotifyStartup)
            _engine.Notifier.Send($":arrow_forward: **iCloud Backup** started on {Environment.MachineName} - backing up " +
                                  $"{string.Join(", ", _engine.Config.Jobs.Where(j => j.Enabled).Select(j => j.Name))}.");
    }

    public void TogglePause()
    {
        _engine.UserPaused = !_engine.UserPaused;
        Log.Info(_engine.UserPaused ? "Backups paused by user" : "Backups resumed by user");
        UpdateTray();
    }

    public void ToggleIntegrity()
    {
        if (_engine.Integrity.Running) _engine.CancelIntegrityCheck();
        else if (!_engine.StartIntegrityCheck("manual"))
            MessageBox.Show("The backup engine isn't running yet - save your settings first.", "iCloud Backup");
    }

    public void ShowMain(int? tab = null, bool firstRun = false)
    {
        if (_form == null || _form.IsDisposed) _form = new MainForm(this, firstRun);
        if (tab != null) _form.SelectTab(tab.Value);
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    private void UpdateTray()
    {
        string key;
        var failing = false;
        try
        {
            failing = _engine.Config.Jobs.Any(j => j.Enabled && !_engine.IsNasOnline(j));
        }
        catch { }

        if (!_engine.Running || _engine.UserPaused) key = "paused";
        else if (failing) key = "problem";
        else if (_engine.ActiveTransfers.Count > 0 || _engine.Integrity.Running) key = "busy";
        else key = "idle";

        _tray.Icon = _icons[key];
        var text = !_engine.Running ? "iCloud Backup - not started (open settings)" : $"iCloud Backup - {_engine.Activity}";
        if (_engine.QueueLength > 0) text += $" - {_engine.QueueLength} queued";
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    public async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _timer.Stop();
        _form?.Hide();
        _tray.Text = "iCloud Backup - stopping...";
        if (_engine.Running && _engine.Config.NotifyStartup)
            _engine.Notifier.Send($":stop_button: **iCloud Backup** stopped on {Environment.MachineName}.");
        await _engine.StopAsync();
        await _engine.Notifier.FlushAsync();
        _tray.Visible = false;
        _form?.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _tray.Dispose();
            foreach (var i in _icons.Values) i.Dispose();
        }
        base.Dispose(disposing);
    }
}

public static class Icons
{
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>Draws a simple cloud in the given colour so the tray icon shows status at a glance.</summary>
    public static Icon Cloud(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var path = new GraphicsPath();
            path.AddEllipse(2, 13, 14, 14);
            path.AddEllipse(8, 5, 17, 17);
            path.AddEllipse(17, 11, 13, 13);
            path.AddRectangle(new Rectangle(9, 17, 15, 10));
            using var region = new Region(path);
            using var brush = new SolidBrush(color);
            g.FillRegion(brush, region);
            using var white = new SolidBrush(Color.White);
            // little down arrow = "backing up"
            g.FillRectangle(white, 14, 11, 4, 8);
            g.FillPolygon(white, [new Point(10, 18), new Point(22, 18), new Point(16, 24)]);
        }
        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }
}

public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ICloudBackup";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --minimized");
            else key.DeleteValue(ValueName, false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not update start-with-Windows setting: {ex.Message}");
        }
    }
}
