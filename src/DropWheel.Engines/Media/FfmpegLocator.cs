namespace DropWheel.Engines.Media;

/// <summary>Where ffmpeg and ffprobe were found. They are always taken from the same directory.</summary>
public sealed record FfmpegTools(string Ffmpeg, string Ffprobe);

/// <summary>
/// Finds the ffmpeg sidecar: the copy bundled under sidecars/ffmpeg next to the app (see scripts/fetch-sidecars.sh),
/// then the PATH, which is what development machines and CI use.
/// </summary>
public static class FfmpegLocator
{
    private static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    /// <param name="directory">Look only here. A directory without both tools means not found, there is no fallback.</param>
    public static FfmpegTools? Find(string? directory = null)
    {
        if (directory is not null)
        {
            return In(directory);
        }

        var bundled = In(Path.Combine(AppContext.BaseDirectory, "sidecars", "ffmpeg"));
        if (bundled is not null)
        {
            return bundled;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (In(dir) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static FfmpegTools? In(string directory)
    {
        var ffmpeg = Path.Combine(directory, Exe("ffmpeg"));
        var ffprobe = Path.Combine(directory, Exe("ffprobe"));
        return File.Exists(ffmpeg) && File.Exists(ffprobe) ? new(ffmpeg, ffprobe) : null;
    }
}
