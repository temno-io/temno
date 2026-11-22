using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Temno.Core.Transcription;

public sealed record PreparedPcmWav(byte[] Bytes, string Sha256, double DurationSeconds, int SampleRate);

public interface IPcmWavPreparer
{
    Task<PreparedPcmWav> PrepareAsync(string mediaPath, CancellationToken cancellationToken = default);
}

public static class Pcm16Wav
{
    public const int SampleRate = 16_000;
    // WP-393: the Gateway's hosted cap (WaveInput), refused here before any upload.
    public const int MaxSeconds = 1_800;
    public const int MaxBytes = 96_000_000;
    public const string TooLongMessage =
        "Cloud transcription accepts recordings up to 30 minutes. Split longer audio into parts of 30 minutes or less and transcribe each part.";

    public static PreparedPcmWav FromMonoFloats(float[] samples, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (sampleRate is not 16_000 and not 24_000)
            throw new InvalidOperationException("Cloud transcription accepts 16 kHz or 24 kHz mono PCM WAV.");
        if (samples.Length == 0)
            throw new InvalidOperationException("Cloud transcription needs audible audio.");

        var duration = samples.Length / (double)sampleRate;
        if (duration > MaxSeconds)
            throw new InvalidOperationException(TooLongMessage);

        var pcm = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            var clipped = Math.Clamp(samples[i], -1f, 1f);
            var sample = (short)Math.Round(clipped * short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), sample);
        }

        var bytes = Wrap(pcm, sampleRate);
        if (bytes.Length > MaxBytes)
            throw new InvalidOperationException(TooLongMessage);
        return new PreparedPcmWav(bytes, Convert.ToHexString(SHA256.HashData(bytes)), duration, sampleRate);
    }

    public static byte[] Wrap(ReadOnlySpan<byte> pcm, int sampleRate)
    {
        var dataSize = pcm.Length;
        var bytes = new byte[44 + dataSize];
        var span = bytes.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataSize);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataSize);
        pcm.CopyTo(span[44..]);
        return bytes;
    }
}
