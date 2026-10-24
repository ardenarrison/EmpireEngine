using NLayer;

namespace Engine.Core.Audio;

// A sound file decoded to 16-bit samples in memory, so playing it never touches the disk. Loads .mp3 and .wav.
// Clips with more than two channels keep only the first two.
public sealed class AudioClip
{
    public string Name { get; }
    public int SampleRate { get; }
    public int Channels { get; }          // 1 or 2
    public short[] Samples { get; }       // interleaved when stereo
    public int FrameCount => Samples.Length / Channels;
    public float Length => (float)FrameCount / SampleRate;

    public AudioClip(string name, int sampleRate, int channels, short[] samples)
    {
        if (channels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channels));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        Name = name;
        SampleRate = sampleRate;
        Channels = channels;
        Samples = samples;
    }

    public static bool IsAudio(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mp3" or ".wav";

    public static AudioClip Load(string path)
    {
        string name = Path.GetFileName(path);
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp3" => LoadMp3(path, name),
            ".wav" => LoadWav(File.ReadAllBytes(path), name),
            var ext => throw new NotSupportedException($"'{name}': {(ext.Length == 0 ? "no file extension" : ext)} is not a supported sound format. Use .mp3 or .wav."),
        };
    }

    private static AudioClip LoadMp3(string path, string name)
    {
        using var mpeg = new MpegFile(path);
        int channels = mpeg.Channels;
        var samples = new List<short>(checked((int)Math.Min(mpeg.Length / sizeof(float), int.MaxValue / 2)));
        var buffer = new float[4096 * channels];
        int read;
        while ((read = mpeg.ReadSamples(buffer, 0, buffer.Length)) > 0)
            for (int i = 0; i < read; i++)
                samples.Add(ToShort(buffer[i]));
        if (samples.Count == 0) throw new InvalidDataException($"'{name}' contains no audio.");
        return new AudioClip(name, mpeg.SampleRate, channels, samples.ToArray());
    }

    // RIFF/WAVE with PCM (8, 16, 24 or 32 bit) or 32-bit float samples.
    public static AudioClip LoadWav(byte[] data, string name = "clip.wav")
    {
        if (data.Length < 12 || !Tag(data, 0, "RIFF") || !Tag(data, 8, "WAVE"))
            throw new InvalidDataException($"'{name}' is not a WAV file.");

        int format = 0, channels = 0, rate = 0, bits = 0;
        int dataStart = -1, dataLength = 0;
        for (int pos = 12; pos + 8 <= data.Length;)
        {
            int size = BitConverter.ToInt32(data, pos + 4);
            int body = pos + 8;
            if (Tag(data, pos, "fmt ") && body + 16 <= data.Length)
            {
                format = BitConverter.ToUInt16(data, body);
                channels = BitConverter.ToUInt16(data, body + 2);
                rate = BitConverter.ToInt32(data, body + 4);
                bits = BitConverter.ToUInt16(data, body + 14);
                if (format == 0xFFFE && size >= 26 && body + 26 <= data.Length)
                    format = BitConverter.ToUInt16(data, body + 24);   // WAVE_FORMAT_EXTENSIBLE: the real format comes first in the sub-format
            }
            else if (Tag(data, pos, "data"))
            {
                dataStart = body;
                // A streamed file can leave the size at 0 or -1; the data then runs to the end of the file.
                dataLength = size <= 0 || body + size > data.Length ? data.Length - body : size;
                break;
            }
            if (size < 0) break;
            pos = body + size + (size & 1);
        }

        if (dataStart < 0 || channels < 1 || rate <= 0)
            throw new InvalidDataException($"'{name}' is missing its WAV format or data.");
        bool isFloat = format == 3;
        if (!(format == 1 && bits is 8 or 16 or 24 or 32) && !(isFloat && bits == 32))
            throw new NotSupportedException($"'{name}': only 8, 16, 24 and 32-bit PCM and 32-bit float WAV files are supported.");

        int bytes = bits / 8;
        int frames = dataLength / (bytes * channels);
        if (frames == 0) throw new InvalidDataException($"'{name}' contains no audio.");
        int outChannels = Math.Min(channels, 2);
        var samples = new short[frames * outChannels];
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < outChannels; c++)
            {
                int at = dataStart + (f * channels + c) * bytes;
                samples[f * outChannels + c] = bits switch
                {
                    8 => (short)((data[at] - 128) << 8),
                    16 => BitConverter.ToInt16(data, at),
                    24 => (short)((data[at + 2] << 8) | data[at + 1]),   // the top 16 of the 24 bits
                    _ when isFloat => ToShort(BitConverter.ToSingle(data, at)),
                    _ => (short)(BitConverter.ToInt32(data, at) >> 16),
                };
            }
        return new AudioClip(name, rate, outChannels, samples);
    }

    private static short ToShort(float sample) =>
        (short)Math.Clamp((int)MathF.Round(sample * 32767f), short.MinValue, short.MaxValue);

    private static bool Tag(byte[] data, int at, string tag) =>
        at + 4 <= data.Length && data[at] == tag[0] && data[at + 1] == tag[1] && data[at + 2] == tag[2] && data[at + 3] == tag[3];
}
