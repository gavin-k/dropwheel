namespace DropWheel.Core.Tools;

/// <summary>
/// Something wrong with a tool folder. <see cref="Code"/> is stable for agents and tests; <see cref="Message"/>
/// is for people. A warning does not stop the tool from loading.
/// </summary>
public sealed record ToolProblem(string Code, string Message, bool IsWarning = false)
{
    public const string Json = "json";
    public const string Schema = "schema";
    public const string Id = "id";
    public const string Name = "name";
    public const string Icon = "icon";
    public const string Wheel = "wheel";
    public const string AppliesTo = "appliesTo";
    public const string Kind = "kind";
    public const string Command = "command";
    public const string Args = "args";
    public const string Output = "output";
    public const string Timeout = "timeout";
    public const string Placement = "placement";
    public const string Undo = "undo";
    public const string Steps = "steps";
}

/// <summary>What reading one tool folder gave: a manifest when there was no error, and every problem found.</summary>
public sealed record ToolReadResult(string Folder, ToolManifest? Manifest, IReadOnlyList<ToolProblem> Problems)
{
    public bool HasErrors => Problems.Any(p => !p.IsWarning);
}
