using System.Security.Cryptography;

namespace ICloudBackup.Core;

public sealed class IntegrityException(string message) : IOException(message);

public sealed record SourceFile(string FullPath, string Rel, long Size, long Mtime, FileAttributes Attributes);

public static class FileOps
{
    private const int BufferSize = 1 << 20;
    public const string PartialSuffix = ".icbpartial";

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double v = bytes;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.##} {units[i]}";
    }

    public static async Task<string> HashFileAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[BufferSize];
        int read;
        while ((read = await fs.ReadAsync(buffer, ct)) > 0) hash.AppendData(buffer, 0, read);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>
    /// Walks a folder tree. Written by hand rather than with RecurseSubdirectories so it always descends into
    /// iCloud's placeholder folders (which carry reparse-point attributes) and never skips hidden/system files.
    /// </summary>
    public static IEnumerable<SourceFile> Walk(string root, Func<string, bool> excludeName, string? skipDirName = null)
    {
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
            ReturnSpecialDirectories = false,
        };
        var stack = new Stack<(DirectoryInfo Dir, int Depth)>();
        stack.Push((new DirectoryInfo(root), 0));

        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            IEnumerable<FileSystemInfo> entries;
            try
            {
                entries = dir.EnumerateFileSystemInfos("*", opts).ToList();
            }
            catch (Exception ex)
            {
                Log.Warn($"Cannot list folder {dir.FullName}: {ex.Message}");
                continue;
            }

            foreach (var e in entries)
            {
                if (e is DirectoryInfo d)
                {
                    if (depth >= 64) continue;
                    if (skipDirName != null && depth == 0 && d.Name.Equals(skipDirName, StringComparison.OrdinalIgnoreCase)) continue;
                    stack.Push((d, depth + 1));
                }
                else if (e is FileInfo f)
                {
                    if (excludeName(f.Name)) continue;
                    yield return new SourceFile(f.FullName, Path.GetRelativePath(root, f.FullName), f.Length,
                        f.LastWriteTimeUtc.Ticks, f.Attributes);
                }
            }
        }
    }

    /// <summary>
    /// Copies to a temporary file on the NAS while hashing the source, re-reads the NAS copy to verify the hash,
    /// keeps the previous version if requested, then moves the new copy into place. Returns the SHA-256.
    /// </summary>
    public static async Task<string> CopyVerifiedAsync(SourceFile src, string dest, string? versionsPath, string? previousHash,
        Action<long>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var tmp = dest + PartialSuffix;
        string srcHash;

        try
        {
            await using (var input = new FileStream(src.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                             BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous))
            await using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[BufferSize];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    total += read;
                    progress?.Invoke(total);
                }
                await output.FlushAsync(ct);
                output.Flush(true);
                srcHash = Convert.ToHexString(hash.GetHashAndReset());
            }

            // Only size is checked: the hash we just took is of exactly what was written, and a same-size edit
            // changes the modified time, which the next scan picks up and copies again.
            var after = new FileInfo(src.FullPath);
            if (after.Length != src.Size)
                throw new IOException("Source file changed size while it was being copied - will retry");

            var destHash = await HashFileAsync(tmp, ct);
            if (!string.Equals(srcHash, destHash, StringComparison.OrdinalIgnoreCase))
                throw new IntegrityException($"NAS copy failed verification (source {srcHash[..12]}…, NAS {destHash[..12]}…)");

            if (File.Exists(dest))
            {
                if (versionsPath != null)
                {
                    var existingHash = previousHash ?? await HashFileAsync(dest, ct);
                    if (!string.Equals(existingHash, srcHash, StringComparison.OrdinalIgnoreCase))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(versionsPath)!);
                        File.Move(dest, versionsPath, true);
                    }
                }
            }

            File.Move(tmp, dest, true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }

        FixNameCase(dest);
        TrySetMtime(dest, src.Mtime);
        return srcHash;
    }

    public static string VersionPath(string destRoot, string versionsFolder, string rel)
    {
        var dir = Path.GetDirectoryName(rel) ?? "";
        var name = Path.GetFileNameWithoutExtension(rel);
        var ext = Path.GetExtension(rel);
        return Path.Combine(destRoot, versionsFolder, dir, $"{name}.{DateTime.Now:yyyyMMdd-HHmmss}{ext}");
    }

    /// <summary>
    /// The old PowerShell scripts lower-cased every file name on the NAS. When we touch a file we put the real
    /// iCloud casing back (Windows paths are case-insensitive so the file is still found either way).
    /// </summary>
    public static void FixNameCase(string desiredPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(desiredPath)!;
            var wanted = Path.GetFileName(desiredPath);
            var actual = new DirectoryInfo(dir).EnumerateFiles(wanted).FirstOrDefault();
            if (actual == null || actual.Name == wanted) return;
            var temp = actual.FullName + ".casefix";
            File.Move(actual.FullName, temp);
            File.Move(temp, desiredPath);
        }
        catch (Exception ex)
        {
            Log.Debug($"Could not fix name casing for {desiredPath}: {ex.Message}");
        }
    }

    public static void TrySetMtime(string path, long ticksUtc)
    {
        try { File.SetLastWriteTimeUtc(path, new DateTime(ticksUtc, DateTimeKind.Utc)); } catch { }
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>Directory.Exists on a dead SMB share can hang for a long time - cap it.</summary>
    public static async Task<bool> PathReachableAsync(string path, TimeSpan timeout)
    {
        try
        {
            return await Task.Run(() => Directory.Exists(path)).WaitAsync(timeout);
        }
        catch
        {
            return false;
        }
    }
}
