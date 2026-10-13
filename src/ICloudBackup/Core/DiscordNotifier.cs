using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace ICloudBackup.Core;

/// <summary>
/// Sends Discord webhook messages. Copies and failures are batched (one message per batch window) so a big
/// initial backup doesn't hit Discord's rate limit; alerts go out straight away.
/// </summary>
public sealed class DiscordNotifier : IDisposable
{
    private sealed record CopyEvent(string Job, string Rel, long Size, bool Updated, bool Adopted);
    private sealed record FailEvent(string Job, string Rel, string Error, int Attempt, TimeSpan? RetryIn);

    private const int MaxMessage = 1900;
    private const int MaxListed = 15;

    private readonly Func<AppConfig> _config;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly ConcurrentQueue<CopyEvent> _copies = new();
    private readonly ConcurrentQueue<FailEvent> _failures = new();
    private readonly ConcurrentQueue<string> _immediate = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private DateTime _lastFlush = DateTime.UtcNow;

    public DiscordNotifier(Func<AppConfig> config)
    {
        _config = config;
        _loop = Task.Run(LoopAsync);
    }

    private AppConfig Cfg => _config();

    public void Copied(string job, string rel, long size, bool updated, bool adopted)
    {
        if (Cfg.NotifyCopies) _copies.Enqueue(new CopyEvent(job, rel, size, updated, adopted));
    }

    public void Failed(string job, string rel, string error, int attempt, TimeSpan? retryIn)
    {
        if (Cfg.NotifyFailures) _failures.Enqueue(new FailEvent(job, rel, error, attempt, retryIn));
    }

    /// <summary>Queues a message to send as soon as possible (respecting Discord's rate limit).</summary>
    public void Send(string message) => _immediate.Enqueue(message);

    public static string Code(string s) => "`" + s.Replace("`", "'") + "`";

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, _cts.Token);
                while (_immediate.TryDequeue(out var msg)) await PostAsync(msg, _cts.Token);

                if (DateTime.UtcNow - _lastFlush >= TimeSpan.FromSeconds(Math.Max(10, Cfg.DiscordBatchSeconds)))
                {
                    _lastFlush = DateTime.UtcNow;
                    await FlushBatchesAsync(_cts.Token);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Warn($"Discord loop error: {ex.Message}");
            }
        }
    }

    private async Task FlushBatchesAsync(CancellationToken ct)
    {
        var copies = Drain(_copies);
        if (copies.Count > 0)
        {
            var sb = new StringBuilder();
            var adopted = copies.Count(c => c.Adopted);
            var copied = copies.Where(c => !c.Adopted).ToList();
            if (copied.Count > 0)
            {
                var updated = copied.Count(c => c.Updated);
                var perJob = string.Join(", ", copied.GroupBy(c => c.Job).Select(g => $"{g.Key}: {g.Count()}"));
                sb.AppendLine($":white_check_mark: **iCloud Backup** - copied {copied.Count} file(s), {FileOps.FormatBytes(copied.Sum(c => c.Size))} ({perJob})" +
                              (updated > 0 ? $" - {updated} were changed files (previous version kept)" : ""));
                foreach (var c in copied.Take(MaxListed))
                    sb.AppendLine($"• {c.Job}: {Code(c.Rel)} ({FileOps.FormatBytes(c.Size)}){(c.Updated ? " - updated" : "")}");
                if (copied.Count > MaxListed) sb.AppendLine($"… and {copied.Count - MaxListed} more");
            }
            if (adopted > 0)
                sb.AppendLine($":ballot_box_with_check: Verified {adopted} file(s) already on the NAS (hashes match, no copy needed).");
            await PostAsync(sb.ToString(), ct);
        }

        var fails = Drain(_failures);
        if (fails.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine($":warning: **iCloud Backup** - {fails.Count} file(s) failed (will retry automatically)");
            foreach (var f in fails.Take(MaxListed))
            {
                var retry = f.RetryIn is { } r ? $"retry in {FormatSpan(r)}" : "retry on next sync";
                sb.AppendLine($"• {f.Job}: {Code(f.Rel)} - attempt {f.Attempt}, {retry} - {Truncate(f.Error, 150)}");
            }
            if (fails.Count > MaxListed) sb.AppendLine($"… and {fails.Count - MaxListed} more (see the Failed tab in the app)");
            await PostAsync(sb.ToString(), ct);
        }
    }

    public static string FormatSpan(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{t.TotalHours:0.#} h" : t.TotalMinutes >= 1 ? $"{t.TotalMinutes:0} min" : $"{t.TotalSeconds:0} s";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static List<T> Drain<T>(ConcurrentQueue<T> q)
    {
        var list = new List<T>();
        while (q.TryDequeue(out var item)) list.Add(item);
        return list;
    }

    private async Task PostAsync(string content, CancellationToken ct)
    {
        var url = Cfg.GetWebhookUrl();
        if (string.IsNullOrEmpty(url)) return;
        foreach (var chunk in Split(content))
        {
            var error = await PostChunkAsync(url, chunk, ct);
            if (error != null) Log.Warn($"Discord notification failed: {error}");
        }
    }

    /// <summary>Sends a test message; returns null on success or an error description.</summary>
    public async Task<string?> TestAsync(string url) =>
        await PostChunkAsync(url, ":wave: **iCloud Backup** - test notification. Discord is set up correctly.", CancellationToken.None);

    private async Task<string?> PostChunkAsync(string url, string content, CancellationToken ct)
    {
        var payload = new
        {
            username = "iCloud Backup",
            content,
            allowed_mentions = new { parse = Array.Empty<string>() },
        };
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                using var resp = await _http.PostAsJsonAsync(url, payload, ct);
                if (resp.IsSuccessStatusCode) return null;
                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var wait = TimeSpan.FromSeconds(2);
                    try
                    {
                        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                        if (doc.RootElement.TryGetProperty("retry_after", out var ra)) wait = TimeSpan.FromSeconds(ra.GetDouble() + 0.25);
                    }
                    catch { }
                    await Task.Delay(wait, ct);
                    continue;
                }
                return $"HTTP {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                if (attempt == 4) return ex.Message;
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct);
            }
        }
        return "Rate limited by Discord";
    }

    private static IEnumerable<string> Split(string content)
    {
        if (content.Length <= MaxMessage) { yield return content; yield break; }
        var sb = new StringBuilder();
        foreach (var line in content.Split('\n'))
        {
            var l = line.Length > MaxMessage ? line[..MaxMessage] : line;
            if (sb.Length + l.Length + 1 > MaxMessage)
            {
                yield return sb.ToString();
                sb.Clear();
            }
            sb.Append(l).Append('\n');
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    /// <summary>Sends anything still queued (used on shutdown).</summary>
    public async Task FlushAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (_immediate.TryDequeue(out var msg)) await PostAsync(msg, cts.Token);
            await FlushBatchesAsync(cts.Token);
        }
        catch { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop.Wait(2000); } catch { }
        _http.Dispose();
    }
}
