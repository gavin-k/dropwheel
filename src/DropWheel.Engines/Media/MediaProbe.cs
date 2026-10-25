using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DropWheel.Engines.Media;

/// <param name="Index">The stream's index in the file, for -map 0:N.</param>
/// <param name="Type">"video", "audio", "subtitle"…</param>
/// <param name="Width">As displayed, i.e. already swapped for a stream rotated by 90°.</param>
/// <param name="AttachedPicture">Cover art stored as a video stream, not a real video.</param>
/// <param name="BitsPerSample">Of the decoded audio or video samples, when the codec keeps it (FLAC, PCM).</param>
public sealed record MediaTrack(
    int Index,
    string Type,
    string Codec,
    int? Width = null,
    int? Height = null,
    double? FrameRate = null,
    long? BitRate = null,
    bool AttachedPicture = false,
    int? BitsPerSample = null,
    int? Channels = null);

public sealed record MediaInfo(TimeSpan? Duration, long? BitRate, IReadOnlyList<MediaTrack> Streams)
{
    /// <summary>The first real video stream, skipping cover art.</summary>
    public MediaTrack? Video => Streams.FirstOrDefault(s => s.Type == "video" && !s.AttachedPicture);

    public IReadOnlyList<MediaTrack> Audio => Streams.Where(s => s.Type == "audio").ToArray();
}

/// <summary>Reads streams and duration with ffprobe.</summary>
public static class MediaProbe
{
    /// <exception cref="InvalidDataException">ffprobe could not read the file.</exception>
    public static async Task<MediaInfo> ProbeAsync(string ffprobe, string path, CancellationToken cancellationToken)
    {
        var json = new StringBuilder();
        var result = await new SidecarProcess(ffprobe).RunAsync(
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", "file:" + path],
            line => json.AppendLine(line), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} 无法识别为音视频文件{Detail(result.StderrTail)}");
        }

        return Parse(json.ToString());
    }

    public static MediaInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var streams = new List<MediaTrack>();
        if (root.TryGetProperty("streams", out var list))
        {
            foreach (var s in list.EnumerateArray())
            {
                var type = Str(s, "codec_type") ?? "";
                int? width = Int(s, "width"), height = Int(s, "height");
                if (Rotation(s) is 90 or 270)
                {
                    (width, height) = (height, width);
                }

                streams.Add(new(
                    Int(s, "index") ?? streams.Count,
                    type,
                    Str(s, "codec_name") ?? "",
                    width,
                    height,
                    type == "video" ? Rate(Str(s, "avg_frame_rate")) ?? Rate(Str(s, "r_frame_rate")) : null,
                    Long(s, "bit_rate"),
                    s.TryGetProperty("disposition", out var d) && Int(d, "attached_pic") == 1,
                    Positive(Int(s, "bits_per_raw_sample")) ?? Positive(Int(s, "bits_per_sample")),
                    Int(s, "channels")));
            }
        }

        TimeSpan? duration = null;
        long? bitRate = null;
        if (root.TryGetProperty("format", out var format))
        {
            if (Double(format, "duration") is double seconds && seconds > 0)
            {
                duration = TimeSpan.FromSeconds(seconds);
            }

            bitRate = Long(format, "bit_rate");
        }

        return new(duration, bitRate, streams);
    }

    internal static string Detail(IReadOnlyList<string> stderrTail) =>
        stderrTail.LastOrDefault(l => !string.IsNullOrWhiteSpace(l)) is { } last ? $"：{last.Trim()}" : "";

    private static int Rotation(JsonElement stream)
    {
        if (!stream.TryGetProperty("side_data_list", out var sideData))
        {
            return 0;
        }

        foreach (var item in sideData.EnumerateArray())
        {
            if (Double(item, "rotation") is { } r)
            {
                return ((int)Math.Round(r) % 360 + 360) % 360;
            }
        }

        return 0;
    }

    /// <summary>"30000/1001" as 29.97; "0/0" (unknown) as null.</summary>
    private static double? Rate(string? fraction)
    {
        var parts = fraction?.Split('/');
        if (parts is not [var n, var d]
            || !double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
            || !double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
            || num <= 0 || den <= 0)
        {
            return null;
        }

        return num / den;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>ffprobe writes some numbers as JSON numbers and others ("bit_rate", "duration") as strings.</summary>
    private static double? Double(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
    }

    private static int? Positive(int? value) => value > 0 ? value : null;

    private static long? Long(JsonElement e, string name) => Double(e, name) is { } d ? (long)d : null;

    private static int? Int(JsonElement e, string name) => Double(e, name) is { } d ? (int)d : null;
}
