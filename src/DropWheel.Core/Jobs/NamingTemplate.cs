using System.Globalization;
using System.Text.RegularExpressions;

namespace DropWheel.Core.Jobs;

public enum ConflictPolicy { AutoNumber, OverwriteToRecycleBin, Skip, Ask }

/// <summary>
/// Expands <c>{name}_{fmt}_{date}</c>-style templates. Tokens: {name} {fmt} {ext} {date} {time} {n} {w} {h}.
/// </summary>
public sealed partial class NamingTemplate(string template)
{
    public const string Default = "{name}";

    public string Template { get; } = string.IsNullOrWhiteSpace(template) ? Default : template;

    public string Expand(NamingContext ctx)
    {
        var result = TokenRegex().Replace(Template, m => m.Groups[1].Value.ToLowerInvariant() switch
        {
            "name" => ctx.SourceNameWithoutExtension,
            "fmt" => ctx.TargetFormatId,
            "ext" => ctx.TargetExtension,
            "date" => ctx.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            "time" => ctx.Now.ToString("HHmmss", CultureInfo.InvariantCulture),
            "n" => ctx.Sequence?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            "w" => ctx.Width?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            "h" => ctx.Height?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            _ => m.Value,
        });

        result = InvalidCharsRegex().Replace(result, "_").Trim();
        return result.Length == 0 ? ctx.SourceNameWithoutExtension : result;
    }

    /// <summary>Returns the final path, applying the conflict policy against <paramref name="exists"/>.</summary>
    public static string? ResolveConflict(string directory, string baseName, string extension, ConflictPolicy policy, Func<string, bool> exists)
    {
        var candidate = Path.Combine(directory, $"{baseName}.{extension}");
        if (!exists(candidate))
        {
            return candidate;
        }

        return policy switch
        {
            ConflictPolicy.AutoNumber => Enumerable.Range(1, 9999)
                .Select(i => Path.Combine(directory, $"{baseName} ({i}).{extension}"))
                .First(p => !exists(p)),
            ConflictPolicy.OverwriteToRecycleBin => candidate,
            ConflictPolicy.Skip => null,
            ConflictPolicy.Ask => null, // UI handles it
            _ => candidate,
        };
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"[<>:""/\\|?*\u0000-\u001F]")]
    private static partial Regex InvalidCharsRegex();
}

public sealed record NamingContext(
    string SourceNameWithoutExtension,
    string TargetFormatId,
    string TargetExtension,
    DateTimeOffset Now,
    int? Sequence = null,
    int? Width = null,
    int? Height = null);
