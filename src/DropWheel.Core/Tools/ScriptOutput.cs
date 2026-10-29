using System.Globalization;

namespace DropWheel.Core.Tools;

public enum ScriptLineKind
{
    /// <summary>Anything else the script printed; ignored.</summary>
    Other,
    /// <summary>"progress 0.42": how far along this item is, 0 to 1.</summary>
    Progress,
    /// <summary>"detail text": one line for the task window.</summary>
    Detail,
    /// <summary>"skip reason": nothing to do for this item; no output is expected.</summary>
    Skip,
}

/// <param name="Progress">For <see cref="ScriptLineKind.Progress"/>, clamped to 0..1.</param>
public readonly record struct ScriptLine(ScriptLineKind Kind, string Text = "", double Progress = 0);

/// <summary>
/// The stdout protocol a script may speak to DropWheel: one keyword, a space, the rest of the line. Everything
/// else is ignored, so a script that prints nothing special still works.
/// </summary>
public static class ScriptOutput
{
    public static ScriptLine Parse(string line)
    {
        var trimmed = line.Trim();
        var split = trimmed.IndexOf(' ');
        var keyword = split < 0 ? trimmed : trimmed[..split];
        var rest = split < 0 ? "" : trimmed[(split + 1)..].Trim();
        return keyword switch
        {
            "progress" when double.TryParse(rest, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) => new(ScriptLineKind.Progress, rest, Math.Clamp(value, 0, 1)),
            "detail" when rest.Length > 0 => new(ScriptLineKind.Detail, rest),
            "skip" => new(ScriptLineKind.Skip, rest),
            _ => new(ScriptLineKind.Other, trimmed),
        };
    }
}
