using System.Text;
using VoiceClock.Core.Diagnostics;
using VoiceClock.Core.Tests.TestSupport;

namespace VoiceClock.Core.Tests.Diagnostics;

public class FileAppLogTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly string _path;

    public FileAppLogTests()
    {
        _path = Path.Combine(_temp.Path, "logs", "app.log");
    }

    public void Dispose() => _temp.Dispose();

    private async Task<string> ReadAsync(FileAppLog log)
    {
        using var memory = new MemoryStream();
        await log.CopyToAsync(memory);
        return Encoding.UTF8.GetString(memory.ToArray());
    }

    [Fact]
    public async Task Warnings_and_crashes_are_kept_but_info_is_not()
    {
        var log = new FileAppLog(_path);
        Assert.False(log.HasEntries);

        log.Info("started");
        Assert.False(log.HasEntries);

        log.Warn("clip damaged", new InvalidDataException("bad header"));
        log.Crash(new InvalidOperationException("boom"));

        var text = await ReadAsync(log);
        Assert.True(log.HasEntries);
        Assert.DoesNotContain("started", text);
        Assert.Contains("WARN clip damaged", text);
        Assert.Contains("bad header", text);
        Assert.Contains("CRASH Unhandled exception", text);
        Assert.Contains("boom", text);
    }

    [Fact]
    public async Task Entries_survive_a_restart()
    {
        new FileAppLog(_path).Warn("before restart");
        Assert.Contains("before restart", await ReadAsync(new FileAppLog(_path)));
    }

    [Fact]
    public void The_log_never_grows_past_its_limit()
    {
        var log = new FileAppLog(_path);
        var line = new string('x', 1000);
        for (var i = 0; i < 600; i++)
        {
            log.Warn($"{i:000} {line}");
        }

        Assert.True(new FileInfo(_path).Length <= FileAppLog.MaxBytes + 2000);
        var text = File.ReadAllText(_path);
        Assert.Contains("599 ", text);           // newest kept
        Assert.DoesNotContain("000 x", text);    // oldest dropped
        Assert.StartsWith("20", text);           // trimmed at a line boundary (timestamp first)
    }

    [Fact]
    public async Task Concurrent_warnings_are_all_written()
    {
        var log = new FileAppLog(_path);
        await Task.WhenAll(Enumerable.Range(0, 50).Select(i => Task.Run(() => log.Warn($"warning {i}"))));

        var text = await ReadAsync(log);
        Assert.All(Enumerable.Range(0, 50), i => Assert.Contains($"warning {i}\n", text.Replace("\r\n", "\n")));
    }

    [Fact]
    public void Messages_are_forwarded_to_the_inner_log()
    {
        var inner = new RecordingLog();
        var log = new FileAppLog(_path, inner);

        log.Info("hello");
        log.Warn("careful");

        Assert.Equal(new[] { "info:hello", "warn:careful" }, inner.Lines.ToArray());
    }

    private sealed class RecordingLog : IAppLog
    {
        public List<string> Lines { get; } = [];

        public void Info(string message) => Lines.Add("info:" + message);

        public void Warn(string message, Exception? exception = null) => Lines.Add("warn:" + message);
    }
}
