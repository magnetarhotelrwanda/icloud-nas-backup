using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ICloudBackup.Core;

/// <summary>
/// Windows Cloud Files API (cldapi.dll) - what iCloud for Windows uses for its "online-only" placeholders.
/// Lets us pin folders ("Always keep on this device") and explicitly download individual files.
/// </summary>
public static class CloudFiles
{
    private const int FILE_ATTRIBUTE_OFFLINE = 0x00001000;
    private const int FILE_ATTRIBUTE_PINNED = 0x00080000;
    private const int FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x00040000;
    private const int FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x00400000;

    private const int CF_PIN_STATE_PINNED = 1;
    private const int CF_SET_PIN_FLAG_RECURSE = 0x1;
    private const int CF_HYDRATE_FLAG_NONE = 0;

    private const int ERROR_NOT_A_CLOUD_FILE = 0x178;
    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_WRITE_ATTRIBUTES = 0x0100;
    private const uint FILE_SHARE_ALL = 0x7;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

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

    /// <summary>
    /// Blocks until iCloud has downloaded the whole file. Throws on timeout or a hard failure.
    /// If the explicit API call is refused we fall back silently: reading the file during the copy also triggers the download.
    /// </summary>
    public static async Task HydrateAsync(string path, TimeSpan timeout, CancellationToken ct)
    {
        var task = Task.Run(() =>
        {
            using var h = CreateFileW(LongPath(path), FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not open file for download");
            var hr = CfHydratePlaceholder(h, 0, -1, CF_HYDRATE_FLAG_NONE, IntPtr.Zero);
            return hr;
        }, CancellationToken.None);

        int result;
        try
        {
            result = await task.WaitAsync(timeout, ct);
        }
        catch (TimeoutException)
        {
            // The native call keeps running in the background; iCloud may still finish it and the retry will pick it up.
            throw new TimeoutException($"iCloud did not finish downloading the file within {timeout.TotalMinutes:0} min");
        }

        if (result != 0 && (result & 0xFFFF) != ERROR_NOT_A_CLOUD_FILE)
        {
            Log.Debug($"CfHydratePlaceholder returned 0x{result:X8} for {path} - falling back to read-through download");
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
            using var h = CreateFileW(LongPath(folder), FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES, FILE_SHARE_ALL,
                IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
            if (h.IsInvalid) return Marshal.GetHRForLastWin32Error();
            return CfSetPinState(h, CF_PIN_STATE_PINNED, CF_SET_PIN_FLAG_RECURSE, IntPtr.Zero);
        }, ct);

        if (hr == 0) return true;

        Log.Warn($"Cloud Files pin API returned 0x{hr:X8} for {folder} - falling back to attrib +P");
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
