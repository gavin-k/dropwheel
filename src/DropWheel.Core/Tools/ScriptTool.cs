using System.ComponentModel;
using DropWheel.Core.Conversion;
using DropWheel.Core.Jobs;
using DropWheel.Core.Wheel;

namespace DropWheel.Core.Tools;

/// <summary>
/// A user tool that runs a script. DropWheel hands the script a temporary output path beside the target and moves
/// the file into place through <see cref="OutputFiles"/>, so naming, conflicts, rollback and undo work as for any
/// engine; the script never names its own output. Stateless, as the queue reuses the instance for retries.
/// </summary>
public sealed class ScriptTool(ToolManifest manifest, string folder, IProcessRunner runner, CommandResolver resolver, TimeProvider? time = null) : IWheelOperation
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public ToolManifest Manifest { get; } = manifest;

    /// <summary>The tool's folder, holding tool.json and whatever the script needs.</summary>
    public string Folder { get; } = folder;

    public string Id => Manifest.Id;

    public WheelMode Mode => Manifest.Wheel;

    public string Label => Manifest.Name.Pick();

    public string Hint => Manifest.Hint.Pick();

    public string? TrustKey => ToolManifestReader.CommandHash(Manifest.Script);

    public bool AppliesTo(MediaKind kind) => Manifest.AppliesToKind(kind);

    /// <summary>The command's path, or null when it cannot be found; a few file-exists calls, cheap enough for every wheel open.</summary>
    public CommandResolution? Command => resolver.Resolve(Manifest.Script.Command, Folder);

    public Availability Check(ConversionRequest request) => Command is null
        ? Availability.Unavailable(L.T("找不到 {0}", Manifest.Script.Command)) with { Cause = UnavailableCause.MissingOptionalModule }
        : Availability.Available;

    public async Task<ConversionResult> RunAsync(ConversionRequest request, IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (Command is not { } command)
        {
            return new(false, [], L.T("找不到 {0}", Manifest.Script.Command));
        }

        var script = Manifest.Script;
        var detail = new Detail();
        try
        {
            IReadOnlyList<string> outputs;
            if (script.PerFile)
            {
                outputs = await OutputFiles.ForEachSourceAsync(request, progress,
                    (source, index, within) => RunOneAsync(command.Path, request, [source], source, index, within, detail, cancellationToken), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Once for all the sources, the output named after the first, as the built-in merge does.
                var first = request with { SourcePaths = [request.SourcePaths[0]] };
                outputs = await OutputFiles.ForEachSourceAsync(first, progress,
                    (source, index, within) => RunOneAsync(command.Path, request, request.SourcePaths, source, index, within, detail, cancellationToken), cancellationToken).ConfigureAwait(false);
            }

            if (!script.Output.WritesFile)
            {
                return new ConversionResult(true, []) { Undo = new UndoPlan.None(), Detail = detail.Text };
            }

            return ConversionResult.Written(outputs) with
            {
                Undo = Manifest.Undo == "none" ? new UndoPlan.None() : null,
                Detail = detail.Text,
            };
        }
        catch (ScriptFailure ex)
        {
            return new(false, [], ex.Message);
        }
        catch (Win32Exception)
        {
            return new(false, [], L.T("无法启动 {0}", command.Path));
        }
    }

    /// <returns>The written path, or null when the script skipped the item, the conflict policy did, or there is no output.</returns>
    private async Task<string?> RunOneAsync(string executable, ConversionRequest request, IReadOnlyList<string> inputs, string namedAfter, int index, IProgress<double> within, Detail detail, CancellationToken cancellationToken)
    {
        var output = Manifest.Script.Output;
        if (!output.WritesFile)
        {
            await RunScriptAsync(executable, inputs, null, Path.GetDirectoryName(namedAfter) ?? "", null, within, detail, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var extension = output.Extension == OutputSpec.Same ? Path.GetExtension(namedAfter).TrimStart('.') : output.Extension;
        if (extension.Length == 0)
        {
            throw new ScriptFailure(L.T("{0} 没有扩展名", Path.GetFileName(namedAfter)));
        }

        var plan = request with { OutputExtension = extension, Naming = request.Naming + output.Suffix };
        try
        {
            return await OutputFiles.WriteAsync(plan, namedAfter, index, _time.GetLocalNow(), temp => WriteAsync(executable, inputs, namedAfter, temp, within, detail, cancellationToken), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (ScriptSkipped)
        {
            return null;
        }
    }

    /// <summary>Has the script write beside <paramref name="temp"/> and moves its file there.</summary>
    private async Task WriteAsync(string executable, IReadOnlyList<string> inputs, string namedAfter, string temp, IProgress<double> within, Detail detail, CancellationToken cancellationToken)
    {
        var output = Manifest.Script.Output;
        var extension = output.Extension == OutputSpec.Same ? Path.GetExtension(namedAfter).TrimStart('.') : output.Extension;
        {
            // The script's own file lives in a scratch folder beside the target, so the move into place is a rename
            // and Explorer never sees a half-written output under its real name.
            var outputDir = Path.GetDirectoryName(temp)!;
            var scratch = Path.Combine(outputDir, $".{Guid.NewGuid():N}{OutputFiles.TempSuffix}");
            var file = Path.Combine(scratch, $"{FormatRegistry.StemOf(namedAfter)}{output.Suffix}.{extension}");
            Directory.CreateDirectory(scratch);
            try
            {
                if (!await RunScriptAsync(executable, inputs, file, outputDir, scratch, within, detail, cancellationToken).ConfigureAwait(false))
                {
                    throw new ScriptSkipped();
                }

                if (!File.Exists(file))
                {
                    throw new ScriptFailure(L.T("{0} 没有写出文件", Label));
                }

                File.Move(file, temp);
            }
            finally
            {
                try
                {
                    Directory.Delete(scratch, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    /// <returns>False when the script said to skip this item.</returns>
    private async Task<bool> RunScriptAsync(string executable, IReadOnlyList<string> inputs, string? output, string outputDir, string? scratch, IProgress<double> within, Detail detail, CancellationToken cancellationToken)
    {
        var script = Manifest.Script;
        var arguments = new List<string>();
        foreach (var arg in script.Args)
        {
            if (arg == "{inputs}")
            {
                arguments.AddRange(inputs);
                continue;
            }

            arguments.Add(arg
                .Replace("{input}", inputs[0])
                .Replace("{inputs}", string.Join(' ', inputs))
                .Replace("{output}", output ?? "")
                .Replace("{outputDir}", outputDir)
                .Replace("{toolDir}", Folder));
        }

        var environment = new Dictionary<string, string>
        {
            ["DROPWHEEL_INPUT"] = inputs[0],
            ["DROPWHEEL_INPUTS"] = string.Join('\n', inputs),
            ["DROPWHEEL_OUTPUT_DIR"] = outputDir,
            ["DROPWHEEL_TOOL_DIR"] = Folder,
            ["PYTHONIOENCODING"] = "utf-8",
            ["PYTHONUTF8"] = "1",
        };
        if (output is not null)
        {
            environment["DROPWHEEL_OUTPUT"] = output;
            environment["DROPWHEEL_TEMP"] = scratch!;
        }

        string? skipped = null;
        var start = new ProcessStart(executable, arguments, Folder, environment, TimeSpan.FromSeconds(script.TimeoutSeconds));
        var outcome = await runner.RunAsync(start, line =>
        {
            var parsed = ScriptOutput.Parse(line);
            switch (parsed.Kind)
            {
                case ScriptLineKind.Progress:
                    within.Report(parsed.Progress);
                    break;
                case ScriptLineKind.Detail:
                    detail.Text = parsed.Text;
                    break;
                case ScriptLineKind.Skip:
                    skipped = parsed.Text;
                    break;
            }
        }, cancellationToken).ConfigureAwait(false);

        if (outcome.TimedOut)
        {
            throw new ScriptFailure(L.T("{0} 超过 {1} 秒没有完成", Label, script.TimeoutSeconds));
        }

        if (outcome.ExitCode != 0)
        {
            var tail = outcome.StderrTail.Trim();
            throw new ScriptFailure(tail.Length > 0 ? L.T("{0} 失败：{1}", Label, tail) : L.T("{0} 失败（退出码 {1}）", Label, outcome.ExitCode));
        }

        if (skipped is not null)
        {
            detail.Text = skipped.Length > 0 ? skipped : detail.Text;
            return false;
        }

        return true;
    }

    /// <summary>The last "detail" line any run printed; shared across the sources of one job.</summary>
    private sealed class Detail
    {
        public string? Text { get; set; }
    }

    private sealed class ScriptFailure(string message) : Exception(message);

    private sealed class ScriptSkipped : Exception;
}
