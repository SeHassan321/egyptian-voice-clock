using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace VoiceClock.Core.Audio;

/// <summary>Why a piece of audio cannot be used.</summary>
public enum AudioProblem
{
    None,
    NotWav,
    UnsupportedFormat,
    Truncated,
    Empty,
    TooLarge,
}

public sealed class InvalidAudioException : Exception
{
    public InvalidAudioException(AudioProblem problem, string message)
        : base(message)
    {
        Problem = problem;
    }

    public AudioProblem Problem { get; }
}

/// <summary>
/// Reads and writes RIFF/WAVE files holding 16-bit PCM. Writes are always mono; reads accept
/// mono or stereo (down-mixed) and skip unrelated chunks such as LIST metadata.
/// </summary>
public static class WavCodec
{
    public const int HeaderSize = 44;

    /// <summary>Generous upper bound (≈6 minutes of 44.1 kHz stereo) that blocks absurd files.</summary>
    public const int MaxDataBytes = 64 * 1024 * 1024;

    private const ushort FormatPcm = 1;
    private const ushort FormatExtensible = 0xFFFE;

    public static byte[] Encode(PcmAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var dataBytes = checked(audio.Samples.Length * 2);
        var buffer = new byte[HeaderSize + dataBytes];
        var span = buffer.AsSpan();

        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)(36 + dataBytes));
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], FormatPcm);
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)audio.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], (uint)(audio.SampleRate * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)dataBytes);

        var data = span[HeaderSize..];
        var samples = audio.Samples;
        for (var i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(data[(i * 2)..], samples[i]);
        }

        return buffer;
    }

    public static bool TryDecode(ReadOnlySpan<byte> wav, [NotNullWhen(true)] out PcmAudio? audio, out AudioProblem problem)
    {
        try
        {
            audio = Decode(wav);
            problem = AudioProblem.None;
            return true;
        }
        catch (InvalidAudioException ex)
        {
            audio = null;
            problem = ex.Problem;
            return false;
        }
    }

    public static PcmAudio Decode(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            throw new InvalidAudioException(AudioProblem.NotWav, "Not a RIFF/WAVE file.");
        }

        var haveFormat = false;
        int channels = 0, sampleRate = 0, blockAlign = 0;
        long position = 12;

        while (position + 8 <= wav.Length)
        {
            var chunkId = wav.Slice((int)position, 4);
            long chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice((int)position + 4, 4));
            var bodyStart = position + 8;
            var available = wav.Length - bodyStart;

            if (chunkId.SequenceEqual("fmt "u8))
            {
                if (chunkSize < 16 || chunkSize > available)
                {
                    throw new InvalidAudioException(AudioProblem.Truncated, "Incomplete format chunk.");
                }

                var fmt = wav.Slice((int)bodyStart, (int)chunkSize);
                var formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]);
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..]);
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt[12..]);
                var bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);

                var isPcm = formatTag == FormatPcm ||
                    (formatTag == FormatExtensible && chunkSize >= 40 && BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]) == FormatPcm);

                if (!isPcm || bitsPerSample != 16 || channels is < 1 or > 2 || blockAlign != channels * 2 ||
                    sampleRate is < PcmAudio.MinSampleRate or > PcmAudio.MaxSampleRate)
                {
                    throw new InvalidAudioException(
                        AudioProblem.UnsupportedFormat,
                        $"Unsupported WAV format (tag {formatTag}, {channels} ch, {bitsPerSample} bit, {sampleRate} Hz).");
                }

                haveFormat = true;
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                if (!haveFormat)
                {
                    throw new InvalidAudioException(AudioProblem.NotWav, "Data chunk appears before the format chunk.");
                }

                if (chunkSize > available)
                {
                    throw new InvalidAudioException(AudioProblem.Truncated, "The audio data is shorter than its header declares.");
                }

                if (chunkSize > MaxDataBytes)
                {
                    throw new InvalidAudioException(AudioProblem.TooLarge, "The audio data is too large.");
                }

                var frames = (int)(chunkSize / blockAlign);
                if (frames == 0)
                {
                    throw new InvalidAudioException(AudioProblem.Empty, "The recording contains no audio.");
                }

                return new PcmAudio(sampleRate, ReadSamples(wav.Slice((int)bodyStart, frames * blockAlign), channels, frames));
            }
            else if (chunkSize > available)
            {
                throw new InvalidAudioException(AudioProblem.Truncated, "A chunk is shorter than its header declares.");
            }

            position = bodyStart + chunkSize + (chunkSize & 1);
        }

        throw new InvalidAudioException(haveFormat ? AudioProblem.Truncated : AudioProblem.NotWav, "No audio data chunk was found.");
    }

    private static short[] ReadSamples(ReadOnlySpan<byte> data, int channels, int frames)
    {
        var samples = new short[frames];
        if (channels == 1)
        {
            for (var i = 0; i < frames; i++)
            {
                samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(i * 2)..]);
            }
        }
        else
        {
            for (var i = 0; i < frames; i++)
            {
                int left = BinaryPrimitives.ReadInt16LittleEndian(data[(i * 4)..]);
                int right = BinaryPrimitives.ReadInt16LittleEndian(data[((i * 4) + 2)..]);
                samples[i] = (short)((left + right) / 2);
            }
        }

        return samples;
    }
}
