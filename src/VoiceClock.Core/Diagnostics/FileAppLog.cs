using System.Globalization;
using System.Text;

namespace VoiceClock.Core.Diagnostics;

/// <summary>A problem report the owner can send from the setup screen.</summary>
public interface IDiagnosticLog
{
    bool HasEntries { get; }

    Task CopyToAsync(Stream destination, CancellationToken cancellationToken = default);
}

/// <summary>
/// Keeps warnings and crashes in a small private text file (oldest lines dropped beyond
/// <see cref="MaxBytes"/>), so problems on a phone can be diagnosed without a debugger.
/// Informational messages only go to the inner log (logcat).
/// </summary>
public sealed class FileAppLog : IAppLog, IDiagnosticLog
{
    public const long MaxBytes = 256 * 1024;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private readonly string _path;
    private readonly IAppLog? _inner;
    private readonly object _sync = new();

    public FileAppLog(string path, IAppLog? inner = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _inner = inner;
    }

    public bool HasEntries
    {
        get
        {
            lock (_sync)
            {
                return File.Exists(_path) && new FileInfo(_path).Length > 0;
            }
        }
    }

    public void Info(string message) => _inner?.Info(message);

    public void Warn(string message, Exception? exception = null)
    {
        _inner?.Warn(message, exception);
        Append("WARN", message, exception);
    }

    /// <summary>Records an unhandled exception; must never throw.</summary>
    public void Crash(Exception exception)
    {
        _inner?.Warn("Unhandled exception", exception);
        Append("CRASH", "Unhandled exception", exception);
    }

    public async Task CopyToAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        byte[] content;
        lock (_sync)
        {
            content = File.Exists(_path) ? File.ReadAllBytes(_path) : [];
        }

        await destination.WriteAsync(content, cancellationToken).ConfigureAwait(false);
    }

    private void Append(string level, string message, Exception? exception)
    {
        try
        {
            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z {level} {message}{(exception is null ? string.Empty : Environment.NewLine + exception)}{Environment.NewLine}");

            lock (_sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                TrimIfNeeded();
                File.AppendAllText(_path, line, Utf8NoBom);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void TrimIfNeeded()
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length <= MaxBytes)
        {
            return;
        }

        // Keep the newest half, starting at a line boundary.
        var text = File.ReadAllText(_path, Utf8NoBom);
        var keepFrom = text.Length / 2;
        var newline = text.IndexOf('\n', keepFrom);
        File.WriteAllText(_path, newline < 0 ? string.Empty : text[(newline + 1)..], Utf8NoBom);
    }
}
