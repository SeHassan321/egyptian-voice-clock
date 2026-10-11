namespace VoiceClock.Core.Diagnostics;

/// <summary>Minimal logging hook; the Android app forwards it to logcat.</summary>
public interface IAppLog
{
    void Info(string message);

    void Warn(string message, Exception? exception = null);
}

public sealed class NullAppLog : IAppLog
{
    public static NullAppLog Instance { get; } = new();

    public void Info(string message)
    {
    }

    public void Warn(string message, Exception? exception = null)
    {
    }
}
