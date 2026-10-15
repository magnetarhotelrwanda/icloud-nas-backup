using System.Diagnostics;
using System.Text.Json;

namespace ICloudBackup.Core;

/// <summary>Restarts iCloud for Windows to force a sync, and relaunches it if it dies.</summary>
public sealed class ICloudManager(Func<AppConfig> config)
{
    private sealed record StartApp(string Name, string AppId);

    private List<StartApp>? _startApps;

    private IEnumerable<string> ProcessNames => config().ICloudProcessNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim());

    /// <summary>iCloud's main app (iCloudHome). It owns the other processes and stays open when they die.</summary>
    private string? AppProcess => Clean(config().ICloudAppProcessName);

    /// <summary>iCloud's sync engine (iCloudDrive) - provides both the iCloud Drive and iCloud Photos folders.</summary>
    private string? SyncProcess => Clean(config().ICloudSyncProcessName);

    private static string? Clean(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim();

    private IEnumerable<string> AllNames => ProcessNames.Concat(new[] { AppProcess, SyncProcess }.OfType<string>())
        .Distinct(StringComparer.OrdinalIgnoreCase);

    public static bool IsRunning(string name) => Process.GetProcessesByName(name).Length > 0;

    /// <summary>True once any iCloud process has been seen this session - so a machine without iCloud isn't nagged.</summary>
    public bool EverSeenRunning { get; private set; }

    public bool AnyRunning()
    {
        var running = AllNames.Any(IsRunning);
        if (running) EverSeenRunning = true;
        return running;
    }

    /// <summary>
    /// True when iCloud's sync engine is running. Without it nothing new reaches the iCloud folders, even though
    /// the iCloud app itself is still open. Falls back to "any iCloud process" when no sync process is configured.
    /// </summary>
    public bool SyncRunning()
    {
        var any = AnyRunning();
        return SyncProcess is { } sync ? IsRunning(sync) : any;
    }

    public async Task RestartAsync(CancellationToken ct)
    {
        StopAll();
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        await LaunchAsync(ct);
    }

    /// <summary>
    /// Gets iCloud's sync engine running again. If the iCloud app is still open its Start-menu entry only brings
    /// the existing window forward and the sync engine stays dead, so the app is stopped first and launched fresh.
    /// </summary>
    public async Task<bool> EnsureRunningAsync(CancellationToken ct)
    {
        if (SyncRunning()) return true;
        if (AllNames.Any(IsRunning))
        {
            StopAll();
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        return await LaunchAsync(ct);
    }

    private void StopAll()
    {
        foreach (var name in AllNames)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    p.Kill();
                    Log.Info($"Stopped iCloud process {name} (PID {p.Id})");
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not stop {name}: {ex.Message}");
                }
                finally { p.Dispose(); }
            }
        }
    }

    /// <summary>Launches iCloud via its Start-menu (Store) app entry.</summary>
    private async Task<bool> LaunchAsync(CancellationToken ct)
    {
        var apps = await GetStartAppsAsync(ct);
        if (apps.Count == 0)
        {
            Log.Warn("No iCloud apps found in the Start menu - relying on iCloud to restart itself.");
            return false;
        }

        // The main "iCloud" entry starts the app, which starts the sync engine and the other processes.
        var app = apps.FirstOrDefault(a => Normalize(a.Name) == "icloud") ?? apps[0];
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{app.AppId}") { UseShellExecute = false })?.Dispose();
            Log.Info($"Launched {app.Name} ({app.AppId})");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not launch {app.Name}: {ex.Message}");
            return false;
        }
    }

    private static string Normalize(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private async Task<List<StartApp>> GetStartAppsAsync(CancellationToken ct)
    {
        if (_startApps is { Count: > 0 }) return _startApps;
        var result = new List<StartApp>();
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add("Get-StartApps | Where-Object { $_.Name -like '*iCloud*' } | Select-Object Name, AppID | ConvertTo-Json -Compress");
            using var p = Process.Start(psi)!;
            var output = await p.StandardOutput.ReadToEndAsync(ct).WaitAsync(TimeSpan.FromSeconds(60), ct);
            await p.WaitForExitAsync(ct);

            output = output.Trim();
            if (output.Length > 0)
            {
                using var doc = JsonDocument.Parse(output);
                var items = doc.RootElement.ValueKind == JsonValueKind.Array
                    ? doc.RootElement.EnumerateArray().ToList()
                    : [doc.RootElement];
                foreach (var el in items)
                {
                    var name = el.GetProperty("Name").GetString();
                    var id = el.GetProperty("AppID").GetString();
                    if (name != null && id != null) result.Add(new StartApp(name, id));
                }
            }
            Log.Debug($"iCloud Start apps: {string.Join(", ", result.Select(a => a.Name))}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not list iCloud Start apps: {ex.Message}");
        }
        _startApps = result;
        return result;
    }
}
