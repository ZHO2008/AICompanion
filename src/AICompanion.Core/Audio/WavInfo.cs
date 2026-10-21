namespace AICompanion.Core.Audio;

/// <summary>WAV 头部信息（只解析 PCM 的 fmt/data 块，足够算时长）。</summary>
public sealed record WavInfo(int SampleRate, int Channels, int BitsPerSample, int DataBytes, int ByteRate)
{
    public TimeSpan Duration
    {
        get
        {
            if (ByteRate <= 0)
            {
                return TimeSpan.Zero;
            }

            return TimeSpan.FromSeconds((double)DataBytes / ByteRate);
        }
    }

    /// <summary>从 WAV 字节流解析基本信息；失败返回 false（不抛异常）。</summary>
    public static bool TryParse(byte[]? wav, out WavInfo info)
    {
        info = new WavInfo(0, 0, 0, 0, 0);

        if (wav is null || wav.Length < 44)
        {
            return false;
        }

        if (!Matches(wav, 0, "RIFF") || !Matches(wav, 8, "WAVE"))
        {
            return false;
        }

        var sampleRate = 0;
        var channels = 0;
        var bits = 0;
        var byteRate = 0;
        var dataBytes = 0;

        var offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var chunkId = System.Text.Encoding.ASCII.GetString(wav, offset, 4);
            var chunkSize = BitConverter.ToInt32(wav, offset + 4);

            if (chunkSize < 0)
            {
                return false;
            }

            var body = offset + 8;

            if (chunkId == "fmt " && body + 16 <= wav.Length)
            {
                channels = BitConverter.ToInt16(wav, body + 2);
                sampleRate = BitConverter.ToInt32(wav, body + 4);
                byteRate = BitConverter.ToInt32(wav, body + 8);
                bits = BitConverter.ToInt16(wav, body + 14);
            }
            else if (chunkId == "data")
            {
                dataBytes = Math.Min(chunkSize, wav.Length - body);
                break;
            }

            // 块按偶数字节对齐。
            offset = body + chunkSize + (chunkSize % 2);
        }

        if (sampleRate <= 0 || channels <= 0 || bits <= 0)
        {
            return false;
        }

        if (byteRate <= 0)
        {
            byteRate = sampleRate * channels * bits / 8;
        }

        info = new WavInfo(sampleRate, channels, bits, dataBytes, byteRate);
        return true;
    }

    private static bool Matches(byte[] buffer, int offset, string ascii)
    {
        if (offset + ascii.Length > buffer.Length)
        {
            return false;
        }

        for (var i = 0; i < ascii.Length; i++)
        {
            if (buffer[offset + i] != ascii[i])
            {
                return false;
            }
        }

        return true;
    }
}
