using System.Diagnostics;
using System.Text.Json;

namespace ICloudBackup.Core;

/// <summary>Restarts iCloud for Windows to force a sync, and relaunches it if it dies.</summary>
public sealed class ICloudManager(Func<AppConfig> config)
{
    private sealed record StartApp(string Name, string AppId);

    private List<StartApp>? _startApps;

    private IEnumerable<string> ProcessNames => config().ICloudProcessNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim());

    public static bool IsRunning(string name) => Process.GetProcessesByName(name).Length > 0;

    /// <summary>True once any iCloud process has been seen this session - so a machine without iCloud isn't nagged.</summary>
    public bool EverSeenRunning { get; private set; }

    public bool AnyRunning()
    {
        var running = ProcessNames.Any(IsRunning);
        if (running) EverSeenRunning = true;
        return running;
    }

    public async Task RestartAsync(CancellationToken ct)
    {
        foreach (var name in ProcessNames)
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

        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        await EnsureRunningAsync(ProcessNames.ToList(), ct);
    }

    /// <summary>Launches any of the given processes that aren't running, via their Start-menu (Store) app entry.</summary>
    public async Task<List<string>> EnsureRunningAsync(List<string> names, CancellationToken ct)
    {
        var launched = new List<string>();
        var missing = names.Where(n => !IsRunning(n)).ToList();
        if (missing.Count == 0) return launched;

        var apps = await GetStartAppsAsync(ct);
        if (apps.Count == 0)
        {
            Log.Warn("No iCloud apps found in the Start menu - relying on iCloud to restart itself.");
            return launched;
        }

        var launchedIds = new HashSet<string>();
        foreach (var name in missing)
        {
            var key = Normalize(name);
            var app = apps.FirstOrDefault(a => Normalize(a.Name).Contains(key))
                      ?? apps.FirstOrDefault(a => key.Contains(Normalize(a.Name)) && Normalize(a.Name).Length > "icloud".Length)
                      ?? apps.FirstOrDefault(a => Normalize(a.Name) == "icloud");
            if (app == null || !launchedIds.Add(app.AppId)) continue;

            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{app.AppId}") { UseShellExecute = false })?.Dispose();
                Log.Info($"Launched {app.Name} ({app.AppId}) for {name}");
                launched.Add(name);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not launch {app.Name}: {ex.Message}");
            }
        }
        return launched;
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
