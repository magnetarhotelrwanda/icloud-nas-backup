using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace ICloudBackup.Core;

public sealed record WorkItem(BackupJob Job, string FullPath, string Rel, bool Force)
{
    public string Key => Job.Id + "|" + Rel.ToLowerInvariant();
}

public sealed class ActiveTransfer
{
    public required string Job { get; init; }
    public required string Rel { get; init; }
    public long Size { get; set; }
    public volatile string Phase = "";
    public long Done;
    public DateTime Started { get; } = DateTime.Now;
}

public sealed class IntegrityProgress
{
    public volatile bool Running;
    public volatile string Phase = "";
    public int Total, Done, Ok, Mismatched, Missing, Outdated, Errors, OnlyOnNas;
    public DateTime StartedAt = DateTime.Now;
    public DateTime? FinishedAt;
}

public sealed record ScanResult(int Files, int New, int Changed, int Deleted, int MissingOnNas, int Placeholders, bool Suspicious);

/// <summary>
/// The backup service: watches the iCloud folders, runs the hourly sync, copies + verifies files on worker
/// threads, retries failures with back-off, monitors the NAS / iCloud / disk space and runs integrity checks.
/// </summary>
public sealed class BackupEngine : IDisposable
{
    private readonly StateDb _db;
    private readonly ICloudManager _icloud;
    private AppConfig _cfg;

    private Channel<WorkItem> _queue = Channel.CreateUnbounded<WorkItem>();
    private readonly ConcurrentDictionary<string, byte> _queued = new();
    private Channel<WorkItem> _downloadQueue = Channel.CreateUnbounded<WorkItem>();
    private readonly ConcurrentDictionary<string, byte> _downloadQueued = new();
    private readonly ConcurrentDictionary<int, ActiveTransfer> _active = new();
    private readonly ConcurrentDictionary<string, (BackupJob Job, DateTime Due)> _pendingEvents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _pendingJobScans = new();
    private readonly ConcurrentDictionary<string, bool> _nasOnline = new();
    private readonly HashSet<string> _lowDiskAlerted = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly List<Task> _tasks = [];

    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _integrityCts;
    private volatile bool _systemPaused;
    private volatile bool _icloudRestarting;
    private volatile string? _activity;
    private volatile bool _providerDown;
    private DateTime _lastRelaunchUtc = DateTime.MinValue;
    private bool _icloudDownNotified;
    private int _syncDownChecks;

    private long _sessionCopied, _sessionBytes, _sessionVerified, _sessionFailed;

    public DiscordNotifier Notifier { get; }
    public AppConfig Config => _cfg;
    public bool UserPaused { get; set; }
    public bool Running => _cts != null;
    public DateTime? LastSync { get; private set; }
    public DateTime NextSync { get; private set; } = DateTime.Now;
    public DateTime NextICloudRestart { get; private set; } = DateTime.MaxValue;
    public IntegrityProgress Integrity { get; private set; } = new();

    public long SessionCopied => Interlocked.Read(ref _sessionCopied);
    public long SessionBytes => Interlocked.Read(ref _sessionBytes);
    public long SessionVerified => Interlocked.Read(ref _sessionVerified);
    public long SessionFailed => Interlocked.Read(ref _sessionFailed);
    public int QueueLength => _queued.Count;
    /// <summary>Files waiting for (or in the middle of) an iCloud download.</summary>
    public int DownloadQueueLength => _downloadQueued.Count;
    private const string DownloadingPhase = "Downloading from iCloud";
    public IReadOnlyList<ActiveTransfer> ActiveTransfers => _active.Values.OrderBy(a => a.Started).ToList();
    public bool IsNasOnline(BackupJob job) => !_nasOnline.TryGetValue(job.Id, out var v) || v;
    public StateDb Db => _db;

    public string Activity =>
        UserPaused ? "Paused" :
        _activity ?? (_active.IsEmpty
            ? (_queued.IsEmpty && _downloadQueued.IsEmpty ? "Idle - watching for changes" : "Waiting")
            : $"Working - {_active.Values.Count(a => a.Phase == DownloadingPhase)} downloading from iCloud, {_active.Values.Count(a => a.Phase != DownloadingPhase)} copying");

    public BackupEngine(AppConfig cfg, StateDb db)
    {
        _cfg = cfg;
        _db = db;
        Notifier = new DiscordNotifier(() => _cfg);
        _icloud = new ICloudManager(() => _cfg);
    }

    private IEnumerable<BackupJob> EnabledJobs => _cfg.Jobs.Where(j => j.Enabled);

    // ------------------------------------------------------------------ lifecycle

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _queue = Channel.CreateUnbounded<WorkItem>();
        _queued.Clear();
        _downloadQueue = Channel.CreateUnbounded<WorkItem>();
        _downloadQueued.Clear();
        _pendingEvents.Clear();
        _nasOnline.Clear();

        if (_db.GetMeta("installed") == null) _db.SetMeta("installed", DateTime.Now.ToString("O"));

        foreach (var job in EnabledJobs) StartWatcher(job);

        var threads = Math.Clamp(_cfg.CopyThreads, 1, 32);
        for (var i = 0; i < threads; i++)
        {
            var id = i;
            _tasks.Add(Task.Run(() => WorkerAsync(id, ct)));
        }
        var downloadThreads = Math.Clamp(_cfg.DownloadThreads, 1, 32);
        for (var i = 0; i < downloadThreads; i++)
        {
            var id = 1000 + i; // separate key range in _active
            _tasks.Add(Task.Run(() => DownloadWorkerAsync(id, ct)));
        }
        _tasks.Add(Task.Run(() => SchedulerAsync(ct)));
        NextSync = DateTime.Now;
        NextICloudRestart = _cfg.ICloudRestartIntervalMinutes > 0 ? DateTime.Now.AddMinutes(_cfg.ICloudRestartIntervalMinutes) : DateTime.MaxValue;

        Log.Info($"Backup engine started - {EnabledJobs.Count()} job(s), {threads} copy thread(s), {downloadThreads} download thread(s), " +
                 $"rescan every {_cfg.SyncIntervalMinutes} min, " +
                 (_cfg.ICloudRestartIntervalMinutes > 0 ? $"iCloud restart every {_cfg.ICloudRestartIntervalMinutes} min" : "iCloud restarts off"));
    }

    public async Task StopAsync()
    {
        if (_cts == null) return;
        Log.Info("Stopping backup engine...");
        _cts.Cancel();
        _integrityCts?.Cancel();
        _queue.Writer.TryComplete();
        _downloadQueue.Writer.TryComplete();
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        try { await Task.WhenAll(_tasks).WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { /* cancelled / timed out */ }
        _tasks.Clear();
        _active.Clear();
        _queued.Clear();
        _downloadQueued.Clear();
        _cts.Dispose();
        _cts = null;
        _activity = null;
        _systemPaused = false;
    }

    public async Task ApplyConfigAsync(AppConfig cfg)
    {
        await StopAsync();
        _cfg = cfg;
        Start();
    }

    public void Dispose()
    {
        // Run off the UI thread: after the message loop ends, awaiting there would deadlock.
        try { Task.Run(StopAsync).Wait(TimeSpan.FromSeconds(35)); } catch { }
        Notifier.Dispose();
    }

    // ------------------------------------------------------------------ public actions

    private volatile string _syncReason = "startup";

    /// <summary>Asks the scheduler to run a full sync as soon as it can (within a couple of seconds).</summary>
    public void RequestSync(string reason)
    {
        _syncReason = reason;
        NextSync = DateTime.Now;
    }

    public void RetryNow(FileRecord rec)
    {
        _db.RetryNow(rec.Job, rec.Rel);
        var job = EnabledJobs.FirstOrDefault(j => j.Id == rec.Job);
        if (job != null) Enqueue(new WorkItem(job, Path.Combine(job.SourcePath, rec.Rel), rec.Rel, true));
    }

    public int RetryAll()
    {
        var failed = _db.GetFailed();
        foreach (var rec in failed) RetryNow(rec);
        return failed.Count;
    }

    // ------------------------------------------------------------------ queue + workers

    private void Enqueue(WorkItem item)
    {
        if (_queued.TryAdd(item.Key, 0) && !_queue.Writer.TryWrite(item)) _queued.TryRemove(item.Key, out _);
    }

    private async Task WaitWhilePausedAsync(CancellationToken ct)
    {
        while (UserPaused || _systemPaused) await Task.Delay(1000, ct);
    }

    private async Task WorkerAsync(int id, CancellationToken ct)
    {
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(ct))
            {
                await WaitWhilePausedAsync(ct);
                if (!reader.TryRead(out var item)) continue;
                _queued.TryRemove(item.Key, out _);
                try
                {
                    await ProcessAsync(id, item, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    await HandleFailureAsync(item, ex);
                }
                finally
                {
                    _active.TryRemove(id, out _);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error($"Worker {id} crashed", ex);
        }
    }

    private async Task ProcessAsync(int workerId, WorkItem item, CancellationToken ct)
    {
        var job = item.Job;
        var cfg = _cfg;
        var rel = item.Rel;

        var fi = new FileInfo(item.FullPath);
        if (!fi.Exists) return;
        var src = new SourceFile(fi.FullName, rel, fi.Length, fi.LastWriteTimeUtc.Ticks, fi.Attributes);

        var rec = _db.Get(job.Id, rel);
        if (!item.Force && rec is { Status: FileStatus.Ok } && rec.Size == src.Size && rec.Mtime == src.Mtime) return;

        if (!IsNasOnline(job))
        {
            _db.MarkFailed(job.Id, rel, src.Size, src.Mtime, "NAS not reachable - will copy when it's back", null, countAttempt: false);
            return;
        }

        // 1. Still only in iCloud? Hand it to the download workers; they queue it back here once it's on disk.
        //    Copy threads never sit waiting on iCloud.
        if (CloudFiles.IsPlaceholder(src.Attributes))
        {
            RequestDownload(item);
            return;
        }

        var transfer = new ActiveTransfer { Job = job.Name, Rel = rel, Size = src.Size };
        _active[workerId] = transfer;

        var dest = Path.Combine(job.DestinationPath, rel);
        var existed = File.Exists(dest);
        string? previousHash = rec is { Status: FileStatus.Ok } ? rec.Hash : null;

        // 2. A file we have no record of may already be on the NAS (e.g. from the old PowerShell backup).
        //    If it is byte-for-byte identical, adopt it instead of copying again.
        if (existed && (rec?.Hash == null || item.Force))
        {
            var destInfo = new FileInfo(dest);
            if (destInfo.Length == src.Size)
            {
                transfer.Phase = "Verifying existing NAS copy";
                var srcHash = await FileOps.HashFileAsync(src.FullPath, ct);
                var destHash = await FileOps.HashFileAsync(dest, ct);
                if (string.Equals(srcHash, destHash, StringComparison.OrdinalIgnoreCase))
                {
                    FileOps.FixNameCase(dest);
                    FileOps.TrySetMtime(dest, src.Mtime);
                    _db.MarkOk(job.Id, rel, src.Size, src.Mtime, srcHash, copied: false);
                    Interlocked.Increment(ref _sessionVerified);
                    Notifier.Copied(job.Name, rel, src.Size, updated: false, adopted: true);
                    Log.Debug($"Verified existing NAS copy: {job.Name}: {rel}");
                    return;
                }
                previousHash = destHash;
            }
        }

        // 3. Copy, verify, swap into place.
        transfer.Phase = "Copying to NAS";
        var versionsPath = cfg.KeepVersions ? FileOps.VersionPath(job.DestinationPath, cfg.VersionsFolderName, rel) : null;
        var hash = await FileOps.CopyVerifiedAsync(src, dest, versionsPath, previousHash, done => Interlocked.Exchange(ref transfer.Done, done), ct);

        _db.MarkOk(job.Id, rel, src.Size, src.Mtime, hash, copied: true);
        Interlocked.Increment(ref _sessionCopied);
        Interlocked.Add(ref _sessionBytes, src.Size);
        Notifier.Copied(job.Name, rel, src.Size, updated: existed, adopted: false);
        Log.Ok($"{(existed ? "Updated" : "Copied")} {job.Name}: {rel} ({FileOps.FormatBytes(src.Size)})");
    }

    private void RequestDownload(WorkItem item)
    {
        if (_downloadQueued.TryAdd(item.Key, 0) && !_downloadQueue.Writer.TryWrite(item)) _downloadQueued.TryRemove(item.Key, out _);
    }

    /// <summary>Asks iCloud for one file at a time per thread and waits until it's fully on disk, then queues the copy.</summary>
    private async Task DownloadWorkerAsync(int id, CancellationToken ct)
    {
        var reader = _downloadQueue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(ct))
            {
                await WaitWhilePausedAsync(ct);
                if (!reader.TryRead(out var item)) continue;
                // The key stays in _downloadQueued until the download finishes, so iCloud's own change events
                // during the download don't queue the same file a second time.
                try
                {
                    var fi = new FileInfo(item.FullPath);
                    if (!fi.Exists) continue;
                    if (CloudFiles.IsPlaceholder(fi.Attributes))
                    {
                        _active[id] = new ActiveTransfer { Job = item.Job.Name, Rel = item.Rel, Size = fi.Length, Phase = DownloadingPhase };
                        var started = DateTime.Now;
                        await CloudFiles.HydrateAsync(item.FullPath, TimeSpan.FromMinutes(Math.Max(1, _cfg.HydrationTimeoutMinutes)), ct);
                        Log.Info($"Downloaded from iCloud in {DiscordNotifier.FormatSpan(DateTime.Now - started)}: {item.Job.Name}: {item.Rel} ({FileOps.FormatBytes(fi.Length)})");
                    }
                    Enqueue(item);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (ex is CloudDownloadException { ProviderNotRunning: true }) _providerDown = true;
                    await HandleFailureAsync(item, ex);
                }
                finally
                {
                    _active.TryRemove(id, out _);
                    _downloadQueued.TryRemove(item.Key, out _);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error($"Download worker {id} crashed", ex);
        }
    }

    private int RetryDownloadFailuresNow()
    {
        var jobs = EnabledJobs.ToDictionary(j => j.Id);
        var n = 0;
        foreach (var rec in _db.GetFailed())
        {
            if (rec.LastError?.StartsWith(CloudFiles.DownloadErrorPrefix, StringComparison.Ordinal) != true) continue;
            if (!jobs.TryGetValue(rec.Job, out var job)) continue;
            _db.RetryNow(rec.Job, rec.Rel);
            Enqueue(new WorkItem(job, Path.Combine(job.SourcePath, rec.Rel), rec.Rel, false));
            n++;
        }
        return n;
    }

    private async Task HandleFailureAsync(WorkItem item, Exception ex)
    {
        var job = item.Job;
        var cfg = _cfg;
        try
        {
            if (ex is FileNotFoundException or DirectoryNotFoundException && !File.Exists(item.FullPath))
            {
                Log.Debug($"File disappeared before it could be copied: {item.Rel}");
                return;
            }

            long size = 0, mtime = 0;
            try
            {
                var fi = new FileInfo(item.FullPath);
                if (fi.Exists) { size = fi.Length; mtime = fi.LastWriteTimeUtc.Ticks; }
            }
            catch { }

            var message = ex is AggregateException ae ? ae.InnerException?.Message ?? ex.Message : ex.Message;

            if (!await FileOps.PathReachableAsync(job.DestinationPath, TimeSpan.FromSeconds(15)))
            {
                SetNasOnline(job, false);
                _db.MarkFailed(job.Id, item.Rel, size, mtime, "NAS not reachable: " + message, null, countAttempt: false);
                return;
            }

            var attempts = (_db.Get(job.Id, item.Rel)?.Attempts ?? 0) + 1;
            // Waiting on iCloud is usually temporary, so those retry on a shorter cap than other failures.
            var cap = ex is CloudDownloadException ? Math.Min(cfg.DownloadRetryMaxMinutes, cfg.RetryMaxDelayMinutes) : cfg.RetryMaxDelayMinutes;
            var delay = TimeSpan.FromMinutes(Math.Min(Math.Max(1, cap), Math.Pow(2, Math.Min(attempts - 1, 16))));
            attempts = _db.MarkFailed(job.Id, item.Rel, size, mtime, message,
                DateTimeOffset.UtcNow.Add(delay).ToUnixTimeMilliseconds());
            Interlocked.Increment(ref _sessionFailed);

            Log.Warn($"Failed (attempt {attempts}) {job.Name}: {item.Rel} - {message} - retrying in {DiscordNotifier.FormatSpan(delay)}");
            Notifier.Failed(job.Name, item.Rel, message, attempts, delay);

            if (attempts == cfg.RetryAlertAfterAttempts && cfg.NotifyRetryAlerts)
            {
                Notifier.Send($":rotating_light: **iCloud Backup** - {job.Name}: {DiscordNotifier.Code(item.Rel)} has failed {attempts} times in a row. " +
                              $"Last error: {message}\nIt will keep retrying (up to every {cfg.RetryMaxDelayMinutes / 60.0:0.#} h). Check the Failed tab in the app.");
            }
        }
        catch (Exception inner)
        {
            Log.Error($"Could not record failure for {item.Rel}", inner);
        }
    }

    // ------------------------------------------------------------------ watching

    private void StartWatcher(BackupJob job)
    {
        if (!Directory.Exists(job.SourcePath))
        {
            Log.Warn($"{job.Name}: source folder not found, not watching: {job.SourcePath}");
            return;
        }
        try
        {
            var w = new FileSystemWatcher(job.SourcePath)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = 64 * 1024,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            w.Created += (_, e) => OnFsEvent(job, e.FullPath, structural: true);
            w.Renamed += (_, e) => OnFsEvent(job, e.FullPath, structural: true);
            w.Changed += (_, e) => OnFsEvent(job, e.FullPath, structural: false);
            w.Error += (_, e) =>
            {
                Log.Warn($"{job.Name}: file watcher overflowed ({e.GetException().Message}) - scheduling a rescan");
                _pendingJobScans[job.Id] = DateTime.UtcNow.AddSeconds(_cfg.FileSettleSeconds);
            };
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
            Log.Info($"{job.Name}: watching {job.SourcePath}");
        }
        catch (Exception ex)
        {
            Log.Error($"{job.Name}: could not watch {job.SourcePath}", ex);
        }
    }

    private void OnFsEvent(BackupJob job, string path, bool structural)
    {
        try
        {
            var due = DateTime.UtcNow.AddSeconds(Math.Max(1, _cfg.FileSettleSeconds));
            if (Directory.Exists(path))
            {
                // A new or renamed folder: rescan the job so everything inside it is picked up.
                if (structural) _pendingJobScans[job.Id] = due;
                return;
            }
            if (job.IsExcluded(Path.GetFileName(path))) return;
            _pendingEvents[path] = (job, due);
        }
        catch { /* never throw on the watcher thread */ }
    }

    private void FlushWatcherEvents()
    {
        var now = DateTime.UtcNow;
        foreach (var (path, (job, due)) in _pendingEvents)
        {
            if (due > now || !_pendingEvents.TryRemove(path, out _)) continue;
            if (!File.Exists(path)) continue;
            Enqueue(new WorkItem(job, path, Path.GetRelativePath(job.SourcePath, path), false));
        }

        foreach (var (jobId, due) in _pendingJobScans)
        {
            if (due > now) continue;
            var job = EnabledJobs.FirstOrDefault(j => j.Id == jobId);
            if (job == null) { _pendingJobScans.TryRemove(jobId, out _); continue; }
            if (!_syncLock.Wait(0))
            {
                _pendingJobScans[jobId] = now.AddSeconds(60);
                continue;
            }
            _pendingJobScans.TryRemove(jobId, out _);
            var ct = _cts!.Token;
            _ = Task.Run(async () =>
            {
                try { await ScanJobAsync(job, pin: false, ct); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log.Error($"{job.Name}: rescan failed", ex); }
                finally { _activity = null; _syncLock.Release(); }
            });
        }
    }

    // ------------------------------------------------------------------ scheduler

    private async Task SchedulerAsync(CancellationToken ct)
    {
        DateTime lastNas = DateTime.MinValue, lastRetry = DateTime.MinValue, lastWatchdog = DateTime.MinValue, lastDisk = DateTime.MinValue;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.Now;
                if (now - lastNas >= TimeSpan.FromSeconds(30)) { lastNas = now; await CheckNasAsync(ct); }

                FlushWatcherEvents();

                if (now - lastRetry >= TimeSpan.FromSeconds(30)) { lastRetry = now; EnqueueDueRetries(); }
                if (_cfg.ICloudWatchdog && now - lastWatchdog >= TimeSpan.FromSeconds(60)) { lastWatchdog = now; await WatchdogAsync(ct); }
                if (now - lastDisk >= TimeSpan.FromMinutes(10)) { lastDisk = now; CheckDiskSpace(); }
                if (now >= NextSync)
                {
                    var reason = _syncReason;
                    _syncReason = "scheduled";
                    _ = RunSyncAsync(reason);
                }
                if (now >= NextICloudRestart) _ = RunICloudRestartAsync();
                CheckIntegritySchedule(now);
                CheckDailySummary(now);

                await Task.Delay(2000, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Error("Scheduler error", ex);
                try { await Task.Delay(5000, ct); } catch { break; }
            }
        }
    }

    private void EnqueueDueRetries()
    {
        var jobs = EnabledJobs.ToDictionary(j => j.Id);
        foreach (var rec in _db.GetDueRetries(StateDb.Now))
        {
            if (!jobs.TryGetValue(rec.Job, out var job) || !IsNasOnline(job)) continue;
            Enqueue(new WorkItem(job, Path.Combine(job.SourcePath, rec.Rel), rec.Rel, false));
        }
    }

    private void SetNasOnline(BackupJob job, bool online)
    {
        var known = _nasOnline.TryGetValue(job.Id, out var prev);
        _nasOnline[job.Id] = online;
        if (known && prev == online) return;

        if (!online)
        {
            Log.Error($"{job.Name}: NAS destination not reachable: {job.DestinationPath} - copies paused");
            if (_cfg.NotifyNasEvents)
                Notifier.Send($":red_circle: **iCloud Backup** - NAS not reachable for {job.Name} ({DiscordNotifier.Code(job.DestinationPath)}). Copies are paused until it's back.");
        }
        else if (known)
        {
            Log.Ok($"{job.Name}: NAS reachable again - resuming");
            if (_cfg.NotifyNasEvents)
                Notifier.Send($":green_circle: **iCloud Backup** - NAS reachable again for {job.Name}. Resuming backups.");
            _pendingJobScans[job.Id] = DateTime.UtcNow;
        }
    }

    private async Task CheckNasAsync(CancellationToken ct)
    {
        foreach (var job in EnabledJobs)
        {
            ct.ThrowIfCancellationRequested();
            var ok = await FileOps.PathReachableAsync(job.DestinationPath, TimeSpan.FromSeconds(15));
            if (!ok)
            {
                // Share is up but the folder doesn't exist yet - create it.
                var root = Path.GetPathRoot(job.DestinationPath);
                if (!string.IsNullOrEmpty(root) && await FileOps.PathReachableAsync(root, TimeSpan.FromSeconds(15)))
                {
                    try
                    {
                        Directory.CreateDirectory(job.DestinationPath);
                        Log.Info($"{job.Name}: created destination folder {job.DestinationPath}");
                        ok = true;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"{job.Name}: could not create destination folder: {ex.Message}");
                    }
                }
            }
            SetNasOnline(job, ok);
        }
    }

    /// <summary>
    /// Relaunches iCloud when its sync engine (ICloudSyncProcessName) has stopped, or when downloads report that
    /// the sync engine isn't connected. The engine must be missing on three checks in a row (so iCloud starting
    /// up isn't mistaken for a crash), and there's at most one attempt every 10 minutes. Other iCloud processes, such
    /// as iCloudPhotos, come and go on their own and are ignored.
    /// </summary>
    private async Task WatchdogAsync(CancellationToken ct)
    {
        if (_icloudRestarting) return;
        var syncRunning = _icloud.SyncRunning();
        _syncDownChecks = syncRunning ? 0 : _syncDownChecks + 1;
        var down = !syncRunning && _icloud.EverSeenRunning && _syncDownChecks >= 3;

        if (syncRunning && _icloudDownNotified)
        {
            _icloudDownNotified = false;
            Log.Ok("iCloud is running again");
            if (_cfg.NotifyICloudEvents) Notifier.Send(":green_circle: **iCloud Backup** - iCloud is running again.");
        }
        if (!down && !_providerDown) return;
        if (DateTime.UtcNow - _lastRelaunchUtc < TimeSpan.FromMinutes(10)) return;
        _lastRelaunchUtc = DateTime.UtcNow;

        if (!down && _providerDown)
        {
            // Processes are up but downloads say the sync engine isn't connected: schedule a clean restart
            // (which waits for active work to finish) rather than killing anything here.
            _providerDown = false;
            Log.Warn("iCloud downloads report the sync engine isn't connected - scheduling an iCloud restart");
            NextICloudRestart = DateTime.Now;
            return;
        }

        _providerDown = false;
        Log.Warn("iCloud's sync engine isn't running - relaunching iCloud");
        var launched = await _icloud.EnsureRunningAsync(ct);
        if (_cfg.NotifyICloudEvents && !_icloudDownNotified)
        {
            _icloudDownNotified = true;
            Notifier.Send(launched
                ? ":arrows_counterclockwise: **iCloud Backup** - iCloud had stopped. Relaunched it."
                : ":warning: **iCloud Backup** - iCloud has stopped and could not be relaunched automatically. New files won't download until it's running.");
        }
    }

    private void CheckDiskSpace()
    {
        var threshold = (long)_cfg.LowDiskSpaceGb * 1024 * 1024 * 1024;
        if (threshold <= 0) return;
        foreach (var root in EnabledJobs.Select(j => Path.GetPathRoot(j.SourcePath)).Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var free = new DriveInfo(root!).AvailableFreeSpace;
                if (free < threshold && _lowDiskAlerted.Add(root!))
                {
                    Log.Warn($"Low disk space on {root}: {FileOps.FormatBytes(free)} free");
                    Notifier.Send($":floppy_disk: **iCloud Backup** - low disk space on {root} ({FileOps.FormatBytes(free)} free). " +
                                  "iCloud keeps downloaded files on this disk; if it fills up, downloads will fail. Expand the VM disk or turn off \"Keep files on this device\" for a job.");
                }
                else if (free > threshold * 1.1) _lowDiskAlerted.Remove(root!);
            }
            catch { /* network drive or not ready */ }
        }
    }

    // ------------------------------------------------------------------ sync

    private async Task RunSyncAsync(string reason)
    {
        if (_cts == null || !_syncLock.Wait(0)) return;
        var ct = _cts.Token;
        NextSync = DateTime.MaxValue;
        var started = DateTime.Now;
        try
        {
            Log.Info($"Sync started ({reason})");

            // Only a full stop is handled here; iCloud may still be starting up (e.g. at login), so a missing
            // sync engine alone is left to the watchdog, which waits for it to stay missing.
            if (!_icloud.AnyRunning())
            {
                Log.Warn("No iCloud processes running - launching iCloud");
                await _icloud.EnsureRunningAsync(ct);
            }

            var results = new List<(BackupJob Job, ScanResult Result)>();
            foreach (var job in EnabledJobs)
            {
                var r = await ScanJobAsync(job, job.KeepFilesLocal, ct);
                if (r != null) results.Add((job, r));
            }

            LastSync = DateTime.Now;
            _db.SetMeta("last_sync", LastSync.Value.ToString("O"));
            var took = DateTime.Now - started;
            Log.Ok($"Sync finished in {DiscordNotifier.FormatSpan(took)} - queue: {QueueLength}");

            var deleted = results.Sum(r => r.Result.Deleted);
            var missing = results.Sum(r => r.Result.MissingOnNas);
            if (_cfg.NotifySyncSummary || deleted > 0 || missing > 0)
            {
                var sb = new StringBuilder($":arrows_counterclockwise: **iCloud Backup** - sync complete ({reason}, {DiscordNotifier.FormatSpan(took)})\n");
                foreach (var (job, r) in results)
                {
                    sb.Append($"• {job.Name}: {r.Files:N0} files in iCloud, {r.New} new, {r.Changed} changed");
                    if (r.Placeholders > 0) sb.Append($", {r.Placeholders} still downloading from iCloud");
                    if (r.Deleted > 0) sb.Append($", {r.Deleted} removed from iCloud (kept on NAS)");
                    if (r.MissingOnNas > 0) sb.Append($", **{r.MissingOnNas} missing or wrong size on NAS - re-copying**");
                    sb.Append('\n');
                }
                Notifier.Send(sb.ToString());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Sync failed", ex);
            Notifier.Send($":warning: **iCloud Backup** - sync failed: {ex.Message}");
        }
        finally
        {
            _activity = null;
            NextSync = DateTime.Now.AddMinutes(Math.Max(5, _cfg.SyncIntervalMinutes));
            _syncLock.Release();
        }
    }

    /// <summary>
    /// Restarts iCloud so it re-checks for changes and retries stuck downloads. Never interrupts work: new work is
    /// held back, and the restart only happens once every active download and copy has finished. If that takes
    /// too long the restart is skipped and tried again 5 minutes later.
    /// </summary>
    private async Task RunICloudRestartAsync()
    {
        if (_cts == null || !_syncLock.Wait(0)) return;
        var ct = _cts.Token;
        var cfg = _cfg;
        NextICloudRestart = DateTime.MaxValue;
        DateTime? retryAt = null;
        try
        {
            var stuck = _downloadQueued.Count + _db.GetFailed().Count(r => r.LastError?.StartsWith(CloudFiles.DownloadErrorPrefix, StringComparison.Ordinal) == true);
            if (cfg.ICloudRestartOnlyWhenStuck && stuck == 0)
            {
                Log.Debug("Scheduled iCloud restart skipped - nothing is waiting on iCloud");
                return;
            }

            _activity = "Letting active downloads and copies finish before restarting iCloud";
            _systemPaused = true;
            var limit = TimeSpan.FromMinutes(Math.Max(30, cfg.HydrationTimeoutMinutes + 5));
            var until = DateTime.UtcNow + limit;
            while (DateTime.UtcNow < until)
            {
                while (!_active.IsEmpty && DateTime.UtcNow < until) await Task.Delay(1000, ct);
                // A worker may have taken a file just as the pause was set; give it a moment to show up.
                await Task.Delay(2000, ct);
                if (_active.IsEmpty) break;
            }
            if (!_active.IsEmpty)
            {
                Log.Warn($"iCloud restart skipped - {_active.Count} download(s)/copy(s) still running after {limit.TotalMinutes:0} min. Trying again in 5 min.");
                retryAt = DateTime.Now.AddMinutes(5);
                return;
            }

            _activity = "Restarting iCloud";
            _icloudRestarting = true;
            Log.Info($"Restarting iCloud ({stuck} file(s) waiting on iCloud)");
            _lastRelaunchUtc = DateTime.UtcNow;
            await _icloud.RestartAsync(ct);

            _activity = $"Waiting {cfg.ICloudSettleSeconds}s for iCloud to reconnect";
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, cfg.ICloudSettleSeconds)), ct);
            _providerDown = false;

            if (_icloud.SyncRunning()) Log.Ok("iCloud restarted");
            else Log.Warn("iCloud's sync engine isn't running after restart - the watchdog will keep trying");

            var retried = RetryDownloadFailuresNow();
            if (retried > 0) Log.Info($"Retrying {retried} file(s) that were waiting on iCloud");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("iCloud restart failed", ex);
        }
        finally
        {
            _icloudRestarting = false;
            _systemPaused = false;
            _activity = null;
            NextICloudRestart = retryAt
                ?? (cfg.ICloudRestartIntervalMinutes > 0 ? DateTime.Now.AddMinutes(cfg.ICloudRestartIntervalMinutes) : DateTime.MaxValue);
            _syncLock.Release();
        }
    }

    /// <summary>Compares the iCloud folder against the database and the NAS, queueing anything that needs copying.</summary>
    private async Task<ScanResult?> ScanJobAsync(BackupJob job, bool pin, CancellationToken ct)
    {
        if (!Directory.Exists(job.SourcePath))
        {
            Log.Error($"{job.Name}: source folder not found: {job.SourcePath}");
            if (_cfg.NotifyICloudEvents)
                Notifier.Send($":warning: **iCloud Backup** - {job.Name}: source folder not found ({DiscordNotifier.Code(job.SourcePath)}). Is iCloud signed in?");
            return null;
        }

        _activity = $"Scanning {job.Name}";
        var known = _db.GetAll(job.Id);
        var files = await Task.Run(() => FileOps.Walk(job.SourcePath, job.IsExcluded).ToList(), ct);
        var now = StateDb.Now;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int newCount = 0, changed = 0, deleted = 0, missingOnNas = 0, placeholders = 0, unpinned = 0;
        var unchanged = new List<SourceFile>();

        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            seen.Add(f.Rel);
            if (CloudFiles.IsPlaceholder(f.Attributes)) placeholders++;
            if (!CloudFiles.IsPinned(f.Attributes)) unpinned++;

            if (known.TryGetValue(f.Rel, out var rec))
            {
                var same = rec.Size == f.Size && rec.Mtime == f.Mtime;
                if (rec.Status == FileStatus.Ok && same) { unchanged.Add(f); continue; }
                if (rec.Status == FileStatus.Failed && same && rec.NextAttempt > now) continue; // still backing off
                if (rec.Status == FileStatus.Ok) changed++;
                else if (rec.Status == FileStatus.Deleted) newCount++;
            }
            else newCount++;

            Enqueue(new WorkItem(job, f.FullPath, f.Rel, false));
        }

        // If iCloud signed out or the folder got emptied, don't mark everything as deleted.
        var okKnown = known.Values.Count(r => r.Status == FileStatus.Ok);
        var suspicious = files.Count == 0 && okKnown > 0 || okKnown > 100 && files.Count < okKnown / 2;
        if (suspicious)
        {
            Log.Error($"{job.Name}: only {files.Count} files found but {okKnown} were backed up before - skipping deletion tracking");
            Notifier.Send($":rotating_light: **iCloud Backup** - {job.Name}: the iCloud folder only has {files.Count:N0} files but {okKnown:N0} were backed up before. " +
                          "iCloud may be signed out or still syncing. Nothing has been removed from the NAS.");
        }
        else
        {
            foreach (var rec in known.Values)
            {
                if (rec.Status == FileStatus.Deleted || seen.Contains(rec.Rel)) continue;
                _db.MarkDeleted(job.Id, rec.Rel);
                deleted++;
                Log.Info($"{job.Name}: removed from iCloud (backup kept on NAS): {rec.Rel}");
            }
        }

        // Cheap NAS check: every file we think is backed up must exist there with the right size.
        if (IsNasOnline(job) && unchanged.Count > 0)
        {
            _activity = $"Checking NAS copies for {job.Name}";
            var nas = await Task.Run(() =>
            {
                var d = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                foreach (var nf in FileOps.Walk(job.DestinationPath, n => n.EndsWith(FileOps.PartialSuffix, StringComparison.OrdinalIgnoreCase), _cfg.VersionsFolderName))
                    d.TryAdd(nf.Rel, nf.Size);
                return d;
            }, ct);

            if (nas.Count == 0 && unchanged.Count > 100)
            {
                Log.Error($"{job.Name}: NAS folder listed as empty - skipping NAS check (share problem?)");
            }
            else
            {
                foreach (var f in unchanged)
                {
                    if (nas.TryGetValue(f.Rel, out var size) && size == f.Size) continue;
                    missingOnNas++;
                    Log.Warn($"{job.Name}: missing or wrong size on NAS, re-copying: {f.Rel}");
                    Enqueue(new WorkItem(job, f.FullPath, f.Rel, true));
                }
            }
        }

        if (pin && (unpinned > 0 || placeholders > 0))
        {
            _activity = $"Setting {job.Name} to always keep files on this device";
            if (await CloudFiles.PinFolderAsync(job.SourcePath, ct))
                Log.Info($"{job.Name}: pinned folder - iCloud will download {placeholders} online-only file(s) and keep them local");
        }

        _db.SetMeta($"last_scan_files:{job.Id}", files.Count.ToString(CultureInfo.InvariantCulture));
        Log.Info($"{job.Name}: scanned {files.Count:N0} files - {newCount} new, {changed} changed, {deleted} removed, {missingOnNas} to re-copy, {placeholders} online-only");
        return new ScanResult(files.Count, newCount, changed, deleted, missingOnNas, placeholders, suspicious);
    }

    // ------------------------------------------------------------------ integrity

    public bool StartIntegrityCheck(string reason)
    {
        if (Integrity.Running || _cts == null) return false;
        _integrityCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var progress = new IntegrityProgress { Running = true, Phase = "Starting" };
        Integrity = progress;
        _db.SetMeta("last_integrity_started", DateTime.Now.ToString("O"));
        var ct = _integrityCts.Token;
        _ = Task.Run(() => RunIntegrityAsync(progress, reason, ct));
        return true;
    }

    public void CancelIntegrityCheck() => _integrityCts?.Cancel();

    private void CheckIntegritySchedule(DateTime now)
    {
        var days = _cfg.IntegrityCheckIntervalDays;
        if (days <= 0 || Integrity.Running || now.Hour != _cfg.IntegrityCheckHour) return;
        var last = ParseMetaDate("last_integrity_started") ?? ParseMetaDate("installed") ?? now;
        if (now - last >= TimeSpan.FromDays(days) - TimeSpan.FromHours(2)) StartIntegrityCheck("scheduled");
    }

    private DateTime? ParseMetaDate(string key) =>
        DateTime.TryParse(_db.GetMeta(key), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d) ? d : null;

    private async Task RunIntegrityAsync(IntegrityProgress p, string reason, CancellationToken ct)
    {
        var cfg = _cfg;
        var problems = new ConcurrentBag<(string Job, string Rel, string Issue)>();
        Log.Info($"Integrity check started ({reason})");
        if (cfg.NotifyIntegrity) Notifier.Send($":mag: **iCloud Backup** - integrity check started ({reason}). Every file will be hashed in iCloud and on the NAS.");

        try
        {
            foreach (var job in EnabledJobs)
            {
                if (!Directory.Exists(job.SourcePath) || !IsNasOnline(job))
                {
                    problems.Add((job.Name, "", "Skipped - source folder or NAS not reachable"));
                    continue;
                }

                p.Phase = $"Listing {job.Name}";
                var files = await Task.Run(() => FileOps.Walk(job.SourcePath, job.IsExcluded).ToList(), ct);
                var known = _db.GetAll(job.Id);
                Interlocked.Add(ref p.Total, files.Count);
                p.Phase = $"Hashing {job.Name}";

                await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(cfg.CopyThreads, 1, 32), CancellationToken = ct },
                    async (f, ct2) =>
                    {
                        try
                        {
                            await CheckOneAsync(job, f, known, p, problems, cfg, ct2);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref p.Errors);
                            problems.Add((job.Name, f.Rel, "Error: " + ex.Message));
                        }
                        finally
                        {
                            Interlocked.Increment(ref p.Done);
                        }
                    });

                p.Phase = $"Looking for files only on the NAS ({job.Name})";
                var sourceSet = new HashSet<string>(files.Select(f => f.Rel), StringComparer.OrdinalIgnoreCase);
                var onlyOnNas = await Task.Run(() => FileOps.Walk(job.DestinationPath,
                        n => n.EndsWith(FileOps.PartialSuffix, StringComparison.OrdinalIgnoreCase), cfg.VersionsFolderName)
                    .Count(nf => !sourceSet.Contains(nf.Rel)), ct);
                Interlocked.Add(ref p.OnlyOnNas, onlyOnNas);
            }

            p.FinishedAt = DateTime.Now;
            var took = p.FinishedAt.Value - p.StartedAt;
            var summary = $"{p.Ok:N0} verified OK, {p.Mismatched} mismatched, {p.Missing} missing on NAS, {p.Outdated} changed since last copy, {p.Errors} errors";
            _db.SetMeta("last_integrity", p.FinishedAt.Value.ToString("O"));
            _db.SetMeta("last_integrity_result", summary);

            string? reportPath = null;
            if (!problems.IsEmpty)
            {
                reportPath = Path.Combine(AppPaths.ReportDir, $"integrity-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
                var sb = new StringBuilder("Job,File,Issue\n");
                foreach (var (j, r, issue) in problems.OrderBy(x => x.Job).ThenBy(x => x.Rel))
                    sb.Append(Csv(j)).Append(',').Append(Csv(r)).Append(',').Append(Csv(issue)).Append('\n');
                await File.WriteAllTextAsync(reportPath, sb.ToString(), CancellationToken.None);
            }

            var clean = p.Mismatched == 0 && p.Missing == 0 && p.Errors == 0;
            if (clean) Log.Ok($"Integrity check finished in {DiscordNotifier.FormatSpan(took)}: {summary}");
            else Log.Warn($"Integrity check finished in {DiscordNotifier.FormatSpan(took)}: {summary}. Report: {reportPath}");

            if (cfg.NotifyIntegrity)
            {
                var msg = new StringBuilder(clean
                    ? $":white_check_mark: **iCloud Backup** - integrity check passed in {DiscordNotifier.FormatSpan(took)}. {summary}."
                    : $":x: **iCloud Backup** - integrity check found problems ({DiscordNotifier.FormatSpan(took)}). {summary}. Problem files have been queued to re-copy.");
                if (p.OnlyOnNas > 0) msg.Append($"\n{p.OnlyOnNas:N0} file(s) exist only on the NAS (removed from iCloud, kept as backup).");
                foreach (var (j, r, issue) in problems.Where(x => !x.Issue.StartsWith("Changed")).Take(10))
                    msg.Append($"\n• {j}: {DiscordNotifier.Code(r)} - {issue}");
                if (reportPath != null) msg.Append($"\nFull report on the VM: {DiscordNotifier.Code(reportPath)}");
                Notifier.Send(msg.ToString());
            }
        }
        catch (OperationCanceledException)
        {
            Log.Warn($"Integrity check cancelled after {p.Done:N0} of {p.Total:N0} files");
        }
        catch (Exception ex)
        {
            Log.Error("Integrity check failed", ex);
            Notifier.Send($":warning: **iCloud Backup** - integrity check failed: {ex.Message}");
        }
        finally
        {
            p.FinishedAt ??= DateTime.Now;
            p.Phase = "Finished";
            p.Running = false;
        }
    }

    private async Task CheckOneAsync(BackupJob job, SourceFile f, Dictionary<string, FileRecord> known, IntegrityProgress p,
        ConcurrentBag<(string, string, string)> problems, AppConfig cfg, CancellationToken ct)
    {
        var dest = Path.Combine(job.DestinationPath, f.Rel);
        known.TryGetValue(f.Rel, out var rec);

        if (rec != null && rec.Status == FileStatus.Ok && (rec.Size != f.Size || rec.Mtime != f.Mtime) ||
            rec != null && rec.Status != FileStatus.Ok)
        {
            // Not a corruption - just a change that hasn't been backed up yet.
            Interlocked.Increment(ref p.Outdated);
            problems.Add((job.Name, f.Rel, "Changed in iCloud since last backup - queued"));
            Enqueue(new WorkItem(job, f.FullPath, f.Rel, false));
            return;
        }

        if (!File.Exists(dest))
        {
            Interlocked.Increment(ref p.Missing);
            problems.Add((job.Name, f.Rel, "Missing on NAS - queued for copy"));
            Enqueue(new WorkItem(job, f.FullPath, f.Rel, true));
            return;
        }

        string srcHash;
        if (cfg.IntegrityRehashSource || rec?.Hash == null)
        {
            if (CloudFiles.IsPlaceholder(f.Attributes))
                await CloudFiles.HydrateAsync(f.FullPath, TimeSpan.FromMinutes(Math.Max(1, cfg.HydrationTimeoutMinutes)), ct);
            srcHash = await FileOps.HashFileAsync(f.FullPath, ct);
        }
        else srcHash = rec.Hash;

        var destHash = await FileOps.HashFileAsync(dest, ct);
        if (string.Equals(srcHash, destHash, StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref p.Ok);
            _db.MarkOk(job.Id, f.Rel, f.Size, f.Mtime, srcHash, copied: false);
        }
        else
        {
            Interlocked.Increment(ref p.Mismatched);
            problems.Add((job.Name, f.Rel, $"Hash mismatch (iCloud {srcHash[..12]}…, NAS {destHash[..12]}…) - queued for re-copy"));
            Log.Warn($"Integrity mismatch {job.Name}: {f.Rel}");
            Enqueue(new WorkItem(job, f.FullPath, f.Rel, true));
        }
    }

    private static string Csv(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    // ------------------------------------------------------------------ daily summary

    private void CheckDailySummary(DateTime now)
    {
        if (!_cfg.NotifyDailySummary || now.Hour != _cfg.DailySummaryHour) return;
        var today = now.ToString("yyyy-MM-dd");
        if (_db.GetMeta("last_daily") == today) return;
        _db.SetMeta("last_daily", today);
        Notifier.Send(BuildDailySummary());
    }

    public string BuildDailySummary()
    {
        var sb = new StringBuilder(":bar_chart: **iCloud Backup - daily summary**\n");
        var since = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();
        foreach (var job in EnabledJobs)
        {
            var c = _db.Counts(job.Id);
            var (n, bytes) = _db.CopiedSince(job.Id, since);
            sb.Append($"• **{job.Name}**: {c.Ok:N0} files backed up, {n:N0} copied in the last 24 h ({FileOps.FormatBytes(bytes)})");
            if (c.Failed > 0) sb.Append($", **{c.Failed} failing**");
            if (!IsNasOnline(job)) sb.Append(", **NAS offline**");
            sb.Append('\n');
        }
        sb.Append($"Last sync: {LastSync?.ToString("HH:mm") ?? "not yet"}\n");
        var lastIntegrity = ParseMetaDate("last_integrity");
        sb.Append($"Last integrity check: {(lastIntegrity == null ? "never" : $"{lastIntegrity:ddd d MMM} - {_db.GetMeta("last_integrity_result")}")}\n");
        foreach (var root in EnabledJobs.Select(j => Path.GetPathRoot(j.SourcePath)).Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { sb.Append($"Free space on {root}: {FileOps.FormatBytes(new DriveInfo(root!).AvailableFreeSpace)}\n"); } catch { }
        }
        return sb.ToString();
    }
}
