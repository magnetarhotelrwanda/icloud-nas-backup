# iCloud NAS Backup

A Windows tray app that keeps a continuously updated, hash-verified copy of **iCloud Drive** and **iCloud Photos** on a NAS (or any folder or SMB share).

It runs on a Windows machine (typically a VM) with **iCloud for Windows** installed. iCloud syncs files into local folders, and this app watches those folders. It makes sure every file is actually downloaded from iCloud, copies it to the NAS, checks the copy, and reports to Discord.

- One self-contained `.exe` (.NET 10, WinForms). No installer, no runtime to install, no Windows service.
- State is kept in SQLite, settings in JSON, logs in daily text files. Everything is under `%LOCALAPPDATA%\ICloudBackup`.
- It never deletes anything from the NAS.

---

## Contents

1. [Quick start](#quick-start)
2. [Requirements](#requirements)
3. [How it works](#how-it-works)
   - [Components](#components)
   - [Lifecycle](#lifecycle)
   - [The copy pipeline (per file)](#the-copy-pipeline-per-file)
   - [Getting files out of iCloud (hydration and pinning)](#getting-files-out-of-icloud-hydration-and-pinning)
   - [Change detection](#change-detection)
   - [Hourly sync](#hourly-sync)
   - [Retries](#retries)
   - [Integrity check](#integrity-check)
   - [Health monitoring](#health-monitoring)
   - [Deletions and safety rails](#deletions-and-safety-rails)
   - [Discord notifications](#discord-notifications)
4. [Files and data locations](#files-and-data-locations)
5. [Configuration reference](#configuration-reference)
6. [State database](#state-database)
7. [User interface](#user-interface)
8. [Operations runbook](#operations-runbook)
9. [Troubleshooting](#troubleshooting)
10. [Known limitations](#known-limitations)
11. [Building and releasing](#building-and-releasing)
12. [Source layout](#source-layout)

---

## Quick start

1. Install **iCloud for Windows** on the machine. Sign in, and turn on iCloud Drive and iCloud Photos.
2. Download `ICloudBackup.exe` from the [Releases](../../releases) page and put it in a **permanent** folder, e.g. `C:\Tools\ICloudBackup\`. The start-with-Windows entry points at the exe's location, so don't run it from Downloads or the Desktop.
3. Run it. On first run the **Settings** tab opens and **nothing is backed up until you save**.
4. Check each backup job's iCloud folder and NAS folder. Paste a Discord webhook URL and click **Send test**.
5. Click **Save and apply**. The app starts a full sync straight away and then runs in the system tray (cloud icon).
6. Set up the machine to **sign in to Windows automatically**. See [Requirements](#requirements).

Windows SmartScreen will warn that the exe is unsigned. Click **More info**, then **Run anyway**.

---

## Requirements

| Requirement | Why |
|---|---|
| Windows 10 1709+ or Windows 11, x64 | Uses the Windows Cloud Files API (`cldapi.dll`) |
| iCloud for Windows (Microsoft Store version), signed in | Provides the synced folders |
| **An interactive, signed-in user session** (auto-logon on a VM) | iCloud for Windows only syncs while its user is signed in, and only downloads files for that session. The app therefore runs as a normal per-user tray app, **not** as a Windows service. |
| Write access to the NAS path for that Windows user | Mapped drives (`Z:\`) work because the app runs in the user's session. UNC paths (`\\nas\share\...`) are preferred because they don't depend on the drive mapping. |
| Enough local disk for the whole iCloud library, if "Keep files on this device" is on | Pinning makes iCloud keep a full local copy |

Only **one instance** can run per user (enforced by a named mutex, `Local\ICloudBackup.SingleInstance`).

---

## How it works

### Components

```
                  ┌──────────────────────────── ICloudBackup.exe (per-user tray app) ────────────────────────────┐
                  │                                                                                            │
 iCloud for       │  FileSystemWatcher ──┐                                                                     │
 Windows ──sync──►│  (per job)           │ debounce (FileSettleSeconds)                                        │
 (local folders)  │                      ▼                                                                     │
                  │  Scheduler (2 s tick) ──► Work queue (dedup) ──► N copy workers ──► NAS (SMB)              │
                  │   • hourly sync            ▲                       │  hydrate → copy → verify → swap        │
                  │   • retry due failures ────┘                       ▼                                       │
                  │   • NAS / iCloud / disk checks              SQLite state.db                                │
                  │   • integrity schedule                             │                                       │
                  │   • daily summary                                  ▼                                       │
                  │                                             DiscordNotifier ──► Discord webhook            │
                  │  WinForms UI (tray + window) reads engine status every 1–2 s                               │
                  └────────────────────────────────────────────────────────────────────────────────────────────┘
```

| Class | File | Responsibility |
|---|---|---|
| `Program` | `Program.cs` | Single-instance check, loads config, opens the database, starts the engine and the tray. |
| `BackupEngine` | `Core/BackupEngine.cs` | The service: watchers, queue, workers, scheduler, sync, retries, integrity check, health checks, daily summary. |
| `FileOps` | `Core/FileOps.cs` | Folder walking, SHA-256 hashing, verified copy, versioning, filename case fix, reachability with timeout. |
| `CloudFiles` | `Core/CloudFiles.cs` | P/Invoke into `cldapi.dll`: download a placeholder (`CfHydratePlaceholder`) and pin a folder (`CfSetPinState`), with `attrib +P` as a fallback. |
| `ICloudManager` | `Core/ICloudManager.cs` | Kills and relaunches the iCloud processes. Finds launchable iCloud apps with `Get-StartApps`. |
| `DiscordNotifier` | `Core/DiscordNotifier.cs` | Batched and immediate webhook messages, rate-limit handling, message splitting. |
| `StateDb` | `Core/StateDb.cs` | SQLite record of every file: size, mtime, hash, status, retry state. |
| `AppConfig` / `ConfigStore` / `Secret` | `Core/Config.cs` | Settings model, JSON load/save, DPAPI encryption of the webhook. |
| `Log` / `AppPaths` | `Core/Log.cs` | Daily log files and the in-memory ring buffer for the UI. |
| `TrayContext`, `MainForm`, `JobEditForm` | `UI/` | Tray icon and menu, main window, job editor, start-with-Windows registry entry. |

### Lifecycle

1. **Start** (`Program.Main`):
   1. Takes the single-instance mutex and creates `%LOCALAPPDATA%\ICloudBackup`.
   2. Loads `config.json`, or the defaults if it doesn't exist (first run).
   3. Opens `state.db` and creates the engine and the tray icon.
   4. On first run, opens Settings and **does not start the engine**. Otherwise starts the engine and sends a "started" Discord message.
2. **Engine start** (`BackupEngine.Start`):
   1. Creates a FileSystemWatcher for each enabled job.
   2. Starts `CopyThreads` worker tasks and the scheduler task.
   3. Schedules a sync to run immediately (reason: `startup`).
3. **Saving settings** saves `config.json`, updates the start-with-Windows registry entry, then **stops and restarts the engine** with the new config. Copies in progress are cancelled; their partial files are deleted and the files are retried.
4. **Exit** (tray menu, **Exit**):
   1. Sends a "stopped" message.
   2. Stops the engine (waits up to 30 s for workers).
   3. Flushes queued Discord messages and exits.

   Closing the window with the **X** only hides it.

### The copy pipeline (per file)

Each queued file goes through `BackupEngine.ProcessAsync`:

1. **Skip check.** If the database has the file as `Ok` with the same size and modified time (and the item isn't forced), there's nothing to do.
2. **NAS check.** If the job's NAS is marked offline, the file is recorded as failed without counting an attempt. It's picked up again when the NAS comes back.
3. **Hydrate.** If the file is still a cloud placeholder (it has the `RECALL_ON_DATA_ACCESS`, `RECALL_ON_OPEN` or `OFFLINE` attribute), the worker calls `CfHydratePlaceholder` and waits up to `HydrationTimeoutMinutes` for iCloud to download it.
4. **Adopt an existing copy.** If the database has no hash for this file, or the item is forced, and a same-size file already exists on the NAS, both files are hashed.
   - If the hashes match, the NAS file is **adopted**: no copy, filename case fixed, mtime set, recorded as `Ok`.
   - This is how an existing backup (e.g. from older scripts) is taken over without copying everything again.
5. **Copy.**
   1. Streams the source to `<dest>.icbpartial`, computing SHA-256 on the way.
   2. Flushes to disk.
   3. Checks the source size hasn't changed during the copy.
6. **Verify.** Re-reads `<dest>.icbpartial` from the NAS and hashes it. If it doesn't match the source hash, it throws `IntegrityException` and the file is retried.
7. **Version.** If `<dest>` already exists with different content and `KeepVersions` is on, the old file is moved to `<NAS root>\.versions\<rel dir>\<name>.<yyyyMMdd-HHmmss><ext>`.
8. **Swap.**
   1. Moves `<dest>.icbpartial` to `<dest>` (overwriting).
   2. Restores the iCloud filename casing.
   3. Sets the NAS file's last-write time to the source's.
9. **Record.** Writes `status=Ok`, size, mtime and hash to the database. Queues a "copied" Discord event and writes an `OK` log line.

If any step throws, `HandleFailureAsync` takes over (see [Retries](#retries)).

The NAS folder tree mirrors the iCloud folder tree, relative to each job's root, with original filename casing.

### Getting files out of iCloud (hydration and pinning)

iCloud for Windows uses **Cloud Files placeholders**: a file can appear in Explorer while its data is still in iCloud. Copying such a file can fail or time out, so the app handles it in two layers:

1. **Pinning (whole folder).** When a job has **Keep files on this device** on, each sync checks whether any file is unpinned or still a placeholder. If so, it calls `CfSetPinState(PINNED, RECURSE)` on the job's root folder. This is the same as right-clicking the folder and choosing *Always keep on this device*.
   - iCloud then downloads everything in the background and keeps it downloaded. New files inherit the pin.
   - If the API call is refused, it falls back to `attrib +P -U /S /D "<folder>\*"`.
2. **Per-file download (just in time).** Before copying, any file still a placeholder is downloaded explicitly with `CfHydratePlaceholder`, with a timeout.
   - If that API returns an error, the copy's own read still triggers the download as a fallback.
   - If iCloud doesn't deliver the file within the timeout, the file goes to retry.

### Change detection

There are three layers, from fastest to most thorough:

| Layer | When | What it catches |
|---|---|---|
| FileSystemWatcher | Continuous | Created, changed and renamed files (queued after `FileSettleSeconds` of quiet). A new or renamed **folder** triggers a rescan of that job. A watcher buffer overflow also triggers a rescan. |
| Sync scan | Every `SyncIntervalMinutes` and on startup | Any file whose size or mtime differs from the database, any file not in the database, and files whose NAS copy is missing or the wrong size. |
| Integrity check | Every `IntegrityCheckIntervalDays` or on demand | Silent corruption: compares the actual SHA-256 hashes on both sides. |

A file counts as changed when its **size or last-write time** differs from what was recorded at its last successful backup.

### Hourly sync

`RunSyncAsync` runs on startup, every `SyncIntervalMinutes` (minimum 5), on **Sync now**, and when the scheduler asks for one. Only one sync or rescan runs at a time.

1. **Restart iCloud** (if `RestartICloudOnSync`):
   1. Pauses the workers from taking new files.
   2. Waits up to 10 minutes for copies in progress to finish.
   3. Kills the processes in `ICloudProcessNames`, waits 5 s, and relaunches any that didn't restart on their own via `explorer.exe shell:AppsFolder\<AppID>` (found with `Get-StartApps`).
   4. Resumes the workers and waits `ICloudSettleSeconds`.

   If restarting is off, it only launches iCloud when nothing is running.
2. **For each enabled job** (`ScanJobAsync`):
   1. Walks the iCloud folder. It descends into every folder (including placeholder folders) and never skips hidden or system files, except names matching `ExcludePatterns`.
   2. Queues every file that is new, changed, previously deleted, or failed and past its retry time.
   3. Records files that have disappeared from iCloud as `Deleted`. This is skipped when the safety rail trips; see [Deletions and safety rails](#deletions-and-safety-rails).
   4. Walks the NAS folder, skipping the `.versions` folder and `*.icbpartial` files. Every unchanged `Ok` file whose NAS copy is missing or a different size is queued with **force**.
   5. Pins the folder if anything was unpinned or still a placeholder.
3. Records `last_sync` and schedules the next sync. Sends a Discord summary if `NotifySyncSummary` is on, or if anything was deleted or missing on the NAS.

### Retries

When a file fails (`HandleFailureAsync`):

- **File gone:** if the source file has disappeared, it's ignored.
- **NAS unreachable:** the job's NAS is marked offline and the file is recorded as `Failed` **without** counting an attempt and with no retry time. The resync that runs when the NAS comes back picks it up.
- **Anything else:**
  - The attempt count goes up by one.
  - The next retry is set **min(2^(attempts-1) minutes, RetryMaxDelayMinutes)** away: 1, 2, 4, 8, 16, 32, 64, 128, 256, then 360 min…
  - The error is stored.
  - A failure event goes into the next batched Discord message.
- **Alert:** when the attempt count reaches exactly `RetryAlertAfterAttempts`, a separate alert is sent immediately.
- **No limit:** a file keeps retrying forever at the maximum interval until it succeeds.

The scheduler queues failed files that are due every 30 s. **Retry selected / Retry all** in the UI sets their retry time to now and queues them immediately.

### Integrity check

Runs on demand (**Run integrity check**) or automatically: during the hour `IntegrityCheckHour`, once at least `IntegrityCheckIntervalDays` (minus 2 h of slack) have passed since the last check started. The very first scheduled check is measured from the app's first start.

For each enabled job, with `CopyThreads` files in parallel, each iCloud file gets one of these results:

| Result | Condition | Action |
|---|---|---|
| **Outdated** | The database shows the file changed since its last backup, or it isn't `Ok` | Queued normally. Not counted as corruption. |
| **Missing** | No file on the NAS | Queued with force |
| **OK** | SHA-256 of the iCloud file (re-hashed if `IntegrityRehashSource`, otherwise the stored hash) equals the SHA-256 of the NAS file | Recorded as `Ok` and verified |
| **Mismatch** | Hashes differ | Queued with force. The bad NAS copy is moved to `.versions` before being replaced. |
| **Error** | Exception (e.g. download timeout) | Counted and reported |

It also counts **files that exist only on the NAS** (deleted from iCloud, kept as backup). These are reported only, never removed.

When it finishes:
- A CSV of every problem is written to `%LOCALAPPDATA%\ICloudBackup\reports\integrity-<timestamp>.csv`.
- `last_integrity` and `last_integrity_result` are stored in the database.
- A pass or fail message goes to Discord.

The check can be cancelled from the UI or tray. With source re-hashing on, it reads the whole library twice: once locally and once over the network.

### Health monitoring

The scheduler ticks every 2 s:

| Check | Interval | Behaviour |
|---|---|---|
| NAS reachable | 30 s | `Directory.Exists(dest)` with a 15 s timeout. If the folder is missing but the drive or share root is reachable, the folder is created. Copying pauses while offline. Discord is told when the NAS goes offline and when it comes back; coming back also triggers a rescan. |
| Watcher events | 2 s | Queues debounced files and runs pending job rescans. |
| Due retries | 30 s | Queues failed files whose retry time has passed. |
| iCloud watchdog | 60 s | Any process in `ICloudProcessNames` that was seen running and has now stopped is relaunched, and Discord is told. Skipped while a sync is deliberately restarting iCloud. |
| Disk space | 10 min | Alerts once when free space on a source drive falls below `LowDiskSpaceGb`. The alert re-arms once free space is back above 110 % of the threshold. |
| Sync due | each tick | Starts the hourly sync |
| Integrity due | each tick | See above |
| Daily summary | each tick | Once per day during hour `DailySummaryHour` |

### Deletions and safety rails

- **Nothing is ever deleted from the NAS.** A file removed from iCloud is only marked `Deleted` in the database. Its NAS copy stays.
- **Empty-source guard:** a scan is "suspicious" if it finds 0 files when files were previously backed up, or fewer than half of the previously backed-up count (when there were more than 100). The usual cause is iCloud being signed out or rebuilding. In that case deletion tracking is skipped for that scan and an alert is sent.
- **Empty-NAS guard:** if the NAS folder lists as empty while more than 100 files are known, the NAS size check is skipped. This avoids re-copying everything because of a share glitch.
- **Atomic replace:** a file on the NAS is only replaced after the new copy has been verified. An interrupted copy leaves a `*.icbpartial` file, which is overwritten on retry and ignored by every scan.
- **Previous versions** of changed files go to `.versions\` (configurable). They are **never cleaned up automatically**, so prune them manually if space matters.

### Discord notifications

Messages are sent to one webhook as user `iCloud Backup`, with mentions disabled (`allowed_mentions.parse=[]`) so filenames can't ping anyone. Messages over 1,900 characters are split. On HTTP 429 the sender waits for Discord's `retry_after` time and tries again, up to 4 attempts in total.

| Type | Setting | Timing | Example |
|---|---|---|---|
| Files copied | `NotifyCopies` | Batched every `DiscordBatchSeconds` | ✅ copied 12 file(s), 1.2 GB … (lists up to 15) |
| Files adopted | `NotifyCopies` | Batched | ☑️ Verified 340 file(s) already on the NAS |
| Copy failures | `NotifyFailures` | Batched | ⚠️ 2 file(s) failed (will retry automatically) |
| Repeated failure | `NotifyRetryAlerts` | Immediate | 🚨 X has failed 5 times in a row |
| iCloud stopped/relaunched, source folder missing | `NotifyICloudEvents` | Immediate | 🔄 iCloud had stopped. Relaunched it. |
| NAS offline/online | `NotifyNasEvents` | Immediate | 🔴 NAS not reachable … / 🟢 reachable again |
| Integrity start/result | `NotifyIntegrity` | Immediate | 🔍 started / ✅ passed / ❌ found problems |
| Sync summary | `NotifySyncSummary` (or automatic on deletions/missing files) | After each sync | 🔄 sync complete … |
| App started/stopped | `NotifyStartup` | Immediate | ▶️ started on HOSTNAME |
| Daily summary | `NotifyDailySummary` | Hour `DailySummaryHour` | 📊 per-job totals, last sync, last integrity check, free space |
| Empty-source guard, low disk, sync failure | always on | Immediate | 🚨 / 💾 / ⚠️ |

The webhook URL is stored **encrypted with Windows DPAPI** (CurrentUser scope), so it only decrypts for the same Windows user on the same machine.

---

## Files and data locations

Everything lives in `%LOCALAPPDATA%\ICloudBackup\` (e.g. `C:\Users\<user>\AppData\Local\ICloudBackup\`):

| Path | Contents |
|---|---|
| `config.json` | All settings (see below). Written atomically. A corrupt file is renamed `config.json.corrupt` and defaults are used. |
| `state.db` (+ `-wal`, `-shm`) | SQLite state database |
| `logs\icloudbackup-YYYY-MM-DD.log` | Daily log, kept 30 days. Format: `[yyyy-MM-dd HH:mm:ss][LEVEL] message` |
| `reports\integrity-YYYYMMDD-HHMMSS.csv` | Integrity check problem lists (`Job,File,Issue`) |

Other things the app touches:

| Location | What |
|---|---|
| `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\ICloudBackup` | `"<path to exe>" --minimized` when **Start with Windows** is on |
| Each job's iCloud folder | Read only, apart from the pin attribute (`Always keep on this device`) |
| Each job's NAS folder | Mirrored files, `<file>.icbpartial` temp files, `.versions\` |

---

## Configuration reference

All of these are editable in the **Settings** tab, which is the recommended way. `config.json` can also be edited while the app is **not running**.

### Backup jobs (`Jobs[]`)

| Key | Default | Meaning |
|---|---|---|
| `Id` | random GUID | Stable key in the state database. Renaming a job or changing its paths keeps its history. |
| `Name` | — | Display name |
| `SourcePath` | `%USERPROFILE%\iCloudDrive` and `%USERPROFILE%\Pictures\iCloud Photos\Photos` | iCloud folder to back up |
| `DestinationPath` | `\\nas\backup\iCloud Drive` and `\\nas\backup\iCloud Photos` (placeholders, **change these**) | NAS folder |
| `Enabled` | `true` | |
| `KeepFilesLocal` | `true` | Pin the folder so iCloud keeps everything downloaded |
| `ExcludePatterns` | `desktop.ini`, `thumbs.db`, `~$*`, `*.tmp`, `.ds_store`, `*.icbpartial` | File-name wildcards (`*`, `?`, case-insensitive) that are never backed up |

### Engine

| Key | Default | Meaning |
|---|---|---|
| `CopyThreads` | 4 | Parallel copy workers (1–32). Also the parallelism of the integrity check. |
| `HydrationTimeoutMinutes` | 15 | Maximum wait for iCloud to download one file |
| `FileSettleSeconds` | 15 | Quiet period after a watcher event before the file is queued |
| `KeepVersions` | true | Move the previous NAS copy to `.versions` when a file changes |
| `VersionsFolderName` | `.versions` | Folder at each NAS job root. Ignored by all scans. |
| `RetryMaxDelayMinutes` | 360 | Upper limit on the retry back-off |
| `RetryAlertAfterAttempts` | 5 | Send the "keeps failing" alert at this attempt |
| `SyncIntervalMinutes` | 60 | Full sync interval (minimum 5) |
| `RestartICloudOnSync` | true | Restart iCloud at each sync to force it to pick up changes |
| `ICloudSettleSeconds` | 30 | Wait after restarting iCloud |
| `ICloudWatchdog` | true | Relaunch iCloud processes that stop |
| `ICloudProcessNames` | `iCloudDrive`, `iCloudPhotos` | Process names (without `.exe`) to restart and watch |
| `IntegrityCheckIntervalDays` | 7 | 0 turns off automatic checks |
| `IntegrityCheckHour` | 3 | Local hour (0–23) when an automatic check may start |
| `IntegrityRehashSource` | true | Re-hash iCloud files during the check. If false, the stored hash is trusted for unchanged files. |
| `LowDiskSpaceGb` | 20 | Free-space alert threshold for source drives (0 = off) |

### Discord and app

| Key | Default | Meaning |
|---|---|---|
| `DiscordWebhookProtected` | null | DPAPI-encrypted webhook URL (Base64). Set it through the UI. |
| `DiscordBatchSeconds` | 60 | Batch window for copy and failure messages (minimum 10) |
| `NotifyCopies`, `NotifyFailures`, `NotifyRetryAlerts`, `NotifyICloudEvents`, `NotifyNasEvents`, `NotifyIntegrity`, `NotifyStartup`, `NotifyDailySummary` | true | See [Discord notifications](#discord-notifications) |
| `NotifySyncSummary` | false | Message after every sync, even when nothing changed |
| `DailySummaryHour` | 9 | Local hour for the daily summary |
| `StartWithWindows` | true | Manages the HKCU Run entry |
| `StartMinimized` | true | Start with only the tray icon showing |

---

## State database

`state.db` is SQLite in WAL mode. All access goes through one connection, serialized by a lock.

```sql
CREATE TABLE files(
  job          TEXT NOT NULL,              -- BackupJob.Id
  rel          TEXT NOT NULL COLLATE NOCASE, -- path relative to the job's source root
  size         INTEGER NOT NULL DEFAULT 0, -- bytes, as seen in iCloud
  mtime        INTEGER NOT NULL DEFAULT 0, -- source LastWriteTimeUtc in .NET ticks
  hash         TEXT,                       -- SHA-256 (hex) of the verified NAS copy
  status       INTEGER NOT NULL DEFAULT 0, -- 0 Pending, 1 Ok, 2 Failed, 3 Deleted
  attempts     INTEGER NOT NULL DEFAULT 0, -- consecutive failed attempts
  next_attempt INTEGER,                    -- unix ms; NULL = retry on next sync
  last_error   TEXT,
  backed_up_at INTEGER,                    -- unix ms of last actual copy (or first adoption)
  verified_at  INTEGER,                    -- unix ms of last hash verification
  PRIMARY KEY(job, rel)
);
CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT);
```

`meta` keys: `installed`, `last_sync`, `last_integrity_started`, `last_integrity`, `last_integrity_result`, `last_daily` (`yyyy-MM-dd`), `last_scan_files:<jobId>`.

Useful queries (with the app stopped, or read-only):

```sql
-- what's failing and why
SELECT job, rel, attempts, last_error FROM files WHERE status = 2 ORDER BY attempts DESC;
-- totals per job
SELECT job, status, COUNT(*), SUM(size) FROM files GROUP BY job, status;
-- copied in the last 24 h
SELECT rel, size FROM files WHERE status = 1 AND backed_up_at > (strftime('%s','now') - 86400) * 1000;
```

Deleting `state.db` is safe: on the next start every file is re-checked by hash and adopted from the NAS. That takes a full read of both sides.

---

## User interface

**Tray icon** (cloud shape; colour shows status): green = idle, blue = copying or checking, grey = paused or not started, red = a NAS is offline. Hover for the current activity and queue length.
- **Double-click** opens the window.
- **Right-click** menu: Open, Sync now, Pause/Resume, Run/Cancel integrity check, Exit.

**Window tabs:**

| Tab | Contents |
|---|---|
| Status | Current activity, queue length, totals for this session, last and next sync, last integrity result. Buttons: Sync now, Pause, Run integrity check, Retry all failed, Send summary to Discord, Open logs, Open integrity reports. Per-job table (NAS online, files backed up, failing). Live table of files being worked on, with stage and percentage. |
| Activity | Live log (last 2,000 non-debug lines), coloured by level, with auto-scroll |
| Failed files | Every file in `Failed` state with attempts, next retry and last error. Retry selected / Retry all. |
| Settings | Jobs (add, edit, remove) and every option in the [configuration reference](#configuration-reference). **Send test** for the webhook. **Save and apply** restarts the engine. **Undo changes** reloads the saved settings. |

**Pause** is not remembered: restarting the app resumes backups.

---

## Operations runbook

**Is it running?** Look for `ICloudBackup.exe` in Task Manager, or the tray icon. Today's log should have lines at least hourly (`Sync started`, `scanned N files`).

**Install or move:** see [Quick start](#quick-start). After moving the exe, run it from its new location and click **Save and apply** once, so the start-with-Windows entry points at the new path.

**Upgrade:**
1. Tray → **Exit**.
2. Replace `ICloudBackup.exe` with the new build.
3. Start it.

Settings and state are kept, and database schema changes are applied automatically (`CREATE … IF NOT EXISTS`).

**Uninstall:**
1. Tray → **Exit**.
2. Untick **Start with Windows** first, or delete the `HKCU\…\Run\ICloudBackup` registry value.
3. Delete the exe and `%LOCALAPPDATA%\ICloudBackup`.
4. Optionally unpin the iCloud folders: right-click → *Free up space*, or `attrib -P +U /S /D "<folder>\*"`.

**Force a full re-verification:** Status → **Run integrity check**.

**Start state from scratch:**
1. Exit.
2. Delete `state.db`, `state.db-wal` and `state.db-shm`.
3. Start the app.

**Change the NAS location:**
1. Copy the existing backup to the new location (optional but much faster).
2. Edit each job's NAS folder in Settings and click **Save and apply**.

Files already present are adopted by hash. Anything missing is copied.

**Restore files:** the NAS folder is a plain mirror of the iCloud folder structure. Copy files back directly. Older versions are under `.versions\` with a timestamp suffix.

**Move to another machine or Windows user:** copy `config.json` and `state.db` across, then re-enter the Discord webhook. DPAPI-encrypted values don't move between users or machines.

---

## Troubleshooting

| Symptom / log line | Cause | Fix |
|---|---|---|
| `source folder not found` | iCloud not signed in, a feature turned off, or the path changed | Check iCloud for Windows and the job's iCloud folder |
| `iCloud did not finish downloading the file within N min` | iCloud slow, stuck or signed out | Retries happen automatically. Check that iCloud is running. Raise `HydrationTimeoutMinutes` for very large videos. |
| `CfHydratePlaceholder returned 0x…` (debug) | The API refused; the read fallback is used | Usually harmless |
| `Cloud Files pin API returned 0x… - falling back to attrib +P` then `attrib +P exited …` | iCloud doesn't accept pinning for that folder | Set *Always keep on this device* manually in Explorer, or turn **Keep files local** off (per-file download still works) |
| `NAS destination not reachable` | Share down, credentials, or drive not mapped | Fix the share. The app resumes and rescans by itself. Prefer UNC paths over mapped drives. |
| `NAS copy failed verification` | Network or NAS corruption during the write | Retried automatically. If it keeps happening, check the NAS disks and network. |
| `only N files found but M were backed up before` | iCloud signed out or re-indexing | Fix iCloud. Nothing was removed. |
| `No iCloud apps found in the Start menu` | iCloud isn't a Store install, or `Get-StartApps` is unavailable | Relaunch iCloud manually, or set `RestartICloudOnSync` to false |
| `iCloud (...) has stopped and could not be relaunched` | As above | As above |
| Lots of `Source file changed size while it was being copied` | A file is being written while it's copied | Raise `FileSettleSeconds` |
| Windows shows "*ICloudBackup is downloading files*" or a blocked-app prompt | Windows' per-app download control for cloud files | Allow it, or re-enable it under Settings → Privacy → Automatic file downloads |
| `Could not decrypt stored Discord webhook` | Config moved to another user or machine | Re-enter the webhook in Settings |
| App won't start: "already running" | Another instance is in the tray | Use that one, or end it in Task Manager |
| Nothing after a reboot | No auto sign-in, or the exe was moved | Set up auto-logon. Re-save settings from the exe's current location. |

To see more detail, open the log file. Debug-level lines are written to the file but not shown in the Activity tab.

---

## Known limitations

- **Needs an interactive session.** It doesn't run as a service because iCloud for Windows doesn't sync without a signed-in user.
- **Deletions are never mirrored**, and `.versions\` is never pruned. This is by design for a cold backup, so plan NAS capacity accordingly.
- **Renamed or moved files in iCloud** are treated as a new file plus a deleted file, so the NAS keeps both paths.
- **Folder name casing**: filename casing is corrected on the NAS, but folder casing is left as created. Windows paths are case-insensitive.
- **Change detection uses size and mtime.** An in-place edit that keeps both identical isn't seen until the next integrity check.
- **Hashing reads over SMB** may be served from the Windows client cache, so the verify step mainly proves the data reached the SMB client and server intact. The scheduled integrity check is the deeper check.
- **iCloud restart heuristics**: relaunching uses the Start-menu (Store) app entries that match the names in `ICloudProcessNames`. Non-Store installs may need `RestartICloudOnSync = false`.
- **The exe is unsigned**, so SmartScreen will warn on first run.

---

## Building and releasing

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). Building works on Windows, macOS or Linux (`EnableWindowsTargeting` is set); running needs Windows.

```bash
dotnet publish src/ICloudBackup -c Release -o publish
```

This produces `publish/ICloudBackup.exe`: self-contained, single file, compressed, win-x64, about 53 MB. The build sets `DebugType=none` and a `PathMap`, so no build-machine paths are embedded.

To release, bump `<Version>` in `src/ICloudBackup/ICloudBackup.csproj`, publish, then:

```bash
gh release create vX.Y.Z publish/ICloudBackup.exe --title "vX.Y.Z" --notes "..."
```

Dependencies: `Microsoft.Data.Sqlite` (bundled native SQLite) and `System.Security.Cryptography.ProtectedData` (DPAPI).

---

## Source layout

```
src/ICloudBackup/
├── ICloudBackup.csproj      build settings (self-contained single-file win-x64)
├── app.manifest             asInvoker, long-path aware, Win10+
├── Program.cs               entry point
├── Core/
│   ├── BackupEngine.cs      watchers, queue, workers, scheduler, sync, retries, integrity, health
│   ├── CloudFiles.cs        Cloud Files API: hydrate + pin
│   ├── Config.cs            AppConfig, BackupJob, ConfigStore, DPAPI Secret
│   ├── DiscordNotifier.cs   webhook batching, rate limiting
│   ├── FileOps.cs           walk, hash, verified copy, versions, case fix
│   ├── ICloudManager.cs     restart / relaunch iCloud
│   ├── Log.cs               AppPaths + logger
│   └── StateDb.cs           SQLite state
└── UI/
    ├── MainForm.cs          Status / Activity / Failed files / Settings
    ├── JobEditForm.cs       add/edit job dialog
    └── TrayContext.cs       tray icon + menu, status icons, start-with-Windows
```
