using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ICloudBackup.Core;

/// <summary>A file iCloud couldn't (or didn't) download. Messages start with "iCloud download" so they can be recognised later.</summary>
public sealed class CloudDownloadException(string message, int win32Error = 0) : IOException(message)
{
    public int Win32Error { get; } = win32Error;

    /// <summary>iCloud's sync engine isn't connected - relaunching iCloud is the fix.</summary>
    public bool ProviderNotRunning => Win32Error is CloudFiles.ERROR_CLOUD_FILE_PROVIDER_NOT_RUNNING or CloudFiles.ERROR_CLOUD_FILE_PROVIDER_TERMINATED;
}

/// <summary>
/// Windows Cloud Files API (cldapi.dll) - what iCloud for Windows uses for its "online-only" placeholders.
/// Lets us pin folders ("Always keep on this device") and explicitly download individual files.
/// </summary>
public static class CloudFiles
{
    public const string DownloadErrorPrefix = "iCloud download";

    private const int FILE_ATTRIBUTE_OFFLINE = 0x00001000;
    private const int FILE_ATTRIBUTE_PINNED = 0x00080000;
    private const int FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x00040000;
    private const int FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000;

    private const int CF_PIN_STATE_PINNED = 1;
    private const int CF_SET_PIN_FLAG_RECURSE = 0x1;
    private const int CF_HYDRATE_FLAG_NONE = 0;
    private const long CF_EOF = -1;

    public const int ERROR_CLOUD_FILE_PROVIDER_NOT_RUNNING = 362;
    public const int ERROR_NOT_A_CLOUD_FILE = 376;
    public const int ERROR_CLOUD_FILE_PROVIDER_TERMINATED = 404;
    private const int ERROR_OPERATION_ABORTED = 995;

    // Both CfHydratePlaceholder and CfSetPinState require READ_DATA (or WRITE_DAC) access on the handle.
    // FILE_READ_DATA and FILE_LIST_DIRECTORY are the same bit.
    private const uint FILE_READ_DATA = 0x0001;
    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_SHARE_ALL = 0x7;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle handle, IntPtr overlapped);

    [DllImport("cldapi.dll")]
    private static extern int CfHydratePlaceholder(SafeFileHandle fileHandle, long startingOffset, long length,
        int hydrateFlags, IntPtr overlapped);

    [DllImport("cldapi.dll")]
    private static extern int CfSetPinState(SafeFileHandle fileHandle, int pinState, int pinFlags, IntPtr overlapped);

    /// <summary>True when the file's data is (at least partly) still in iCloud rather than on this disk.</summary>
    public static bool IsPlaceholder(FileAttributes attrs)
    {
        var a = (int)attrs;
        return (a & (FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS | FILE_ATTRIBUTE_RECALL_ON_OPEN | FILE_ATTRIBUTE_OFFLINE)) != 0;
    }

    public static bool IsPinned(FileAttributes attrs) => ((int)attrs & FILE_ATTRIBUTE_PINNED) != 0;

    private static string LongPath(string path) =>
        path.StartsWith(@"\\?\") ? path
        : path.StartsWith(@"\\") ? @"\\?\UNC\" + path[2..]
        : @"\\?\" + path;

    /// <summary>Win32 error code from an HRESULT (0x8007xxxx), or the value itself if it already is one.</summary>
    private static int Win32Code(int hr) => (hr & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) ? hr & 0xFFFF : hr;

    public static string Describe(int win32) => $"{new Win32Exception(win32).Message} (error {win32})";

    /// <summary>
    /// Downloads the whole file from iCloud and waits until it's on disk. Uses CfHydratePlaceholder (the same call
    /// Explorer's "Always keep on this device" ends up making); if that is refused for a reason other than iCloud
    /// being down, falls back to reading the file end to end, which also forces the download.
    /// Throws <see cref="CloudDownloadException"/> on failure or timeout.
    /// </summary>
    public static async Task HydrateAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var hr = await HydrateWithApiAsync(path, timeout, ct);

        if (hr != 0)
        {
            var code = Win32Code(hr);
            if (code == ERROR_NOT_A_CLOUD_FILE) return;
            if (code is ERROR_CLOUD_FILE_PROVIDER_NOT_RUNNING or ERROR_CLOUD_FILE_PROVIDER_TERMINATED)
                throw new CloudDownloadException($"{DownloadErrorPrefix} failed - iCloud isn't running or connected: {Describe(code)}", code);

            Log.Warn($"CfHydratePlaceholder refused ({Describe(code)}) for {path} - forcing the download by reading the file");
            var remaining = timeout - started.Elapsed;
            if (remaining < TimeSpan.FromMinutes(1)) remaining = TimeSpan.FromMinutes(1);
            await HydrateByReadingAsync(path, remaining, ct);
        }

        var attrs = File.GetAttributes(path);
        if (IsPlaceholder(attrs))
            throw new CloudDownloadException($"{DownloadErrorPrefix} incomplete - iCloud reported success but the file is still online-only");
    }

    private static async Task<int> HydrateWithApiAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        using var h = CreateFileW(LongPath(path), FILE_READ_DATA | FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            var err = Marshal.GetLastWin32Error();
            throw new CloudDownloadException($"{DownloadErrorPrefix} failed - could not open the file: {Describe(err)}", err);
        }

        var call = Task.Run(() => CfHydratePlaceholder(h, 0, CF_EOF, CF_HYDRATE_FLAG_NONE, IntPtr.Zero), CancellationToken.None);
        try
        {
            return await call.WaitAsync(timeout, ct);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            // Cancel the blocked native call so it doesn't hold a download slot (and the handle) forever.
            CancelIoEx(h, IntPtr.Zero);
            try { await call.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None); } catch { }
            if (ex is OperationCanceledException) throw;
            throw new CloudDownloadException($"{DownloadErrorPrefix} timed out - iCloud did not deliver the file within {timeout.TotalMinutes:0} min");
        }
    }

    private static async Task HydrateByReadingAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                1 << 20, FileOptions.SequentialScan | FileOptions.Asynchronous);
            var buffer = new byte[1 << 20];
            while (await fs.ReadAsync(buffer, cts.Token) > 0) { }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CloudDownloadException($"{DownloadErrorPrefix} timed out - iCloud did not deliver the file within {timeout.TotalMinutes:0} min");
        }
        catch (IOException ex) when (ex is not CloudDownloadException)
        {
            var code = Win32Code(ex.HResult);
            throw new CloudDownloadException($"{DownloadErrorPrefix} failed - {ex.Message}", code);
        }
    }

    /// <summary>
    /// Marks a folder and everything in it as "Always keep on this device" so iCloud downloads it all
    /// and keeps it downloaded. New files added later inherit the pin.
    /// </summary>
    public static async Task<bool> PinFolderAsync(string folder, CancellationToken ct)
    {
        var hr = await Task.Run(() =>
        {
            using var h = CreateFileW(LongPath(folder), FILE_READ_DATA | FILE_READ_ATTRIBUTES, FILE_SHARE_ALL,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (h.IsInvalid) return Marshal.GetHRForLastWin32Error();
            return CfSetPinState(h, CF_PIN_STATE_PINNED, CF_SET_PIN_FLAG_RECURSE, IntPtr.Zero);
        }, ct);

        if (hr == 0) return true;

        Log.Warn($"Cloud Files pin API refused for {folder}: {Describe(Win32Code(hr))} - falling back to attrib +P");
        return await PinWithAttribAsync(folder, ct);
    }

    private static async Task<bool> PinWithAttribAsync(string folder, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("attrib.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("+P");
            psi.ArgumentList.Add("-U");
            psi.ArgumentList.Add("/S");
            psi.ArgumentList.Add("/D");
            psi.ArgumentList.Add(Path.Combine(folder, "*"));
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            var stderr = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMinutes(30), ct);
            if (p.ExitCode != 0)
            {
                Log.Warn($"attrib +P exited {p.ExitCode}: {(await stderr).Trim()} {(await stdout).Trim()}");
                return false;
            }
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Warn($"attrib +P failed for {folder}: {ex.Message}");
            return false;
        }
    }
}
