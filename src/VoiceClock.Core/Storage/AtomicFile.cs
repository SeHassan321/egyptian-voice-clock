using System.Text;

namespace VoiceClock.Core.Storage;

/// <summary>
/// Crash-safe file replacement: data is written to a temporary file in the same directory,
/// flushed to disk, then renamed over the target. An interruption leaves either the old file
/// or the new file, never a half-written one.
/// </summary>
public static class AtomicFile
{
    public const string TempSuffix = ".tmp";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task WriteAllBytesAsync(string path, ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}{TempSuffix}");

        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    public static Task WriteAllTextAsync(string path, string text, CancellationToken cancellationToken = default) =>
        WriteAllBytesAsync(path, Utf8NoBom.GetBytes(text), cancellationToken);

    /// <summary>Removes leftovers of interrupted writes.</summary>
    public static int DeleteTempFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*" + TempSuffix))
        {
            if (TryDelete(file))
            {
                deleted++;
            }
        }

        return deleted;
    }

    public static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return false;
    }
}
