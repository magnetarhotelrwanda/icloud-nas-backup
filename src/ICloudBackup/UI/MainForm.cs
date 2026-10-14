using System.Diagnostics;
using ICloudBackup.Core;

namespace ICloudBackup.UI;

public sealed class MainForm : Form
{
    public const int StatusTab = 0, ActivityTab = 1, FailedTab = 2, SettingsTab = 3;

    private readonly TrayContext _tray;
    private BackupEngine Engine => _tray.Engine;
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    // Status tab
    private readonly Label _lblState = new() { AutoSize = true, Font = new Font("Segoe UI", 13F, FontStyle.Bold) };
    private readonly Label _lblDetails = new() { AutoSize = true };
    private readonly Button _btnSync = new() { Text = "Sync now", AutoSize = true };
    private readonly Button _btnPause = new() { Text = "Pause", AutoSize = true };
    private readonly Button _btnIntegrity = new() { Text = "Run integrity check", AutoSize = true };
    private readonly ProgressBar _integrityBar = new() { Width = 300, Height = 16, Visible = false };
    private readonly Label _lblIntegrity = new() { AutoSize = true };
    private readonly ListView _jobsView = MakeList(("Job", 140), ("NAS", 80), ("Backed up", 90), ("Failing", 70), ("iCloud folder", 260), ("NAS folder", 260));
    private readonly ListView _activeView = MakeList(("Job", 120), ("File", 420), ("Stage", 200), ("Progress", 140));

    // Activity tab
    private readonly ListView _logView = MakeList(("Time", 130), ("Level", 60), ("Message", 900));
    private readonly CheckBox _autoScroll = new() { Text = "Auto-scroll", Checked = true, AutoSize = true };
    private long _logSeq;

    // Failed tab
    private readonly ListView _failedView = MakeList(("Job", 120), ("File", 380), ("Attempts", 70), ("Next retry", 130), ("Last error", 500));
    private DateTime _failedRefreshed = DateTime.MinValue;

    // Settings tab
    private List<BackupJob> _editJobs = [];
    private readonly ListView _jobsEdit = MakeList(("Name", 140), ("Enabled", 60), ("Keep local", 75), ("iCloud folder", 300), ("NAS folder", 300));
    private readonly NumericUpDown _threads = Num(1, 32);
    private readonly NumericUpDown _downloadThreads = Num(1, 32);
    private readonly NumericUpDown _downloadRetryMax = Num(1, 1440);
    private readonly NumericUpDown _hydrationTimeout = Num(1, 240);
    private readonly NumericUpDown _settle = Num(1, 600);
    private readonly CheckBox _keepVersions = Check("Keep the previous version on the NAS when a file changes (in a .versions folder)");
    private readonly TextBox _versionsFolder = new() { Width = 160 };
    private readonly NumericUpDown _retryMax = Num(1, 1440);
    private readonly NumericUpDown _retryAlert = Num(1, 100);
    private readonly NumericUpDown _syncInterval = Num(5, 1440);
    private readonly NumericUpDown _restartInterval = Num(0, 1440);
    private readonly CheckBox _restartOnlyStuck = Check("Only restart when files are waiting on an iCloud download");
    private readonly NumericUpDown _icloudSettle = Num(0, 600);
    private readonly CheckBox _watchdog = Check("Relaunch iCloud automatically if it stops running");
    private readonly TextBox _processNames = new() { Width = 300 };
    private readonly NumericUpDown _integrityDays = Num(0, 90);
    private readonly NumericUpDown _integrityHour = Num(0, 23);
    private readonly CheckBox _rehashSource = Check("Re-hash the iCloud copy too (slower, but checks both sides byte for byte)");
    private readonly NumericUpDown _lowDisk = Num(0, 100000);
    private readonly TextBox _webhook = new() { Width = 460, UseSystemPasswordChar = true };
    private readonly CheckBox _showWebhook = Check("Show");
    private readonly NumericUpDown _batchSeconds = Num(10, 3600);
    private readonly CheckBox _nCopies = Check("Files copied (batched)");
    private readonly CheckBox _nFailures = Check("Copy failures (batched)");
    private readonly CheckBox _nRetryAlerts = Check("A file keeps failing (after the alert threshold)");
    private readonly CheckBox _nICloud = Check("iCloud stopped / relaunched / folder missing");
    private readonly CheckBox _nNas = Check("NAS offline / back online");
    private readonly CheckBox _nIntegrity = Check("Integrity check started / results");
    private readonly CheckBox _nSync = Check("Every hourly sync (even when nothing changed)");
    private readonly CheckBox _nStartup = Check("App started / stopped");
    private readonly CheckBox _nDaily = Check("Daily summary");
    private readonly NumericUpDown _dailyHour = Num(0, 23);
    private readonly CheckBox _startWithWindows = Check("Start iCloud Backup when I sign in to Windows");
    private readonly CheckBox _startMinimized = Check("Start minimised to the system tray");
    private readonly Button _btnSave = new() { Text = "Save and apply", AutoSize = true, Font = new Font("Segoe UI", 9.5F, FontStyle.Bold) };
    private readonly Label _lblSettingsStatus = new() { AutoSize = true, ForeColor = Color.DimGray };

    public MainForm(TrayContext tray, bool firstRun)
    {
        _tray = tray;
        Text = $"iCloud Backup {Program.Version}";
        Icon = Icons.Cloud(Color.FromArgb(0, 120, 212));
        Size = new Size(1150, 760);
        MinimumSize = new Size(800, 500);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9F);

        _tabs.TabPages.Add(BuildStatusTab());
        _tabs.TabPages.Add(BuildActivityTab());
        _tabs.TabPages.Add(BuildFailedTab());
        _tabs.TabPages.Add(BuildSettingsTab(firstRun));
        Controls.Add(_tabs);

        LoadSettings(Engine.Config);

        _timer.Tick += (_, _) => RefreshUi();
        _timer.Start();
        RefreshUi();
    }

    public void SelectTab(int index) => _tabs.SelectedIndex = index;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // The close button just hides the window - the app keeps running in the tray.
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }

    // ------------------------------------------------------------------ helpers

    private static ListView MakeList(params (string Title, int Width)[] cols)
    {
        var lv = new ListView
        {
            View = View.Details,
            FullRowSelect = true,
            GridLines = true,
            Dock = DockStyle.Fill,
            HideSelection = false,
        };
        foreach (var (title, width) in cols) lv.Columns.Add(title, width);
        return lv;
    }

    private static NumericUpDown Num(int min, int max) => new() { Minimum = min, Maximum = max, Width = 80 };
    private static CheckBox Check(string text) => new() { Text = text, AutoSize = true };

    private static FlowLayoutPanel ButtonRow(params Control[] controls)
    {
        var p = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 4, 0, 4) };
        p.Controls.AddRange(controls);
        return p;
    }

    private static Button MakeButton(string text, Action onClick)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += (_, _) => onClick();
        return b;
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "iCloud Backup"); }
    }

    private static string When(DateTime? t) =>
        t == null ? "never" :
        t.Value.Date == DateTime.Today ? t.Value.ToString("HH:mm:ss") : t.Value.ToString("ddd d MMM HH:mm");

    // ------------------------------------------------------------------ Status tab

    private TabPage BuildStatusTab()
    {
        _btnSync.Click += (_, _) => { Engine.RequestSync("manual"); Log.Info("Manual sync requested"); };
        _btnPause.Click += (_, _) => _tray.TogglePause();
        _btnIntegrity.Click += (_, _) => _tray.ToggleIntegrity();

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));

        layout.Controls.Add(_lblState);
        layout.Controls.Add(_lblDetails);
        layout.Controls.Add(ButtonRow(_btnSync, _btnPause, _btnIntegrity,
            MakeButton("Retry all failed", () =>
            {
                var n = Engine.RetryAll();
                Log.Info($"Retrying {n} failed file(s) now");
            }),
            MakeButton("Send summary to Discord", () => Engine.Notifier.Send(Engine.BuildDailySummary())),
            MakeButton("Open logs", () => OpenFolder(AppPaths.LogDir)),
            MakeButton("Open integrity reports", () => OpenFolder(AppPaths.ReportDir))));
        layout.Controls.Add(ButtonRow(_integrityBar, _lblIntegrity));
        layout.Controls.Add(new Label { Text = "Backup jobs", AutoSize = true, Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), Margin = new Padding(3, 8, 3, 3) });
        layout.Controls.Add(_jobsView);
        layout.Controls.Add(new Label { Text = "Working on now", AutoSize = true, Font = new Font("Segoe UI", 9.5F, FontStyle.Bold), Margin = new Padding(3, 8, 3, 3) });
        layout.Controls.Add(_activeView);

        var page = new TabPage("Status");
        page.Controls.Add(layout);
        return page;
    }

    private void RefreshStatus()
    {
        var e = Engine;
        _lblState.Text = e.Running ? e.Activity : "Not started - review the Settings tab and click \"Save and apply\"";
        _lblState.ForeColor = !e.Running || e.UserPaused ? Color.DarkOrange : Color.FromArgb(0, 90, 160);

        var lastIntegrity = e.Db.GetMeta("last_integrity");
        var integrityText = DateTime.TryParse(lastIntegrity, out var li) ? $"{When(li)} - {e.Db.GetMeta("last_integrity_result")}" : "never";
        _lblDetails.Text =
            $"Queue: {e.QueueLength:N0} to copy, {e.DownloadQueueLength:N0} waiting on iCloud download     This session: {e.SessionCopied:N0} copied ({FileOps.FormatBytes(e.SessionBytes)}), " +
            $"{e.SessionVerified:N0} verified already on NAS, {e.SessionFailed:N0} failed attempts\n" +
            $"Last rescan: {When(e.LastSync)}     Next rescan: {(e.NextSync == DateTime.MaxValue ? "running now" : When(e.NextSync))}     " +
            $"Next iCloud restart: {(e.NextICloudRestart == DateTime.MaxValue ? (e.Config.ICloudRestartIntervalMinutes > 0 ? "in progress" : "off") : When(e.NextICloudRestart))}\n" +
            $"Last integrity check: {integrityText}";

        _btnPause.Text = e.UserPaused ? "Resume" : "Pause";
        var ip = e.Integrity;
        _btnIntegrity.Text = ip.Running ? "Cancel integrity check" : "Run integrity check";
        _integrityBar.Visible = ip.Running;
        if (ip.Running)
        {
            _integrityBar.Maximum = Math.Max(1, ip.Total);
            _integrityBar.Value = Math.Min(_integrityBar.Maximum, ip.Done);
            _lblIntegrity.Text = $"Integrity check: {ip.Phase} - {ip.Done:N0}/{ip.Total:N0} - {ip.Ok:N0} OK, {ip.Mismatched} mismatched, {ip.Missing} missing, {ip.Errors} errors";
        }
        else _lblIntegrity.Text = "";

        _jobsView.BeginUpdate();
        _jobsView.Items.Clear();
        foreach (var job in e.Config.Jobs)
        {
            var c = e.Db.Counts(job.Id);
            var item = new ListViewItem(job.Name);
            item.SubItems.Add(!job.Enabled ? "disabled" : e.IsNasOnline(job) ? "online" : "OFFLINE");
            item.SubItems.Add(c.Ok.ToString("N0"));
            item.SubItems.Add(c.Failed.ToString("N0"));
            item.SubItems.Add(job.SourcePath);
            item.SubItems.Add(job.DestinationPath);
            if (job.Enabled && !e.IsNasOnline(job)) item.ForeColor = Color.Firebrick;
            else if (c.Failed > 0) item.ForeColor = Color.DarkOrange;
            _jobsView.Items.Add(item);
        }
        _jobsView.EndUpdate();

        _activeView.BeginUpdate();
        _activeView.Items.Clear();
        foreach (var a in e.ActiveTransfers)
        {
            var item = new ListViewItem(a.Job);
            item.SubItems.Add(a.Rel);
            item.SubItems.Add(a.Phase);
            var done = Interlocked.Read(ref a.Done);
            item.SubItems.Add(a.Size > 0 && done > 0 ? $"{done * 100 / a.Size}% of {FileOps.FormatBytes(a.Size)}" : FileOps.FormatBytes(a.Size));
            _activeView.Items.Add(item);
        }
        _activeView.EndUpdate();
    }

    // ------------------------------------------------------------------ Activity tab

    private TabPage BuildActivityTab()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(6) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(ButtonRow(_autoScroll,
            MakeButton("Clear view", () => _logView.Items.Clear()),
            MakeButton("Open log folder", () => OpenFolder(AppPaths.LogDir))));
        layout.Controls.Add(_logView);
        var page = new TabPage("Activity");
        page.Controls.Add(layout);
        return page;
    }

    private void RefreshLog()
    {
        var entries = Log.GetSince(_logSeq);
        if (entries.Count == 0) return;
        _logSeq = entries[^1].Seq;
        _logView.BeginUpdate();
        foreach (var en in entries)
        {
            var item = new ListViewItem(en.Time.ToString("yyyy-MM-dd HH:mm:ss"));
            item.SubItems.Add(en.Level.ToString().ToUpperInvariant());
            item.SubItems.Add(en.Message);
            item.ForeColor = en.Level switch
            {
                LogLevel.Error => Color.Firebrick,
                LogLevel.Warn => Color.DarkOrange,
                LogLevel.Ok => Color.ForestGreen,
                _ => SystemColors.WindowText,
            };
            _logView.Items.Add(item);
        }
        while (_logView.Items.Count > 2000) _logView.Items.RemoveAt(0);
        _logView.EndUpdate();
        if (_autoScroll.Checked && _logView.Items.Count > 0) _logView.EnsureVisible(_logView.Items.Count - 1);
    }

    // ------------------------------------------------------------------ Failed tab

    private TabPage BuildFailedTab()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(6) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "Files that couldn't be copied. Each one is retried automatically with increasing gaps (1, 2, 4, 8 … minutes, up to the maximum in Settings).",
        });
        layout.Controls.Add(ButtonRow(
            MakeButton("Refresh", RefreshFailed),
            MakeButton("Retry selected now", () =>
            {
                foreach (ListViewItem item in _failedView.SelectedItems)
                    if (item.Tag is FileRecord rec) Engine.RetryNow(rec);
                RefreshFailed();
            }),
            MakeButton("Retry all now", () => { Engine.RetryAll(); RefreshFailed(); })));
        layout.Controls.Add(_failedView);
        var page = new TabPage("Failed files");
        page.Controls.Add(layout);
        return page;
    }

    private void RefreshFailed()
    {
        _failedRefreshed = DateTime.Now;
        var names = Engine.Config.Jobs.ToDictionary(j => j.Id, j => j.Name);
        var failed = Engine.Db.GetFailed();
        _failedView.BeginUpdate();
        _failedView.Items.Clear();
        foreach (var rec in failed.Take(5000))
        {
            var item = new ListViewItem(names.GetValueOrDefault(rec.Job, "(removed job)")) { Tag = rec };
            item.SubItems.Add(rec.Rel);
            item.SubItems.Add(rec.Attempts.ToString());
            item.SubItems.Add(rec.NextAttempt is { } n ? When(DateTimeOffset.FromUnixTimeMilliseconds(n).LocalDateTime) : "next sync");
            item.SubItems.Add(rec.LastError ?? "");
            _failedView.Items.Add(item);
        }
        _failedView.EndUpdate();
        _tabs.TabPages[FailedTab].Text = failed.Count > 0 ? $"Failed files ({failed.Count:N0})" : "Failed files";
    }

    // ------------------------------------------------------------------ Settings tab

    private TabPage BuildSettingsTab(bool firstRun)
    {
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(10),
        };

        if (firstRun)
        {
            flow.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(1000, 0),
                ForeColor = Color.FromArgb(0, 90, 160),
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                Text = "Welcome! Edit the backup jobs below - the iCloud folders are pre-filled with the usual locations, " +
                       "but set the NAS folders to your own share. Paste your Discord webhook, then click \"Save and apply\" at the bottom to start backing up.",
                Margin = new Padding(3, 3, 3, 10),
            });
        }

        // Jobs
        _jobsEdit.Dock = DockStyle.None;
        _jobsEdit.Size = new Size(1000, 130);
        _jobsEdit.DoubleClick += (_, _) => EditJob();
        var jobsBox = Group("Backup jobs (iCloud folder → NAS folder)", out var jt);
        jt.Controls.Add(_jobsEdit);
        jt.SetColumnSpan(_jobsEdit, 2);
        var jobButtons = ButtonRow(MakeButton("Add...", AddJob), MakeButton("Edit...", EditJob), MakeButton("Remove", RemoveJob));
        jt.Controls.Add(jobButtons);
        jt.SetColumnSpan(jobButtons, 2);
        flow.Controls.Add(jobsBox);

        var copyBox = Group("Copying", out var ct);
        Row(ct, "Files copied to the NAS at the same time", _threads);
        Row(ct, "Files downloaded from iCloud at the same time", _downloadThreads);
        Row(ct, "Give up waiting for an iCloud download after (minutes)", _hydrationTimeout);
        Row(ct, "Wait for a changed file to settle before copying (seconds)", _settle);
        Row(ct, "", _keepVersions);
        Row(ct, "Versions folder name (on the NAS)", _versionsFolder);
        flow.Controls.Add(copyBox);

        var retryBox = Group("Retries", out var rt);
        Row(rt, "Longest gap between retries (minutes)", _retryMax);
        Row(rt, "Longest gap between retries for files waiting on iCloud (minutes)", _downloadRetryMax);
        Row(rt, "Send an alert after this many failed attempts", _retryAlert);
        flow.Controls.Add(retryBox);

        var icloudBox = Group("iCloud", out var it);
        Row(it, "Full rescan of iCloud and NAS every (minutes)", _syncInterval);
        Row(it, "Restart iCloud every (minutes, 0 = never) - waits for active downloads/copies to finish first", _restartInterval);
        Row(it, "", _restartOnlyStuck);
        Row(it, "After restarting iCloud, wait (seconds)", _icloudSettle);
        Row(it, "", _watchdog);
        Row(it, "iCloud process names (comma separated)", _processNames);
        flow.Controls.Add(icloudBox);

        var integrityBox = Group("Integrity check (hashes every file in iCloud and on the NAS and compares them)", out var gt);
        Row(gt, "Run automatically every (days, 0 = never)", _integrityDays);
        Row(gt, "Start at hour (0-23)", _integrityHour);
        Row(gt, "", _rehashSource);
        flow.Controls.Add(integrityBox);

        var healthBox = Group("Health", out var ht);
        Row(ht, "Warn when free space on the iCloud drive drops below (GB, 0 = off)", _lowDisk);
        flow.Controls.Add(healthBox);

        var discordBox = Group("Discord notifications", out var dt);
        _showWebhook.CheckedChanged += (_, _) => _webhook.UseSystemPasswordChar = !_showWebhook.Checked;
        var testBtn = MakeButton("Send test", async () => await TestWebhookAsync());
        Row(dt, "Webhook URL", ButtonRow(_webhook, _showWebhook, testBtn));
        Row(dt, "Group copy/failure messages over (seconds)", _batchSeconds);
        var notifyFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        notifyFlow.Controls.AddRange([_nCopies, _nFailures, _nRetryAlerts, _nICloud, _nNas, _nIntegrity, _nSync, _nStartup, _nDaily]);
        Row(dt, "Tell me about", notifyFlow);
        Row(dt, "Send the daily summary at hour (0-23)", _dailyHour);
        flow.Controls.Add(discordBox);

        var appBox = Group("App", out var at);
        Row(at, "", _startWithWindows);
        Row(at, "", _startMinimized);
        flow.Controls.Add(appBox);

        _btnSave.Click += async (_, _) => await SaveAsync();
        flow.Controls.Add(ButtonRow(_btnSave, MakeButton("Undo changes", () => LoadSettings(Engine.Config)), _lblSettingsStatus));

        var page = new TabPage("Settings");
        page.Controls.Add(flow);
        return page;
    }

    private static GroupBox Group(string title, out TableLayoutPanel table)
    {
        var box = new GroupBox { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, MinimumSize = new Size(1020, 0), Padding = new Padding(8) };
        table = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        box.Controls.Add(table);
        return box;
    }

    private static void Row(TableLayoutPanel t, string label, Control control)
    {
        t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 6, 12, 3) });
        t.Controls.Add(control);
    }

    private void RefreshJobsEdit()
    {
        _jobsEdit.BeginUpdate();
        _jobsEdit.Items.Clear();
        foreach (var j in _editJobs)
        {
            var item = new ListViewItem(j.Name) { Tag = j };
            item.SubItems.Add(j.Enabled ? "Yes" : "No");
            item.SubItems.Add(j.KeepFilesLocal ? "Yes" : "No");
            item.SubItems.Add(j.SourcePath);
            item.SubItems.Add(j.DestinationPath);
            _jobsEdit.Items.Add(item);
        }
        _jobsEdit.EndUpdate();
    }

    private void AddJob()
    {
        var job = new BackupJob();
        using var dlg = new JobEditForm(job);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _editJobs.Add(job);
            RefreshJobsEdit();
        }
    }

    private void EditJob()
    {
        if (_jobsEdit.SelectedItems.Count == 0 || _jobsEdit.SelectedItems[0].Tag is not BackupJob job) return;
        var copy = job.Clone();
        using var dlg = new JobEditForm(copy);
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _editJobs[_editJobs.IndexOf(job)] = copy;
            RefreshJobsEdit();
        }
    }

    private void RemoveJob()
    {
        if (_jobsEdit.SelectedItems.Count == 0 || _jobsEdit.SelectedItems[0].Tag is not BackupJob job) return;
        if (MessageBox.Show(this, $"Remove the job \"{job.Name}\"? Files already on the NAS are not touched.", "iCloud Backup",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _editJobs.Remove(job);
        RefreshJobsEdit();
    }

    private void LoadSettings(AppConfig c)
    {
        _editJobs = c.Jobs.Select(j => j.Clone()).ToList();
        RefreshJobsEdit();
        _threads.Value = Math.Clamp(c.CopyThreads, 1, 32);
        _downloadThreads.Value = Math.Clamp(c.DownloadThreads, 1, 32);
        _downloadRetryMax.Value = Math.Clamp(c.DownloadRetryMaxMinutes, 1, 1440);
        _hydrationTimeout.Value = Math.Clamp(c.HydrationTimeoutMinutes, 1, 240);
        _settle.Value = Math.Clamp(c.FileSettleSeconds, 1, 600);
        _keepVersions.Checked = c.KeepVersions;
        _versionsFolder.Text = c.VersionsFolderName;
        _retryMax.Value = Math.Clamp(c.RetryMaxDelayMinutes, 1, 1440);
        _retryAlert.Value = Math.Clamp(c.RetryAlertAfterAttempts, 1, 100);
        _syncInterval.Value = Math.Clamp(c.SyncIntervalMinutes, 5, 1440);
        _restartInterval.Value = Math.Clamp(c.ICloudRestartIntervalMinutes, 0, 1440);
        _restartOnlyStuck.Checked = c.ICloudRestartOnlyWhenStuck;
        _icloudSettle.Value = Math.Clamp(c.ICloudSettleSeconds, 0, 600);
        _watchdog.Checked = c.ICloudWatchdog;
        _processNames.Text = string.Join(", ", c.ICloudProcessNames);
        _integrityDays.Value = Math.Clamp(c.IntegrityCheckIntervalDays, 0, 90);
        _integrityHour.Value = Math.Clamp(c.IntegrityCheckHour, 0, 23);
        _rehashSource.Checked = c.IntegrityRehashSource;
        _lowDisk.Value = Math.Clamp(c.LowDiskSpaceGb, 0, 100000);
        _webhook.Text = c.GetWebhookUrl() ?? "";
        _batchSeconds.Value = Math.Clamp(c.DiscordBatchSeconds, 10, 3600);
        _nCopies.Checked = c.NotifyCopies;
        _nFailures.Checked = c.NotifyFailures;
        _nRetryAlerts.Checked = c.NotifyRetryAlerts;
        _nICloud.Checked = c.NotifyICloudEvents;
        _nNas.Checked = c.NotifyNasEvents;
        _nIntegrity.Checked = c.NotifyIntegrity;
        _nSync.Checked = c.NotifySyncSummary;
        _nStartup.Checked = c.NotifyStartup;
        _nDaily.Checked = c.NotifyDailySummary;
        _dailyHour.Value = Math.Clamp(c.DailySummaryHour, 0, 23);
        _startWithWindows.Checked = c.StartWithWindows;
        _startMinimized.Checked = c.StartMinimized;
        _lblSettingsStatus.Text = "";
    }

    private AppConfig ReadSettings()
    {
        var c = Engine.Config.Clone();
        c.Jobs = _editJobs.Select(j => j.Clone()).ToList();
        c.CopyThreads = (int)_threads.Value;
        c.DownloadThreads = (int)_downloadThreads.Value;
        c.DownloadRetryMaxMinutes = (int)_downloadRetryMax.Value;
        c.HydrationTimeoutMinutes = (int)_hydrationTimeout.Value;
        c.FileSettleSeconds = (int)_settle.Value;
        c.KeepVersions = _keepVersions.Checked;
        c.VersionsFolderName = string.IsNullOrWhiteSpace(_versionsFolder.Text) ? ".versions" : _versionsFolder.Text.Trim();
        c.RetryMaxDelayMinutes = (int)_retryMax.Value;
        c.RetryAlertAfterAttempts = (int)_retryAlert.Value;
        c.SyncIntervalMinutes = (int)_syncInterval.Value;
        c.ICloudRestartIntervalMinutes = (int)_restartInterval.Value;
        c.ICloudRestartOnlyWhenStuck = _restartOnlyStuck.Checked;
        c.ICloudSettleSeconds = (int)_icloudSettle.Value;
        c.ICloudWatchdog = _watchdog.Checked;
        c.ICloudProcessNames = _processNames.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n).ToList();
        c.IntegrityCheckIntervalDays = (int)_integrityDays.Value;
        c.IntegrityCheckHour = (int)_integrityHour.Value;
        c.IntegrityRehashSource = _rehashSource.Checked;
        c.LowDiskSpaceGb = (int)_lowDisk.Value;
        c.SetWebhookUrl(_webhook.Text);
        c.DiscordBatchSeconds = (int)_batchSeconds.Value;
        c.NotifyCopies = _nCopies.Checked;
        c.NotifyFailures = _nFailures.Checked;
        c.NotifyRetryAlerts = _nRetryAlerts.Checked;
        c.NotifyICloudEvents = _nICloud.Checked;
        c.NotifyNasEvents = _nNas.Checked;
        c.NotifyIntegrity = _nIntegrity.Checked;
        c.NotifySyncSummary = _nSync.Checked;
        c.NotifyStartup = _nStartup.Checked;
        c.NotifyDailySummary = _nDaily.Checked;
        c.DailySummaryHour = (int)_dailyHour.Value;
        c.StartWithWindows = _startWithWindows.Checked;
        c.StartMinimized = _startMinimized.Checked;
        return c;
    }

    private async Task TestWebhookAsync()
    {
        var url = _webhook.Text.Trim();
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Paste a Discord webhook URL first (Server Settings → Integrations → Webhooks).", "iCloud Backup");
            return;
        }
        var error = await Engine.Notifier.TestAsync(url);
        MessageBox.Show(this, error == null ? "Test message sent - check your Discord channel." : $"Discord said: {error}", "iCloud Backup");
    }

    private async Task SaveAsync()
    {
        var cfg = ReadSettings();

        var problems = new List<string>();
        foreach (var j in cfg.Jobs.Where(j => j.Enabled))
        {
            if (!Directory.Exists(j.SourcePath)) problems.Add($"{j.Name}: iCloud folder not found ({j.SourcePath})");
            if (!await FileOps.PathReachableAsync(j.DestinationPath, TimeSpan.FromSeconds(10))
                && !await FileOps.PathReachableAsync(Path.GetPathRoot(j.DestinationPath) ?? j.DestinationPath, TimeSpan.FromSeconds(10)))
                problems.Add($"{j.Name}: NAS folder not reachable ({j.DestinationPath})");
        }
        if (cfg.Jobs.Count(j => j.Enabled) == 0) problems.Add("No backup jobs are enabled.");
        if (string.IsNullOrEmpty(cfg.GetWebhookUrl())) problems.Add("No Discord webhook set - you won't get notifications.");

        if (problems.Count > 0 &&
            MessageBox.Show(this, "Please check:\n\n• " + string.Join("\n• ", problems) + "\n\nSave anyway?", "iCloud Backup",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;

        _btnSave.Enabled = false;
        _lblSettingsStatus.Text = "Saving and restarting the backup engine...";
        try
        {
            var wasRunning = Engine.Running;
            ConfigStore.Save(cfg);
            StartupRegistration.Apply(cfg.StartWithWindows);
            await Engine.ApplyConfigAsync(cfg);
            if (!wasRunning) _tray.AnnounceStart();
            Log.Info("Settings saved and applied");
            _lblSettingsStatus.Text = $"Saved at {DateTime.Now:HH:mm:ss}. Backups running.";
            LoadSettings(Engine.Config);
            _lblSettingsStatus.Text = $"Saved at {DateTime.Now:HH:mm:ss}. Backups running.";
        }
        catch (Exception ex)
        {
            Log.Error("Could not save settings", ex);
            MessageBox.Show(this, $"Could not save settings: {ex.Message}", "iCloud Backup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _lblSettingsStatus.Text = "Save failed.";
        }
        finally
        {
            _btnSave.Enabled = true;
        }
    }

    // ------------------------------------------------------------------ refresh

    private void RefreshUi()
    {
        if (!Visible) return;
        try
        {
            RefreshLog();
            if (_tabs.SelectedIndex == StatusTab) RefreshStatus();
            if (_tabs.SelectedIndex == FailedTab && DateTime.Now - _failedRefreshed > TimeSpan.FromSeconds(10)) RefreshFailed();
        }
        catch (Exception ex)
        {
            Log.Debug($"UI refresh error: {ex.Message}");
        }
    }
}
