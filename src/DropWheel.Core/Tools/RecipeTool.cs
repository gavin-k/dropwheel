using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Core.Wheel;

namespace DropWheel.Core.Tools;

/// <summary>
/// A user tool that runs built-in items in turn: each step's outputs are the next step's inputs. Steps before
/// the last write into a hidden scratch folder beside the destination, which is removed afterwards; only the
/// last step writes where the job's outputs go, through the job's own request, so naming, conflicts, rollback
/// and undo are the last step's as for any built-in. Steps run per file unless the item works on a batch; a
/// step that writes nothing for a file (compress found it small enough) passes the file on as it is. A step's
/// suffix stays in the name, so a compress-then-shrink recipe writes "a-压缩-600x400.jpg". Nothing here runs code
/// the user did not get from DropWheel, so no trust switch applies. Stateless, as the queue reuses the instance.
/// </summary>
public sealed class RecipeTool : IUserTool
{
    private readonly IReadOnlyList<IWheelOperation?> _ops;
    private readonly ConversionGraph? _graph;

    public RecipeTool(ToolManifest manifest, string folder, IReadOnlyList<IWheelOperation> builtIns, ConversionGraph? graph)
    {
        Manifest = manifest;
        Folder = folder;
        _graph = graph;
        _ops = [.. manifest.Steps.Select(step => step.Op is { } op ? builtIns.FirstOrDefault(b => b.Id == op && b.IsRecipeStep) : null)];
    }

    public ToolManifest Manifest { get; }

    public string Folder { get; }

    public string Id => Manifest.Id;

    public WheelMode Mode => Manifest.Wheel;

    public string Label => Manifest.Name.Pick();

    public string Hint => Manifest.Hint.Pick();

    public string? Glyph => Glyphs.Parse(Manifest.Icon);

    /// <summary>A recipe is never itself a step: a recipe of recipes would hide what runs.</summary>
    public bool IsRecipeStep => false;

    public bool AppliesTo(MediaKind kind) => Manifest.AppliesTo is not null
        ? Manifest.AppliesToKind(kind)
        : _ops.Count > 0 && _ops[0] is { } first && first.AppliesTo(kind);

    /// <summary>The first step's check; later steps are checked when they run, as their inputs do not exist yet.</summary>
    public Availability Check(ConversionRequest request)
    {
        if (Manifest.Steps.Count == 0)
        {
            return Availability.Unavailable(L.T("配方没有步骤"));
        }

        return Resolve(0, request.SourcePaths) is var (runner, target, missing)
            ? runner is null ? Availability.Unavailable(missing!) : runner.Check(Prepare(request, Manifest.Steps[0], target))
            : Availability.Unavailable(L.T("配方没有步骤"));
    }

    public async Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken)
    {
        var steps = Manifest.Steps;
        if (steps.Count == 0)
        {
            return new(false, [], L.T("配方没有步骤"));
        }

        // Where the last step writes: the job's folder, or beside the first dragged item. Never the scratch folder.
        var destination = request.OutputDirectory.Length > 0 ? request.OutputDirectory : Path.GetDirectoryName(request.SourcePaths[0]) ?? "";
        var scratch = Path.Combine(destination, $".{Guid.NewGuid():N}{OutputFiles.TempSuffix}");
        IReadOnlyList<string> inputs = request.SourcePaths;
        try
        {
            for (var i = 0; i < steps.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var step = steps[i];
                var at = i;
                var within = new InlineProgress(p => progress.Report((at + Math.Clamp(p, 0, 1)) / steps.Count));
                var (runner, target, missing) = Resolve(i, inputs);
                if (runner is null)
                {
                    return Failed(i, step, missing!);
                }

                if (i == steps.Count - 1)
                {
                    // The job's own request, so Written, Conflicts and the naming settings are the job's.
                    var final = Prepare(request, step, target) with { SourcePaths = inputs, OutputDirectory = destination };
                    var result = await runner.RunAsync(final, within, cancellationToken).ConfigureAwait(false);
                    if (!result.Succeeded)
                    {
                        return Failed(i, step, result.Error);
                    }

                    return Manifest.Undo == "none" ? result with { Undo = new UndoPlan.None() } : result;
                }

                Directory.CreateDirectory(scratch);
                Hide(scratch);
                var next = new List<string>();
                if (runner is IWheelOperation { WorksOnBatch: true })
                {
                    var result = await runner.RunAsync(new ConversionRequest(inputs, target, scratch, step.Options), within, cancellationToken).ConfigureAwait(false);
                    if (!result.Succeeded)
                    {
                        return Failed(i, step, result.Error);
                    }

                    next.AddRange(result.OutputPaths.Count > 0 ? result.OutputPaths : inputs);
                }
                else
                {
                    for (var j = 0; j < inputs.Count; j++)
                    {
                        var input = inputs[j];
                        var done = j;
                        var each = new InlineProgress(p => within.Report((done + Math.Clamp(p, 0, 1)) / inputs.Count));
                        var result = await runner.RunAsync(new ConversionRequest([input], target, scratch, step.Options), each, cancellationToken).ConfigureAwait(false);
                        if (!result.Succeeded)
                        {
                            return Failed(i, step, result.Error is { } error ? L.T("{0}：{1}", Path.GetFileName(input), error) : Path.GetFileName(input));
                        }

                        next.AddRange(result.OutputPaths.Count > 0 ? result.OutputPaths : [input]);
                    }
                }

                inputs = next;
                progress.Report((i + 1.0) / steps.Count);
            }

            return new(false, [], L.T("配方没有步骤"));
        }
        finally
        {
            try
            {
                if (Directory.Exists(scratch))
                {
                    Directory.Delete(scratch, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Explorer may hold a handle for a moment; the folder is hidden and ends in .dropwheel-tmp.
            }
        }
    }

    /// <summary>The runner for a step given its inputs, or why there is none; a convert step needs the graph and an edge from the inputs' format.</summary>
    private (IJobRunner? Runner, string Target, string? Missing) Resolve(int index, IReadOnlyList<string> inputs)
    {
        var step = Manifest.Steps[index];
        if (step.Op is { } op)
        {
            return _ops[index] is { } runner ? (runner, op, null) : (null, op, L.T("没有叫 {0} 的内置项能放进配方", op));
        }

        var to = step.Convert!;
        if (_graph is null)
        {
            return (null, to, L.T("这里没有转换引擎"));
        }

        var from = inputs.Count > 0 ? FormatRegistry.FromPath(inputs[0]) : null;
        if (from is null)
        {
            return (null, to, L.T("无法识别源文件格式。"));
        }

        return _graph.Resolve(from.Id, to) is { } converter
            ? (converter, to, null)
            : (null, to, L.T("没有引擎支持 {0} → {1}。", from.DisplayName, FormatRegistry.FromExtension(to)?.DisplayName ?? to));
    }

    private static ConversionRequest Prepare(ConversionRequest request, RecipeStep step, string target) =>
        request with { TargetFormatId = target, Options = step.IsConvert ? request.Options : step.Options };

    private static ConversionResult Failed(int index, RecipeStep step, string? error) =>
        new(false, [], error is { Length: > 0 } ? L.T("第 {0} 步 {1}：{2}", index + 1, step.Describe(), error) : L.T("第 {0} 步 {1} 失败", index + 1, step.Describe()));

    private static void Hide(string folder)
    {
        try
        {
            File.SetAttributes(folder, File.GetAttributes(folder) | FileAttributes.Hidden);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }
    }
}
