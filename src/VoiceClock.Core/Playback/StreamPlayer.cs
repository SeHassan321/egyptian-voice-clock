using System.Diagnostics;
using VoiceClock.Core.Audio;

namespace VoiceClock.Core.Playback;

/// <summary>
/// A streaming PCM output as <see cref="StreamPlayer"/> sees it (on Android: an AudioTrack in
/// MODE_STREAM). The contract mirrors real device behavior, including its quirks:
/// the mixer may hold back the last partial period until <see cref="Stop"/> is called, and
/// <see cref="PlaybackPosition"/> may read 0 once stopped.
/// </summary>
public interface IPcmStreamSink
{
    /// <summary>Frames the output buffers before the mixer consumes them.</summary>
    int BufferFrames { get; }

    void Play();

    /// <summary>Writes without blocking: returns the frames accepted (0 when the buffer is full) or a negative error code.</summary>
    int Write(short[] samples, int offset, int count);

    /// <summary>Frames consumed so far. Only meaningful before <see cref="Stop"/>.</summary>
    long PlaybackPosition { get; }

    /// <summary>End of stream: everything already written still plays.</summary>
    void Stop();

    /// <summary>Stops immediately, discarding what has not been played.</summary>
    void Abort();
}

/// <summary>Monotonic time and delays, replaceable in tests.</summary>
public interface IPlaybackClock
{
    TimeSpan Elapsed { get; }

    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemPlaybackClock : IPlaybackClock
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    public TimeSpan Elapsed => _stopwatch.Elapsed;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

public sealed record StreamPlaybackOptions
{
    /// <summary>Longest wait for the output to start consuming (Bluetooth outputs can take a second or two to wake up).</summary>
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(4);

    /// <summary>Longest time the output may stop consuming mid-way before playback counts as failed.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <summary>Extra wait after the buffered audio has drained, for the output's own latency.</summary>
    public TimeSpan DrainMargin { get; init; } = TimeSpan.FromMilliseconds(150);

    public int ChunkFrames { get; init; } = 2048;
}

/// <summary>
/// Plays one buffer through a streaming output and reports honestly how it went.
/// <para>
/// Success is not inferred from the playback position at the very end, because that does not
/// work on real phones: the mixer may keep the final partial period until the stream is stopped,
/// and stopping resets the position to 0. Instead (as ExoPlayer does), all audio is written while
/// checking that the output keeps consuming it, then the stream is stopped so the remainder drains,
/// and the remaining buffered time is waited out. Failures are only reported when the output
/// returns an error, never starts, or stops consuming.
/// </para>
/// </summary>
public static class StreamPlayer
{
    public static async Task<PlaybackOutcome> PlayAsync(
        IPcmStreamSink sink,
        PcmAudio audio,
        Func<bool> isInterrupted,
        IPlaybackClock clock,
        StreamPlaybackOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(isInterrupted);
        ArgumentNullException.ThrowIfNull(clock);
        options ??= new StreamPlaybackOptions();

        if (audio.IsEmpty)
        {
            return new PlaybackOutcome(PlaybackOutcomeKind.Failed, "Nothing to play.");
        }

        var samples = audio.Samples;
        var total = samples.Length;
        var offset = 0;
        var started = false;
        var lastPosition = 0L;
        var lastProgress = clock.Elapsed;

        try
        {
            sink.Play();

            while (offset < total)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (isInterrupted())
                {
                    sink.Abort();
                    return new PlaybackOutcome(PlaybackOutcomeKind.Interrupted);
                }

                var written = sink.Write(samples, offset, Math.Min(options.ChunkFrames, total - offset));
                if (written < 0)
                {
                    sink.Abort();
                    return new PlaybackOutcome(PlaybackOutcomeKind.Failed, $"The audio output reported error {written}.");
                }

                offset += written;
                var position = sink.PlaybackPosition;
                var now = clock.Elapsed;
                if (position != lastPosition)
                {
                    started |= position > 0;
                    lastPosition = position;
                    lastProgress = now;
                }
                else if (written > 0)
                {
                    lastProgress = now;
                }

                if (written == 0)
                {
                    // Buffer full: wait for the output to consume, but not forever.
                    var limit = started ? options.StallTimeout : options.StartTimeout;
                    if (now - lastProgress > limit)
                    {
                        sink.Abort();
                        return new PlaybackOutcome(
                            PlaybackOutcomeKind.Failed,
                            started ? $"The audio output stopped at frame {position} of {total}." : "The audio output did not start.");
                    }

                    await clock.DelayAsync(options.PollInterval, cancellationToken).ConfigureAwait(false);
                }
            }

            // Everything is written. Read the position before Stop() (which resets it on Android),
            // then let the buffered remainder drain and wait it out.
            var consumed = Math.Clamp(sink.PlaybackPosition, 0, total);
            var pending = Math.Min(total - consumed, Math.Min(total, Math.Max(sink.BufferFrames, 0)));
            sink.Stop();

            var drainUntil = clock.Elapsed + TimeSpan.FromSeconds((double)pending / audio.SampleRate) + options.DrainMargin;
            while (clock.Elapsed < drainUntil)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (isInterrupted())
                {
                    sink.Abort();
                    return new PlaybackOutcome(PlaybackOutcomeKind.Interrupted);
                }

                var remaining = drainUntil - clock.Elapsed;
                await clock.DelayAsync(remaining < options.PollInterval * 2 ? remaining : options.PollInterval * 2, cancellationToken).ConfigureAwait(false);
            }

            return PlaybackOutcome.Completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            sink.Abort();
            return PlaybackOutcome.Cancelled;
        }
    }
}
