using System.Text.Json;
using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Core.Wheel;

namespace DropWheel.Core.Tools;

/// <param name="Tools">Every tool that loaded, in folder-name order, so each keeps its place on a wheel without a layout.</param>
/// <param name="Problems">Every folder with something to report, warnings included; a folder whose tool loaded may be here with a warning.</param>
public sealed record CatalogSnapshot(IReadOnlyList<IUserTool> Tools, IReadOnlyList<ToolReadResult> Problems)
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
    private readonly IReadOnlyList<IWheelOperation> _builtIns;
    private readonly ConversionGraph? _graph;
    private readonly Lock _gate = new();
    private FileSystemWatcher? _watcher;
    private ITimer? _debounce;

    /// <param name="builtIns">The items a recipe step may name, by id; only these, never another user tool.</param>
    /// <param name="graph">What a recipe's convert steps run through; without it they fail when run.</param>
    public UserToolCatalog(string toolsDirectory, IProcessRunner runner, CommandResolver resolver, TimeProvider? time = null, IReadOnlyList<IWheelOperation>? builtIns = null, ConversionGraph? graph = null)
    {
        Directory = toolsDirectory;
        _runner = runner;
        _resolver = resolver;
        _time = time ?? TimeProvider.System;
        _builtIns = builtIns ?? [];
        _graph = graph;
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

    /// <summary>
    /// Reads one folder with the checks the whole folder makes, against the other tool folders next to it; the
    /// command line's <c>--validate-tool</c>. Nothing is written and <see cref="Current"/> is not touched.
    /// </summary>
    public ToolReadResult Validate(string folder)
    {
        var full = Path.GetFullPath(folder);
        if (!System.IO.Directory.Exists(full))
        {
            return new(full, null, [new(ToolProblem.Json, L.T("没有这个文件夹"))]);
        }

        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sibling in Siblings(full))
        {
            if (ToolManifestReader.Read(sibling) is { Manifest: { } other, HasErrors: false })
            {
                seen[other.Id] = sibling;
            }
        }

        return Check(ToolManifestReader.Read(full), seen);
    }

    /// <summary>Every tool folder with its report, warnings and all, in name order; the command line's <c>--list-tools</c>. Nothing is written.</summary>
    public ToolListing Inspect()
    {
        var reports = new List<ToolReport>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var folder in Folders())
        {
            var result = Check(ToolManifestReader.Read(folder), seen);
            if (result.Manifest is { } manifest && !result.HasErrors)
            {
                seen[manifest.Id] = folder;
            }

            reports.Add(ToolReport.From(result));
        }

        return new(Directory, reports);
    }

    private IEnumerable<string> Folders() => System.IO.Directory.Exists(Directory)
        ? System.IO.Directory.EnumerateDirectories(Directory).Where(f => !Path.GetFileName(f).StartsWith('.')).Order(StringComparer.OrdinalIgnoreCase)
        : [];

    /// <summary>The other tool folders next to <paramref name="folder"/>, when it is in the tools folder.</summary>
    private IEnumerable<string> Siblings(string folder) =>
        string.Equals(Path.GetDirectoryName(folder), Path.GetFullPath(Directory), StringComparison.OrdinalIgnoreCase)
            ? Folders().Where(f => !string.Equals(f, folder, StringComparison.OrdinalIgnoreCase))
            : [];

    private CatalogSnapshot Read()
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return CatalogSnapshot.Empty;
        }

        var tools = new List<IUserTool>();
        var problems = new List<ToolReadResult>();
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var folder in Folders())
        {
            var result = Check(ToolManifestReader.Read(folder), seen);
            if (result.Problems.Count > 0)
            {
                problems.Add(result);
            }

            if (result.Manifest is { } manifest && !result.HasErrors)
            {
                seen[manifest.Id] = folder;
                tools.Add(manifest.IsRecipe
                    ? new RecipeTool(manifest, folder, _builtIns, _graph)
                    : new ScriptTool(manifest, folder, _runner, _resolver, _time));
            }

            Report(result);
        }

        return new(tools, problems);
    }

    /// <summary>
    /// The checks only the whole folder and the running app can make: duplicate ids, whether a script's command
    /// can be found, and whether a recipe's steps name built-ins that exist and take the options given.
    /// </summary>
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

        if (manifest.Script is { } script)
        {
            switch (_resolver.Resolve(script.Command, result.Folder))
            {
                case null:
                    problems.Add(new(ToolProblem.Command, L.T("找不到 {0}；装好或放到 PATH 上以后重新加载", script.Command), IsWarning: true));
                    break;
                case { FellBackToWindowsPowerShell: true }:
                    problems.Add(new(ToolProblem.Command, L.T("没有 pwsh，会用 Windows PowerShell 运行；它的输出不是 UTF-8，中文可能变成乱码"), IsWarning: true));
                    break;
            }
        }

        for (var i = 0; i < manifest.Steps.Count; i++)
        {
            if (manifest.Steps[i].Op is { } op)
            {
                CheckStep(i + 1, op, manifest.Steps[i].Options, problems);
            }
        }

        return result with { Problems = problems };
    }

    private void CheckStep(int number, string op, IReadOnlyDictionary<string, string> options, List<ToolProblem> problems)
    {
        var item = _builtIns.FirstOrDefault(b => b.Id == op);
        if (item is null)
        {
            problems.Add(new(ToolProblem.Steps, L.T("第 {0} 步：没有叫 {1} 的内置项能放进配方", number, op)));
            return;
        }

        if (!item.IsRecipeStep)
        {
            problems.Add(new(ToolProblem.Steps, L.T("第 {0} 步：{1} 要问用户或者不产生文件，配方里用不了", number, op)));
            return;
        }

        foreach (var (name, value) in options)
        {
            var spec = item.Options.FirstOrDefault(o => o.Name == name);
            if (spec is null)
            {
                problems.Add(new(ToolProblem.Steps, item.Options.Count == 0
                    ? L.T("第 {0} 步：{1} 没有选项", number, op)
                    : L.T("第 {0} 步：{1} 没有 {2} 这个选项；有的是 {3}", number, op, name, string.Join(' ', item.Options.Select(o => o.Name)))));
            }
            else if (!spec.Accepts(value))
            {
                problems.Add(new(ToolProblem.Steps, L.T("第 {0} 步：{1} 的 {2} 要是 {3}", number, op, name, spec.Expected)));
            }
        }
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
