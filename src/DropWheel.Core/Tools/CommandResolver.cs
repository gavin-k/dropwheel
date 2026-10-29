namespace DropWheel.Core.Tools;

/// <param name="Path">The full path to run.</param>
/// <param name="FellBackToWindowsPowerShell">The manifest said pwsh, which is not installed, so Windows PowerShell runs instead; its console is not UTF-8.</param>
public sealed record CommandResolution(string Path, bool FellBackToWindowsPowerShell = false);

/// <summary>
/// Turns a manifest's command into a path: an absolute path as is, a relative path under the tool's folder,
/// and a bare name first in the tool's folder, then on PATH, each tried with every PATHEXT extension the way
/// the shell does. Pure: the file system comes in as <paramref name="fileExists"/>.
/// </summary>
public sealed class CommandResolver(Func<string, bool> fileExists, IReadOnlyList<string> pathDirectories, IReadOnlyList<string> pathExtensions)
{
    public const string Pwsh = "pwsh";
    public const string WindowsPowerShell = "powershell";

    /// <summary>The process's own PATH and PATHEXT; PATHEXT is empty outside Windows.</summary>
    public static CommandResolver FromEnvironment() => new(
        File.Exists,
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries),
        OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : []);

    public CommandResolution? Resolve(string command, string toolDirectory)
    {
        if (command.Length == 0)
        {
            return null;
        }

        if (Path.IsPathRooted(command))
        {
            return WithExtensions(command) is { } rooted ? new(rooted) : null;
        }

        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
        {
            return WithExtensions(Path.GetFullPath(Path.Combine(toolDirectory, command))) is { } relative ? new(relative) : null;
        }

        if (Bare(command, toolDirectory) is { } found)
        {
            return new(found);
        }

        if (command.Equals(Pwsh, StringComparison.OrdinalIgnoreCase) && Bare(WindowsPowerShell, toolDirectory) is { } fallback)
        {
            return new(fallback, FellBackToWindowsPowerShell: true);
        }

        return null;
    }

    private string? Bare(string name, string toolDirectory)
    {
        foreach (var directory in pathDirectories.Prepend(toolDirectory))
        {
            if (WithExtensions(Path.Combine(directory, name)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private string? WithExtensions(string path)
    {
        if (fileExists(path))
        {
            return path;
        }

        foreach (var extension in pathExtensions)
        {
            if (fileExists(path + extension))
            {
                return path + extension;
            }
        }

        return null;
    }
}
