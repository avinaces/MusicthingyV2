using System.Text;

namespace Musicthingy.Services;

/// <summary>
/// Reads the header of a local audio file to report its container, codec and
/// (when the header carries it) sample rate.
///
/// Cached tracks are stored under an extension-less SHA256 name
/// (see <see cref="TrackCacheService"/>), so the file name says nothing about
/// the format - the magic bytes do. Only the first few KB are read, so this is
/// cheap enough to run once when a track starts.
/// </summary>
public static class LocalMediaInspector
{
    private const int HeaderBytesToRead = 8192;
    private const int OpusDefaultSampleRateHz = 48000;

    /// <summary>MP4 audio sample entries we can name (checked in this order).</summary>
    private static readonly string[] Mp4AudioFourCcs = { "mp4a", "alac" };

    /// <summary>Offset of the 16.16 sample-rate field from the sample entry fourcc.</summary>
    private const int AudioSampleEntryRateOffset = 28;

    public sealed record LocalMediaInfo(string? Codec, string? Container, int? SampleRateHz, long FileSizeBytes);

    /// <summary>Returns null when the path is not a readable file.</summary>
    public static LocalMediaInfo? Inspect(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        try
        {
            FileInfo file = new(path);
            if (!file.Exists) return null;

            byte[] header = new byte[HeaderBytesToRead];
            int read;
            using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                read = stream.Read(header, 0, header.Length);

            (string? codec, string? container, int? sampleRate) = Detect(header, read);

            // MP4/M4A keeps its audio sample entry ('mp4a', 'alac') in the moov
            // atom, which non-faststart files write at the END - scan the tail
            // for both the codec and the sample rate that entry carries.
            if ((codec == null || sampleRate == null) && container is "m4a" or "mp4")
            {
                (string? tailCodec, int? tailSampleRate) = ReadMp4AudioFormatFromTail(path, file.Length);
                codec ??= tailCodec;
                sampleRate ??= tailSampleRate;
            }

            return new LocalMediaInfo(codec, container, sampleRate, file.Length);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Diagnostics] Could not inspect {path}: {ex.Message}");
            return null;
        }
    }

    private static (string? Codec, string? Container, int? SampleRate) Detect(byte[] header, int length)
    {
        if (length < 4) return (null, null, null);

        // Matroska / WebM EBML header.
        if (header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3)
        {
            int opus = IndexOf(header, length, "OpusHead");
            if (opus >= 0)
                return ("opus", "webm", ReadOpusSampleRate(header, length, opus));

            if (IndexOf(header, length, "A_VORBIS") >= 0) return ("vorbis", "webm", null);
            return (null, "webm", null);
        }

        // ISO base media: ....ftyp.
        if (length >= 8 && header[4] == (byte)'f' && header[5] == (byte)'t' &&
            header[6] == (byte)'y' && header[7] == (byte)'p')
        {
            string? container = IndexOf(header, length, "M4A ") >= 0 ? "m4a" : "mp4";
            (string? codec, int? sampleRate) = FindMp4AudioFormat(header, length);
            return (codec, container, sampleRate);
        }

        // Ogg (Opus or Vorbis).
        if (header[0] == (byte)'O' && header[1] == (byte)'g' && header[2] == (byte)'g' && header[3] == (byte)'S')
        {
            int opus = IndexOf(header, length, "OpusHead");
            if (opus >= 0) return ("opus", "ogg", ReadOpusSampleRate(header, length, opus));
            if (IndexOf(header, length, "vorbis") >= 0) return ("vorbis", "ogg", null);
            return (null, "ogg", null);
        }

        if (header[0] == (byte)'f' && header[1] == (byte)'L' && header[2] == (byte)'a' && header[3] == (byte)'C')
            return ("flac", "flac", null);

        if (header[0] == (byte)'R' && header[1] == (byte)'I' && header[2] == (byte)'F' && header[3] == (byte)'F')
            return ("pcm", "wav", null);

        if ((header[0] == (byte)'I' && header[1] == (byte)'D' && header[2] == (byte)'3') ||
            (header[0] == 0xFF && (header[1] & 0xE0) == 0xE0))
            return ("mp3", "mp3", null);

        return (null, null, null);
    }

    /// <summary>
    /// Reads the last 64 KB of the file (where the moov atom lives unless the
    /// file is faststart) and looks for the MP4 audio sample entry in it.
    /// </summary>
    private static (string? Codec, int? SampleRate) ReadMp4AudioFormatFromTail(string path, long fileLength)
    {
        const int TailBytesToRead = 64 * 1024;

        try
        {
            long start = Math.Max(0, fileLength - TailBytesToRead);
            byte[] tail = new byte[TailBytesToRead];

            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            stream.Seek(start, SeekOrigin.Begin);
            int read = stream.Read(tail, 0, tail.Length);

            return FindMp4AudioFormat(tail, read);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Diagnostics] Could not scan MP4 tail of {path}: {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>
    /// Finds the MP4 audio sample entry ('mp4a' / 'alac') and reads the sample
    /// rate it carries. AudioSampleEntry stores that as 16.16 fixed point 28
    /// bytes after the entry's fourcc (6 reserved + data_reference_index + 8
    /// reserved + channels + sampleSize + preDefined + reserved = 28 bytes).
    /// </summary>
    private static (string? Codec, int? SampleRate) FindMp4AudioFormat(byte[] buffer, int length)
    {
        foreach (string fourCc in Mp4AudioFourCcs)
        {
            int searchFrom = 0;
            while (searchFrom < length)
            {
                int index = IndexOf(buffer, length, fourCc, searchFrom);
                if (index < 0) break;

                int rateOffset = index + AudioSampleEntryRateOffset;
                if (rateOffset + 4 <= length)
                {
                    // 16.16 fixed point: the integer part is the first two bytes.
                    int rate = (buffer[rateOffset] << 8) | buffer[rateOffset + 1];
                    if (rate is >= 8000 and <= 384000) return (fourCc, rate);
                }

                searchFrom = index + 1;
            }

            // The entry exists but its rate field was out of range: still
            // report the codec, just without a rate.
            if (IndexOf(buffer, length, fourCc) >= 0) return (fourCc, null);
        }

        return (null, null);
    }

    /// <summary>
    /// OpusHead layout: "OpusHead"(8) version(1) channels(1) preSkip(2) inputSampleRate(4 LE).
    /// </summary>
    private static int? ReadOpusSampleRate(byte[] header, int length, int opusHeadOffset)
    {
        int offset = opusHeadOffset + 12;
        if (offset + 4 > length) return null;

        uint rate = (uint)(header[offset] | (header[offset + 1] << 8) |
                           (header[offset + 2] << 16) | (header[offset + 3] << 24));
        return rate is > 0 and <= 384000 ? (int)rate : OpusDefaultSampleRateHz;
    }

    private static int IndexOf(byte[] haystack, int length, string needle, int startIndex = 0)
    {
        byte[] pattern = Encoding.ASCII.GetBytes(needle);
        int last = length - pattern.Length;
        for (int i = Math.Max(0, startIndex); i <= last; i++)
        {
            int j = 0;
            while (j < pattern.Length && haystack[i + j] == pattern[j]) j++;
            if (j == pattern.Length) return i;
        }
        return -1;
    }
}
