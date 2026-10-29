using System.Text.Json.Serialization;

namespace DropWheel.Core.Tools;

/// <summary>One tool folder as the problems file and the command line report it.</summary>
public sealed record ToolReport(string Folder, string? Id, string? Name, string? Wheel, IReadOnlyList<ToolProblem> Problems)
{
    public static ToolReport From(ToolReadResult result) =>
        new(result.Folder, result.Manifest?.Id, result.Manifest?.Name.Zh, result.Manifest?.Wheel.ToString().ToLowerInvariant(), result.Problems);
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ToolReport))]
[JsonSerializable(typeof(List<ToolReport>))]
internal sealed partial class ToolsJson : JsonSerializerContext;
