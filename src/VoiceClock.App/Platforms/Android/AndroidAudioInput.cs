using System.Diagnostics;
using Android.Media;
using VoiceClock.Core.Audio;
using VoiceClock.Core.Recording;
using AEncoding = Android.Media.Encoding;

namespace VoiceClock.App.Droid;

/// <summary>Captures mono 16-bit PCM with <see cref="AudioRecord"/> (44.1 kHz works on every Android device).</summary>
public sealed class AndroidAudioInput : IAudioInput
{
    public Task<IAudioCapture> StartAsync(int sampleRate, TimeSpan maxDuration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var minBuffer = AudioRecord.GetMinBufferSize(sampleRate, ChannelIn.Mono, AEncoding.Pcm16bit);
        if (minBuffer <= 0)
        {
            throw new AudioInputException($"Recording format not supported ({minBuffer}).");
        }

        AudioRecord? record = null;
        try
        {
            record = new AudioRecord(AudioSource.Mic, sampleRate, ChannelIn.Mono, AEncoding.Pcm16bit, Math.Max(minBuffer * 2, sampleRate));
            if (record.State != Android.Media.State.Initialized)
            {
                throw new AudioInputException("The microphone could not be opened.");
            }

            record.StartRecording();
            if (record.RecordingState != RecordState.Recording)
            {
                throw new AudioInputException("The microphone is being used by another app.");
            }

            var capture = new AndroidAudioCapture(record, sampleRate, maxDuration);
            record = null; // owned by the capture now
            capture.Start();
            return Task.FromResult<IAudioCapture>(capture);
        }
        catch (Java.Lang.Exception ex)
        {
            throw new AudioInputException(ex.Message ?? "Microphone error.", ex);
        }
        finally
        {
            record?.Release();
            record?.Dispose();
        }
    }
}

/// <summary>Reads the microphone on a background thread until stopped or the length limit is reached.</summary>
internal sealed class AndroidAudioCapture : IAudioCapture
{
    private readonly AudioRecord _record;
    private readonly int _sampleRate;
    private readonly int _maxSamples;
    private readonly List<short[]> _chunks = [];
    private readonly object _sync = new();
    private readonly Stopwatch _stopwatch = new();
    private Thread? _thread;
    private volatile bool _running;
    private int _count;
    private bool _released;

    public AndroidAudioCapture(AudioRecord record, int sampleRate, TimeSpan maxDuration)
    {
        _record = record;
        _sampleRate = sampleRate;
        _maxSamples = (int)(maxDuration.TotalSeconds * sampleRate);
    }

    public event EventHandler<float>? LevelChanged;

    public event EventHandler? LimitReached;

    public TimeSpan Elapsed => _stopwatch.Elapsed;

    public void Start()
    {
        _running = true;
        _stopwatch.Start();
        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "VoiceClockRecorder" };
        _thread.Start();
    }

    public Task<PcmAudio> StopAsync() => Task.Run(() =>
    {
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(2));
        StopRecorder();
        _stopwatch.Stop();

        short[] all;
        lock (_sync)
        {
            all = new short[_count];
            var position = 0;
            foreach (var chunk in _chunks)
            {
                Array.Copy(chunk, 0, all, position, chunk.Length);
                position += chunk.Length;
            }
        }

        return new PcmAudio(_sampleRate, all);
    });

    public ValueTask DisposeAsync()
    {
        _running = false;
        _thread?.Join(TimeSpan.FromSeconds(1));
        StopRecorder();
        lock (_sync)
        {
            if (!_released)
            {
                _released = true;
                _record.Release();
                _record.Dispose();
            }
        }

        return ValueTask.CompletedTask;
    }

    private void ReadLoop()
    {
        var buffer = new short[_sampleRate / 20]; // 50 ms
        try
        {
            while (_running)
            {
                var read = _record.Read(buffer, 0, buffer.Length);
                if (read < 0)
                {
                    break;
                }

                if (read == 0)
                {
                    continue;
                }

                var reachedLimit = false;
                float peak = 0;
                lock (_sync)
                {
                    var room = _maxSamples - _count;
                    var take = Math.Min(room, read);
                    if (take > 0)
                    {
                        var chunk = new short[take];
                        Array.Copy(buffer, chunk, take);
                        _chunks.Add(chunk);
                        _count += take;
                        peak = Peak(chunk);
                    }

                    reachedLimit = _count >= _maxSamples;
                }

                LevelChanged?.Invoke(this, peak);
                if (reachedLimit)
                {
                    _running = false;
                    LimitReached?.Invoke(this, EventArgs.Empty);
                    break;
                }
            }
        }
        catch (Exception)
        {
            _running = false;
        }
    }

    private void StopRecorder()
    {
        lock (_sync)
        {
            if (_released)
            {
                return;
            }

            try
            {
                if (_record.RecordingState == RecordState.Recording)
                {
                    _record.Stop();
                }
            }
            catch (Java.Lang.IllegalStateException)
            {
            }
        }
    }

    private static float Peak(short[] chunk)
    {
        var max = 0;
        foreach (var s in chunk)
        {
            var magnitude = s == short.MinValue ? 32768 : Math.Abs((int)s);
            if (magnitude > max)
            {
                max = magnitude;
            }
        }

        return max / 32768f;
    }
}
