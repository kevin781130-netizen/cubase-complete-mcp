using System.Buffers.Binary;

namespace CubaseCompleteMcp.Server;

/// <summary>
/// Bounded header-level integrity check for uncompressed RIFF/WAV audio.
/// Does NOT decode/listen to samples, validate commercial licenses, or assert
/// that a sound actually depicts the requested Foley action.
/// AIFF/FLAC/RF64 and compressed WAVE codecs remain unverified (excluded).
/// </summary>
internal static class SteinbergFoleyAudioProbe
{
    internal sealed record AudioMetadata(
        string Container, string Encoding, int Channels, int SampleRate,
        int BitsPerSample, long DataBytes, double DurationSeconds);

    internal static bool TryProbe(string path, out AudioMetadata? metadata, out string issue)
    {
        metadata = null;
        issue = "UNSUPPORTED_OR_UNVERIFIED_AUDIO_FORMAT";
        if (!Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is < 44 or > 268_435_456) { issue = "AUDIO_SIZE_INVALID"; return false; }

            Span<byte> header = stackalloc byte[12];
            stream.ReadExactly(header);
            if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WAVE"u8))
            { issue = "NOT_STANDARD_RIFF_WAV"; return false; }
            var declaredRiff = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4, 4));
            if ((long)declaredRiff + 8 != stream.Length)
            { issue = "RIFF_LENGTH_MISMATCH"; return false; }

            ushort codec = 0, channels = 0, blockAlign = 0, bitsPerSample = 0;
            uint sampleRate = 0, byteRate = 0;
            long dataBytes = -1;
            bool fmtFound = false, dataFound = false;
            int chunks = 0;

            Span<byte> chunk = stackalloc byte[8];
            while (stream.Position < stream.Length)
            {
                if (stream.Length - stream.Position < 8 || ++chunks > 128)
                { issue = "INVALID_WAV_CHUNKS"; return false; }
                stream.ReadExactly(chunk);
                long size = BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(4, 4));
                long start = stream.Position;
                long paddedSize = size + (size & 1);
                if (paddedSize > stream.Length - start)
                { issue = "TRUNCATED_WAV_CHUNK"; return false; }

                if (chunk[..4].SequenceEqual("fmt "u8))
                {
                    if (fmtFound || size < 16 || size > 4096)
                    { issue = "INVALID_WAV_FMT_CHUNK"; return false; }
                    Span<byte> fmt = stackalloc byte[16];
                    stream.ReadExactly(fmt);
                    codec = BinaryPrimitives.ReadUInt16LittleEndian(fmt[0..2]);
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..4]);
                    sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(fmt[4..8]);
                    byteRate = BinaryPrimitives.ReadUInt32LittleEndian(fmt[8..12]);
                    blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(fmt[12..14]);
                    bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..16]);
                    fmtFound = true;
                }
                else if (chunk[..4].SequenceEqual("data"u8))
                {
                    if (dataFound) { issue = "DUPLICATE_WAV_DATA_CHUNK"; return false; }
                    dataBytes = size;
                    dataFound = true;
                }
                // Skip metadata or audio payload without loading any full file.
                stream.Position = start + paddedSize;
            }

            if (!fmtFound || !dataFound || dataBytes <= 0)
            { issue = "WAV_FMT_OR_DATA_MISSING"; return false; }
            if (codec is not (1 or 3) || channels is < 1 or > 8 ||
                sampleRate is < 8000 or > 192000 || bitsPerSample is not (8 or 16 or 24 or 32) ||
                (codec == 3 && bitsPerSample != 32) ||
                blockAlign != channels * bitsPerSample / 8 ||
                byteRate != sampleRate * (uint)blockAlign ||
                dataBytes % blockAlign != 0)
            { issue = "UNSUPPORTED_OR_INCONSISTENT_WAV_FMT"; return false; }

            var seconds = dataBytes / (double)byteRate;
            if (!double.IsFinite(seconds) || seconds <= 0)
            { issue = "INVALID_AUDIO_DURATION"; return false; }
            metadata = new AudioMetadata("RIFF/WAV", codec == 1 ? "PCM" : "IEEE_FLOAT",
                channels, checked((int)sampleRate), bitsPerSample, dataBytes, seconds);
            issue = "OK_HEADER_ONLY";
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or
            ArgumentException or OverflowException or EndOfStreamException)
        {
            issue = "AUDIO_IO_OR_HEADER_ERROR";
            return false;
        }
    }
}
