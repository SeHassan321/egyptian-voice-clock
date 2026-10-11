using VoiceClock.Core.Audio;
using VoiceClock.Core.Playback;
using VoiceClock.Core.Tests.TestSupport;

namespace VoiceClock.Core.Tests.Playback;

/// <summary>Simulated time: delays complete instantly and move the clock (and the simulated output) forward.</summary>
internal sealed class SimulatedClock : IPlaybackClock
{
    public TimeSpan Elapsed { get; private set; }

    public event Action<TimeSpan>? Advanced;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (delay > TimeSpan.Zero)
        {
            Elapsed += delay;
            Advanced?.Invoke(delay);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

/// <summary>
/// A model of an Android AudioTrack in streaming mode, including the behaviors that made earlier
/// builds report failures after the time had been spoken correctly:
/// the mixer consumes whole periods only and keeps the final partial period until Stop(),
/// playback starts only once the buffer is full, and the position reads 0 after Stop().
/// </summary>
internal sealed class SimulatedOutput : IPcmStreamSink
{
    private readonly int _sampleRate;
    private TimeSpan _sincePlay;
    private TimeSpan _mixClock;
    private bool _playing;
    private bool _consuming;

    public SimulatedOutput(SimulatedClock clock, int sampleRate = 44100)
    {
        _sampleRate = sampleRate;
        clock.Advanced += Advance;
    }

    public int BufferFrames { get; init; } = 8820;          // 200 ms

    public int PeriodFrames { get; init; } = 882;           // 20 ms mixer period

    public TimeSpan StartDelay { get; init; }               // e.g. a Bluetooth output waking up

    public bool PositionReadsZeroAfterStop { get; init; } = true;

    public bool PositionAlwaysZero { get; init; }

    public long DiesAfterFrames { get; init; } = long.MaxValue;

    public int? WriteError { get; init; }

    public long Written { get; private set; }

    public long Consumed { get; private set; }

    public bool Stopped { get; private set; }

    public int Aborts { get; private set; }

    public long PlaybackPosition => PositionAlwaysZero || (Stopped && PositionReadsZeroAfterStop) ? 0 : Consumed;

    public void Play() => _playing = true;

    public int Write(short[] samples, int offset, int count)
    {
        if (WriteError is { } error)
        {
            return error;
        }

        var free = BufferFrames - (Written - Consumed);
        var accepted = (int)Math.Min(count, Math.Max(0, free));
        Written += accepted;
        return accepted;
    }

    public void Stop() => Stopped = true;

    public void Abort()
    {
        Aborts++;
        _playing = false;
    }

    private void Advance(TimeSpan delta)
    {
        if (!_playing)
        {
            return;
        }

        _sincePlay += delta;
        if (_sincePlay < StartDelay)
        {
            return;
        }

        _mixClock += delta;
        var period = TimeSpan.FromSeconds((double)PeriodFrames / _sampleRate);
        while (_mixClock >= period)
        {
            _mixClock -= period;
            MixCycle();
        }
    }

    private void MixCycle()
    {
        var available = Written - Consumed;
        if (Consumed >= DiesAfterFrames)
        {
            return;
        }

        if (!_consuming)
        {
            // Playback begins once the buffer has filled up, or when the stream is stopped.
            if (available >= BufferFrames || (Stopped && available > 0))
            {
                _consuming = true;
            }
            else
            {
                return;
            }
        }

        if (available >= PeriodFrames)
        {
            Consumed += PeriodFrames;
        }
        else if (Stopped)
        {
            Consumed += available; // the final partial period only plays after Stop()
        }
    }
}

public class StreamPlayerTests
{
    private static readonly PcmAudio TwoSeconds = Signals.Tone(2000);

    private static async Task<(PlaybackOutcome Outcome, SimulatedOutput Output, SimulatedClock Clock)> RunAsync(
        Func<SimulatedClock, SimulatedOutput> createOutput,
        PcmAudio? audio = null,
        Func<SimulatedClock, bool>? interrupted = null,
        CancellationToken cancellationToken = default)
    {
        var clock = new SimulatedClock();
        var output = createOutput(clock);
        var outcome = await StreamPlayer.PlayAsync(
            output, audio ?? TwoSeconds, () => interrupted?.Invoke(clock) ?? false, clock, cancellationToken: cancellationToken);
        return (outcome, output, clock);
    }

    [Fact]
    public async Task Typical_phone_plays_everything_and_reports_success()
    {
        var (outcome, output, clock) = await RunAsync(c => new SimulatedOutput(c));

        Assert.Equal(PlaybackOutcomeKind.Completed, outcome.Kind);
        Assert.Equal(TwoSeconds.SampleCount, output.Consumed);
        Assert.InRange(clock.Elapsed.TotalSeconds, 2.0, 2.4);
        Assert.Equal(0, output.Aborts);
    }

    [Theory]
    [InlineData(882)]    // 20 ms periods
    [InlineData(3528)]   // 80 ms "deep buffer" periods
    [InlineData(4410)]   // 100 ms periods
    public async Task Held_back_final_period_and_position_reset_after_stop_are_not_failures(int periodFrames)
    {
        // The exact behaviors behind "it spoke the time, then said playback failed".
        var audio = Signals.Tone(1777); // deliberately not a whole number of periods
        var (outcome, output, _) = await RunAsync(c => new SimulatedOutput(c) { PeriodFrames = periodFrames }, audio);

        Assert.Equal(PlaybackOutcomeKind.Completed, outcome.Kind);
        Assert.Equal(audio.SampleCount, output.Consumed);
    }

    [Fact]
    public async Task A_slow_bluetooth_start_is_waited_for()
    {
        var (outcome, output, clock) = await RunAsync(c => new SimulatedOutput(c) { StartDelay = TimeSpan.FromSeconds(1.5) });

        Assert.Equal(PlaybackOutcomeKind.Completed, outcome.Kind);
        Assert.Equal(TwoSeconds.SampleCount, output.Consumed);
        Assert.InRange(clock.Elapsed.TotalSeconds, 3.5, 4.0);
    }

    [Fact]
    public async Task An_output_that_never_starts_is_a_failure()
    {
        var (outcome, output, clock) = await RunAsync(c => new SimulatedOutput(c) { StartDelay = TimeSpan.FromDays(1) });

        Assert.Equal(PlaybackOutcomeKind.Failed, outcome.Kind);
        Assert.Contains("did not start", outcome.Detail);
        Assert.InRange(clock.Elapsed.TotalSeconds, 4.0, 4.1);
        Assert.Equal(1, output.Aborts);
    }

    [Fact]
    public async Task An_output_that_stops_consuming_midway_is_a_failure()
    {
        var (outcome, output, clock) = await RunAsync(c => new SimulatedOutput(c) { DiesAfterFrames = 30000 });

        Assert.Equal(PlaybackOutcomeKind.Failed, outcome.Kind);
        Assert.Contains("stopped", outcome.Detail);
        Assert.True(output.Consumed < TwoSeconds.SampleCount);
        Assert.Equal(1, output.Aborts);
        Assert.True(clock.Elapsed.TotalSeconds < 3.5);
    }

    [Fact]
    public async Task A_write_error_is_a_failure()
    {
        var (outcome, output, _) = await RunAsync(c => new SimulatedOutput(c) { WriteError = -6 });

        Assert.Equal(PlaybackOutcomeKind.Failed, outcome.Kind);
        Assert.Contains("-6", outcome.Detail);
        Assert.Equal(1, output.Aborts);
    }

    [Fact]
    public async Task Losing_audio_focus_midway_is_an_interruption()
    {
        var (outcome, output, _) = await RunAsync(c => new SimulatedOutput(c), interrupted: c => c.Elapsed > TimeSpan.FromSeconds(0.7));

        Assert.Equal(PlaybackOutcomeKind.Interrupted, outcome.Kind);
        Assert.Equal(1, output.Aborts);
        Assert.True(output.Consumed < TwoSeconds.SampleCount);
    }

    [Fact]
    public async Task Losing_audio_focus_while_draining_is_an_interruption()
    {
        var (outcome, output, _) = await RunAsync(c => new SimulatedOutput(c), interrupted: c => c.Elapsed > TimeSpan.FromSeconds(1.95));

        Assert.Equal(PlaybackOutcomeKind.Interrupted, outcome.Kind);
        Assert.True(output.Stopped);
        Assert.Equal(1, output.Aborts);
    }

    [Fact]
    public async Task Cancellation_stops_immediately()
    {
        using var cts = new CancellationTokenSource();
        var clock = new SimulatedClock();
        var output = new SimulatedOutput(clock);
        clock.Advanced += _ =>
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(0.5))
            {
                cts.Cancel();
            }
        };

        var outcome = await StreamPlayer.PlayAsync(output, TwoSeconds, () => false, clock, cancellationToken: cts.Token);

        Assert.Equal(PlaybackOutcomeKind.Cancelled, outcome.Kind);
        Assert.Equal(1, output.Aborts);
        Assert.InRange(clock.Elapsed.TotalSeconds, 0.5, 0.6);
    }

    [Fact]
    public async Task Audio_shorter_than_the_buffer_plays_when_the_stream_ends()
    {
        var shortAudio = Signals.Tone(100);
        var (outcome, output, _) = await RunAsync(c => new SimulatedOutput(c), shortAudio);

        Assert.Equal(PlaybackOutcomeKind.Completed, outcome.Kind);
        Assert.Equal(shortAudio.SampleCount, output.Consumed);
    }

    [Fact]
    public async Task A_device_whose_position_never_moves_still_plays_to_the_end()
    {
        var (outcome, output, clock) = await RunAsync(c => new SimulatedOutput(c) { PositionAlwaysZero = true });

        Assert.Equal(PlaybackOutcomeKind.Completed, outcome.Kind);
        Assert.Equal(TwoSeconds.SampleCount, output.Consumed);
        Assert.InRange(clock.Elapsed.TotalSeconds, 2.0, 2.4);
    }

    [Fact]
    public async Task Empty_audio_is_rejected()
    {
        var (outcome, _, _) = await RunAsync(c => new SimulatedOutput(c), new PcmAudio(44100, []));
        Assert.Equal(PlaybackOutcomeKind.Failed, outcome.Kind);
    }
}
