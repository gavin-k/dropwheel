using DropWheel.Core.Conversion;
using DropWheel.Core.Wheel;

namespace DropWheel.Core.Tools;

/// <summary>
/// What a tool's <c>tool.json</c> says, once <see cref="ToolManifestReader"/> has checked it. The schema is a public
/// contract agents write against; see docs/tool-manifest.schema.json and ADR-0005.
/// </summary>
/// <param name="Id">"user.&lt;slug&gt;": the operation id, which also becomes the request's TargetFormatId.</param>
/// <param name="Icon">A Segoe Fluent Icons code point such as "E7A7", in any spelling <see cref="Wheel.Glyphs.Parse"/> takes; null for no icon.</param>
/// <param name="AppliesTo">The kinds the tool shows up for; null for every kind.</param>
/// <param name="Kind">"script" runs a command; "recipe" runs built-in items in turn, see <see cref="Steps"/>.</param>
/// <param name="Script">What a script tool runs; null for a recipe.</param>
/// <param name="Placement">Where the author suggests the tool goes; applied once, on install, then the layout file rules.</param>
/// <param name="Undo">"recycle" puts the outputs in the Recycle Bin on undo; "none" says the job cannot be undone.</param>
public sealed record ToolManifest(
    int Schema,
    string Id,
    LocalizedText Name,
    LocalizedText Hint,
    string? Icon,
    WheelMode Wheel,
    IReadOnlyList<MediaKind>? AppliesTo,
    string Kind,
    ScriptSpec? Script,
    PlacementHint? Placement,
    string Undo)
{
    public const int CurrentSchema = 1;
    public const string ScriptKind = "script";
    public const string RecipeKind = "recipe";

    /// <summary>A recipe's steps, in order; empty for a script tool.</summary>
    public IReadOnlyList<RecipeStep> Steps { get; init; } = [];

    public bool IsRecipe => Kind == RecipeKind;

    public bool AppliesToKind(MediaKind kind) => AppliesTo is null || AppliesTo.Contains(kind);
}

/// <summary>
/// One step of a recipe: a built-in item by id with its options, or a conversion to a format. Exactly one of
/// <see cref="Op"/> and <see cref="Convert"/> is set.
/// </summary>
/// <param name="Op">A built-in item's id, such as "tool.compress-image"; never a user tool.</param>
/// <param name="Convert">A format id such as "jpg", resolved through the conversion graph at run time.</param>
/// <param name="Options">The step's options as text; a convert step has none.</param>
public sealed record RecipeStep(string? Op, string? Convert, IReadOnlyDictionary<string, string> Options)
{
    public bool IsConvert => Convert is not null;

    /// <summary>What the step is, for a message: the op id or "→ jpg".</summary>
    public string Describe() => Op ?? "\u2192 " + Convert;
}

/// <param name="Command">An absolute path, a path relative to the tool's folder, or a bare name found on PATH.</param>
/// <param name="Args">Arguments with placeholders: {input} {inputs} {output} {outputDir} {toolDir}.</param>
/// <param name="PerFile">True runs the command once per dragged item with {input}; false runs it once with {inputs}.</param>
public sealed record ScriptSpec(string Command, IReadOnlyList<string> Args, bool PerFile, OutputSpec Output, int TimeoutSeconds)
{
    public const int DefaultTimeoutSeconds = 600;

    public static readonly IReadOnlySet<string> Placeholders = new HashSet<string>(StringComparer.Ordinal) { "input", "inputs", "output", "outputDir", "toolDir" };
}

/// <param name="Mode">"new-file": the script writes one file at {output}, which DropWheel moves into place; "none": no output.</param>
/// <param name="Extension">The output's extension without the dot, or "same" for the source's.</param>
/// <param name="Suffix">Appended to the source's stem, such as "-clean".</param>
public sealed record OutputSpec(string Mode, string Extension, string Suffix)
{
    public const string NewFile = "new-file";
    public const string None = "none";
    public const string Same = "same";

    public static OutputSpec Default { get; } = new(NewFile, Same, "");

    public bool WritesFile => Mode == NewFile;
}

/// <param name="Group">The group the tool should join or start.</param>
/// <param name="Index">The top-level slot, 0 to 5.</param>
public sealed record PlacementHint(LocalizedText? Group, int? Index);
