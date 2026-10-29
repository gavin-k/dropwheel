using System.Text.Json;
using DropWheel.Core.Jobs;

namespace DropWheel.Core.Tools;

/// <param name="Tools">Every tool that loaded, in folder-name order, so each keeps its place on a wheel without a layout.</param>
/// <param name="Problems">Every folder with something to report, warnings included; a folder whose tool loaded may be here with a warning.</param>
public sealed record CatalogSnapshot(IReadOnlyList<ScriptTool> Tools, IReadOnlyList<ToolReadResult> Problems)
{
    public static CatalogSnapshot Empty { get; } = new([], []);
}

/// <summary>
/// The tools folder: one subfolder per tool, each with a tool.json. <see cref="Reload"/> reads them all and leaves a
/// .problems.json in every folder with something wrong, which is how an agent that wrote the folder finds out;
/// <see cref="Watch"/> reloads on changes, so a tool shows up on the wheel as soon as it is written.
/// </summary>
public sealed class UserToolCatalog : IDisposable
{
    public const string ProblemsFile = ".problems.json";

    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private readonly IProcessRunner _runner;
    private readonly CommandResolver _resolver;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private FileSystemWatcher? _watcher;
    private ITimer? _debounce;

    public UserToolCatalog(string toolsDirectory, IProcessRunner runner, CommandResolver resolver, TimeProvider? time = null)
    {
        Directory = toolsDirectory;
        _runner = runner;
        _resolver = resolver;
        _time = time ?? TimeProvider.System;
    }

    public string Directory { get; }

    public CatalogSnapshot Current { get; private set; } = CatalogSnapshot.Empty;

    /// <summary>Raised after every <see cref="Reload"/>, on the thread that reloaded; a watched reload comes from the thread pool.</summary>
    public event Action<CatalogSnapshot>? Changed;

    /// <summary>Reads every tool folder again and writes or removes each folder's .problems.json.</summary>
    public CatalogSnapshot Reload()
    {
        CatalogSnapshot snapshot;
        lock (_gate)
        {
            snapshot = Read();
            Current = snapshot;
        }

        Changed?.Invoke(snapshot);
        return snapshot;
    }

    /// <summary>Creates the folder if needed and reloads 300 ms after the last change in it; DropWheel's own files do not count.</summary>
    public void Watch()
    {
        if (_watcher is not null)
        {
            return;
        }

        System.IO.Directory.CreateDirectory(Directory);
        _debounce = _time.CreateTimer(_ => Reload(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _watcher = new FileSystemWatcher(Directory)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Created += OnChange;
        _watcher.Changed += OnChange;
        _watcher.Deleted += OnChange;
        _watcher.Renamed += OnChange;
        _watcher.Error += (_, _) => _debounce?.Change(Debounce, Timeout.InfiniteTimeSpan);
        _watcher.EnableRaisingEvents = true;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }

    /// <summary>Whether a change to this path is one DropWheel made itself, which must not reload the catalog.</summary>
    public static bool IsOwnFile(string path) =>
        path.EndsWith(ProblemsFile, StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(OutputFiles.TempSuffix, StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    private void OnChange(object sender, FileSystemEventArgs e)
    {
        if (IsOwnFile(e.FullPath) || (e is RenamedEventArgs renamed && IsOwnFile(renamed.OldFullPath)))
        {
            return;
        }

        _debounce?.Change(Debounce, Timeout.InfiniteTimeSpan);
    }

    private CatalogSnapshot Read()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return CatalogSnapshot.Empty;
        }

        var tools = new List<ScriptTool>();
        var problems = new List<ToolReadResult>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var folder in System.IO.Directory.EnumerateDirectories(Directory).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetFileName(folder).StartsWith('.'))
            {
                continue;
            }

            var result = Check(ToolManifestReader.Read(folder), seen);
            if (result.Problems.Count > 0)
            {
                problems.Add(result);
            }

            if (result.Manifest is { } manifest && !result.HasErrors)
            {
                seen[manifest.Id] = folder;
                tools.Add(new ScriptTool(manifest, folder, _runner, _resolver, _time));
            }

            Report(result);
        }

        return new(tools, problems);
    }

    /// <summary>The checks only the whole folder can make: duplicate ids, and whether the command can be found.</summary>
    private ToolReadResult Check(ToolReadResult result, Dictionary<string, string> seen)
    {
        if (result.Manifest is not { } manifest)
        {
            return result;
        }

        var problems = new List<ToolProblem>(result.Problems);
        if (seen.TryGetValue(manifest.Id, out var other))
        {
            problems.Add(new(ToolProblem.Id, L.T("id 和 {0} 的重复", Path.GetFileName(other))));
        }

        switch (_resolver.Resolve(manifest.Script.Command, result.Folder))
        {
            case null:
                problems.Add(new(ToolProblem.Command, L.T("找不到 {0}；装好或放到 PATH 上以后重新加载", manifest.Script.Command), IsWarning: true));
                break;
            case { FellBackToWindowsPowerShell: true }:
                problems.Add(new(ToolProblem.Command, L.T("没有 pwsh，会用 Windows PowerShell 运行；它的输出不是 UTF-8，中文可能变成乱码"), IsWarning: true));
                break;
        }

        return result with { Problems = problems };
    }

    /// <summary>Writes the folder's .problems.json, or removes it when there is nothing to say; never for a folder with no tool.json.</summary>
    private static void Report(ToolReadResult result)
    {
        var path = Path.Combine(result.Folder, ProblemsFile);
        try
        {
            if (result.Problems.Count == 0 || !File.Exists(Path.Combine(result.Folder, ToolManifestReader.FileName)))
            {
                File.Delete(path);
                return;
            }

            var json = JsonSerializer.Serialize(ToolReport.From(result), ToolsJson.Default.ToolReport);
            if (File.Exists(path) && File.ReadAllText(path) == json)
            {
                // Writing the same thing again would only wake the watcher.
                return;
            }

            var temp = $"{path}.{Environment.ProcessId}.tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder is read-only or being deleted; the settings page still shows the problem.
        }
    }
}
