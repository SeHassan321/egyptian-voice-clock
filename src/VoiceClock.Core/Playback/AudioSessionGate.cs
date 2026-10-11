namespace VoiceClock.Core.Playback;

public enum AudioActivity
{
    None,
    Announcement,
    Preview,
    Recording,
}

/// <summary>
/// Ensures only one audio activity runs at a time: an announcement, a preview, or a recording.
/// A second request is refused immediately instead of queuing or overlapping.
/// </summary>
public sealed class AudioSessionGate
{
    private readonly object _sync = new();
    private AudioActivity _current;
    private long _leaseId;

    public event EventHandler<AudioActivity>? ActivityChanged;

    public AudioActivity Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    /// <summary>Returns a lease, or null when something else is active.</summary>
    public AudioLease? TryAcquire(AudioActivity activity)
    {
        if (activity == AudioActivity.None)
        {
            throw new ArgumentOutOfRangeException(nameof(activity));
        }

        long id;
        lock (_sync)
        {
            if (_current != AudioActivity.None)
            {
                return null;
            }

            _current = activity;
            id = ++_leaseId;
        }

        ActivityChanged?.Invoke(this, activity);
        return new AudioLease(this, id, activity);
    }

    internal void Release(long id)
    {
        lock (_sync)
        {
            if (_leaseId != id || _current == AudioActivity.None)
            {
                return;
            }

            _current = AudioActivity.None;
        }

        ActivityChanged?.Invoke(this, AudioActivity.None);
    }
}

public sealed class AudioLease : IDisposable
{
    private readonly AudioSessionGate _gate;
    private readonly long _id;
    private int _released;

    internal AudioLease(AudioSessionGate gate, long id, AudioActivity activity)
    {
        _gate = gate;
        _id = id;
        Activity = activity;
    }

    public AudioActivity Activity { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _gate.Release(_id);
        }
    }
}
