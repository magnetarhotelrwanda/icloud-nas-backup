using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ICloudBackup.Core;

public sealed class BackupJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string DestinationPath { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>Pin the source folder ("Always keep on this device") so iCloud downloads everything locally.</summary>
    public bool KeepFilesLocal { get; set; } = true;

    public List<string> ExcludePatterns { get; set; } = ["desktop.ini", "thumbs.db", "~$*", "*.tmp", ".ds_store", "*.icbpartial"];

    public BackupJob Clone() => new()
    {
        Id = Id, Name = Name, SourcePath = SourcePath, DestinationPath = DestinationPath,
        Enabled = Enabled, KeepFilesLocal = KeepFilesLocal, ExcludePatterns = [.. ExcludePatterns]
    };

    private Regex[]? _excludeRegex;

    public bool IsExcluded(string fileName)
    {
        _excludeRegex ??= ExcludePatterns
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => new Regex("^" + Regex.Escape(p.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .ToArray();
        return _excludeRegex.Any(r => r.IsMatch(fileName));
    }
}

public sealed class AppConfig
{
    public List<BackupJob> Jobs { get; set; } = [];

    // Copying
    public int CopyThreads { get; set; } = 4;
    public int DownloadThreads { get; set; } = 4;
    public int HydrationTimeoutMinutes { get; set; } = 15;
    public int FileSettleSeconds { get; set; } = 15;
    public bool KeepVersions { get; set; } = true;
    public string VersionsFolderName { get; set; } = ".versions";

    // Retries
    public int RetryMaxDelayMinutes { get; set; } = 360;
    public int RetryAlertAfterAttempts { get; set; } = 5;
    /// <summary>Files waiting on an iCloud download are retried sooner than other failures.</summary>
    public int DownloadRetryMaxMinutes { get; set; } = 30;

    // iCloud
    /// <summary>Full rescan of the iCloud folders and NAS (does not restart iCloud).</summary>
    public int SyncIntervalMinutes { get; set; } = 60;
    /// <summary>Restart iCloud this often to make it pick up changes; 0 = never. Waits until nothing is downloading or copying.</summary>
    public int ICloudRestartIntervalMinutes { get; set; } = 15;
    /// <summary>Skip the scheduled restart when no files are waiting on an iCloud download.</summary>
    public bool ICloudRestartOnlyWhenStuck { get; set; } = false;
    public int ICloudSettleSeconds { get; set; } = 30;
    public bool ICloudWatchdog { get; set; } = true;
    public List<string> ICloudProcessNames { get; set; } = ["iCloudDrive", "iCloudPhotos"];

    // Integrity
    public int IntegrityCheckIntervalDays { get; set; } = 7;
    public int IntegrityCheckHour { get; set; } = 3;
    public bool IntegrityRehashSource { get; set; } = true;

    // Health
    public int LowDiskSpaceGb { get; set; } = 20;

    // Discord
    public string? DiscordWebhookProtected { get; set; }
    public int DiscordBatchSeconds { get; set; } = 60;
    public bool NotifyCopies { get; set; } = true;
    public bool NotifyFailures { get; set; } = true;
    public bool NotifyRetryAlerts { get; set; } = true;
    public bool NotifyICloudEvents { get; set; } = true;
    public bool NotifyNasEvents { get; set; } = true;
    public bool NotifyIntegrity { get; set; } = true;
    public bool NotifySyncSummary { get; set; } = false;
    public bool NotifyStartup { get; set; } = true;
    public bool NotifyDailySummary { get; set; } = true;
    public int DailySummaryHour { get; set; } = 9;

    // App
    public bool StartWithWindows { get; set; } = true;
    public bool StartMinimized { get; set; } = true;

    public string? GetWebhookUrl() => Secret.Unprotect(DiscordWebhookProtected);
    public void SetWebhookUrl(string? url) =>
        DiscordWebhookProtected = string.IsNullOrWhiteSpace(url) ? null : Secret.Protect(url.Trim());

    public static AppConfig CreateDefault()
    {
        // Personal builds (-p:PersonalBuild=true) embed personal/defaults.json, which is git-ignored.
        var personal = LoadEmbeddedDefaults();
        if (personal != null) return personal;

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new AppConfig
        {
            Jobs =
            [
                new BackupJob
                {
                    Name = "iCloud Drive",
                    SourcePath = Path.Combine(profile, "iCloudDrive"),
                    DestinationPath = @"\\nas\backup\iCloud Drive",
                },
                new BackupJob
                {
                    Name = "iCloud Photos",
                    SourcePath = Path.Combine(profile, "Pictures", "iCloud Photos", "Photos"),
                    DestinationPath = @"\\nas\backup\iCloud Photos",
                },
            ]
        };
    }

    private static AppConfig? LoadEmbeddedDefaults()
    {
        try
        {
            using var stream = typeof(AppConfig).Assembly.GetManifestResourceStream("PersonalDefaults.json");
            if (stream == null) return null;
            var cfg = JsonSerializer.Deserialize<AppConfig>(stream);
            return cfg is { Jobs.Count: > 0 } ? cfg : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Embedded personal defaults unreadable - using generic defaults: {ex.Message}");
            return null;
        }
    }

    public AppConfig Clone()
    {
        var json = JsonSerializer.Serialize(this);
        return JsonSerializer.Deserialize<AppConfig>(json)!;
    }
}

public static class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static bool Exists => File.Exists(AppPaths.ConfigPath);

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(AppPaths.ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(AppPaths.ConfigPath), Options);
                if (cfg != null) return cfg;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Config file unreadable - using defaults", ex);
            try { File.Copy(AppPaths.ConfigPath, AppPaths.ConfigPath + ".corrupt", true); } catch { }
        }
        return AppConfig.CreateDefault();
    }

    public static void Save(AppConfig cfg)
    {
        AppPaths.Ensure();
        var tmp = AppPaths.ConfigPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, Options));
        File.Move(tmp, AppPaths.ConfigPath, true);
    }
}

/// <summary>Encrypts secrets with Windows DPAPI so they're only readable by this Windows user.</summary>
public static class Secret
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ICloudBackup.v1");

    public static string Protect(string plain) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    public static string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not decrypt stored Discord webhook (re-enter it in Settings): {ex.Message}");
            return null;
        }
    }
}
