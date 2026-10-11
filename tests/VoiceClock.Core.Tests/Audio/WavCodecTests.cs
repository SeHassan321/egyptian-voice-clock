using System.Buffers.Binary;
using VoiceClock.Core.Audio;
using VoiceClock.Core.Tests.TestSupport;

namespace VoiceClock.Core.Tests.Audio;

public class WavCodecTests
{
    [Fact]
    public void Encode_then_decode_round_trips_samples_and_rate()
    {
        var original = Signals.Tone(250, rate: 22050);
        var decoded = WavCodec.Decode(WavCodec.Encode(original));

        Assert.Equal(22050, decoded.SampleRate);
        Assert.Equal(original.Samples, decoded.Samples);
    }

    [Fact]
    public void Encoded_header_is_standard_mono_16_bit_pcm()
    {
        var bytes = WavCodec.Encode(new PcmAudio(44100, [1, -1, 300]));

        Assert.Equal(44 + 6, bytes.Length);
        Assert.Equal("RIFF"u8.ToArray(), bytes[..4]);
        Assert.Equal("WAVE"u8.ToArray(), bytes[8..12]);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(20)));   // PCM
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22)));   // mono
        Assert.Equal(44100u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)));
        Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(34)));
        Assert.Equal(6u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)));
    }

    [Fact]
    public void Stereo_input_is_down_mixed_to_mono()
    {
        var wav = BuildWav(channels: 2, sampleRate: 16000, samples: [100, 300, -200, -400]);
        var decoded = WavCodec.Decode(wav);

        Assert.Equal(new short[] { 200, -300 }, decoded.Samples);
    }

    [Fact]
    public void Unrelated_chunks_before_the_data_are_skipped_including_odd_padding()
    {
        var wav = BuildWav(channels: 1, sampleRate: 8000, samples: [5, 6, 7], extraChunkSize: 3);
        Assert.Equal(new short[] { 5, 6, 7 }, WavCodec.Decode(wav).Samples);
    }

    [Fact]
    public void Wave_format_extensible_with_pcm_subformat_is_accepted()
    {
        var wav = BuildWav(channels: 1, sampleRate: 48000, samples: [1, 2], extensible: true);
        Assert.Equal(new short[] { 1, 2 }, WavCodec.Decode(wav).Samples);
    }

    [Fact]
    public void Random_bytes_are_not_wav()
    {
        var bytes = new byte[200];
        new Random(1).NextBytes(bytes);
        AssertProblem(bytes, AudioProblem.NotWav);
    }

    [Fact]
    public void Riff_files_of_another_kind_are_not_wav()
    {
        var bytes = WavCodec.Encode(Signals.Tone(10));
        "AVI "u8.CopyTo(bytes.AsSpan(8));
        AssertProblem(bytes, AudioProblem.NotWav);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(24)]
    public void Non_16_bit_audio_is_unsupported(int bits)
    {
        var bytes = WavCodec.Encode(Signals.Tone(10));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), (ushort)bits);
        AssertProblem(bytes, AudioProblem.UnsupportedFormat);
    }

    [Fact]
    public void Float_audio_is_unsupported()
    {
        var bytes = WavCodec.Encode(Signals.Tone(10));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 3);
        AssertProblem(bytes, AudioProblem.UnsupportedFormat);
    }

    [Fact]
    public void Truncated_data_is_detected()
    {
        var bytes = WavCodec.Encode(Signals.Tone(100));
        AssertProblem(bytes[..(bytes.Length - 10)], AudioProblem.Truncated);
    }

    [Fact]
    public void Header_without_data_chunk_is_detected()
    {
        var bytes = WavCodec.Encode(Signals.Tone(100));
        AssertProblem(bytes[..36], AudioProblem.Truncated);
    }

    [Fact]
    public void Empty_recording_is_detected()
    {
        AssertProblem(WavCodec.Encode(new PcmAudio(44100, [])), AudioProblem.Empty);
    }

    [Fact]
    public void Zero_length_input_is_not_wav()
    {
        AssertProblem([], AudioProblem.NotWav);
    }

    private static void AssertProblem(byte[] bytes, AudioProblem expected)
    {
        Assert.False(WavCodec.TryDecode(bytes, out var audio, out var problem));
        Assert.Null(audio);
        Assert.Equal(expected, problem);
        var ex = Assert.Throws<InvalidAudioException>(() => WavCodec.Decode(bytes));
        Assert.Equal(expected, ex.Problem);
    }

    private static byte[] BuildWav(int channels, int sampleRate, short[] samples, int extraChunkSize = 0, bool extensible = false)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var fmtSize = extensible ? 40 : 16;
        w.Write("RIFF"u8);
        w.Write(0u);
        w.Write("WAVE"u8);

        if (extraChunkSize > 0)
        {
            w.Write("LIST"u8);
            w.Write((uint)extraChunkSize);
            w.Write(new byte[extraChunkSize]);
            if (extraChunkSize % 2 == 1)
            {
                w.Write((byte)0);
            }
        }

        w.Write("fmt "u8);
        w.Write((uint)fmtSize);
        w.Write(extensible ? (ushort)0xFFFE : (ushort)1);
        w.Write((ushort)channels);
        w.Write((uint)sampleRate);
        w.Write((uint)(sampleRate * channels * 2));
        w.Write((ushort)(channels * 2));
        w.Write((ushort)16);
        if (extensible)
        {
            w.Write((ushort)22);      // cbSize
            w.Write((ushort)16);      // valid bits
            w.Write(0u);              // channel mask
            w.Write((ushort)1);       // sub-format: PCM GUID starts with 0x0001
            w.Write(new byte[14]);
        }

        w.Write("data"u8);
        w.Write((uint)(samples.Length * 2));
        foreach (var s in samples)
        {
            w.Write(s);
        }

        return ms.ToArray();
    }
}
