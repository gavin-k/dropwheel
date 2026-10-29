using System.Text.Json.Serialization;

namespace DropWheel.Core.Tools;

/// <summary>One tool folder as the problems file and the command line report it.</summary>
public sealed record ToolReport(string Folder, string? Id, string? Name, string? Wheel, IReadOnlyList<ToolProblem> Problems)
{
    public static ToolReport From(ToolReadResult result) =>
        new(result.Folder, result.Manifest?.Id, result.Manifest?.Name.Zh, result.Manifest?.Wheel.ToString().ToLowerInvariant(), result.Problems);

    [JsonIgnore]
    public bool HasErrors => Problems.Any(p => !p.IsWarning);
}

/// <summary>The tools folder and a report for every tool folder in it, as <c>--list-tools</c> prints it.</summary>
public sealed record ToolListing(string Folder, IReadOnlyList<ToolReport> Tools);

/// <summary>What <c>--validate-tool</c> prints when it is given no folder.</summary>
public sealed record ToolUsage(string Usage);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ToolReport))]
[JsonSerializable(typeof(ToolListing))]
[JsonSerializable(typeof(ToolUsage))]
internal sealed partial class ToolsJson : JsonSerializerContext;
