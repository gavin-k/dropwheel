using System.Text.Json;
using DropWheel.Core.Conversion;
using DropWheel.Core.Wheel;

namespace DropWheel.Core.Tools;

/// <summary>
/// What an agent runs after writing a tool: <c>--validate-tool &lt;folder&gt;</c> reads one folder with every check the
/// catalog makes and prints its report; <c>--list-tools</c> prints every folder in the tools folder. Both print one
/// JSON document and exit 0 when nothing is an error, 1 when something is, 2 when the arguments make no sense.
/// Neither writes a .problems.json: the report is the output.
/// </summary>
public static class ToolCommandLine
{
    public const string Validate = "--validate-tool";
    public const string List = "--list-tools";

    public static bool Handles(IReadOnlyList<string> args) => args.Contains(Validate) || args.Contains(List);

    /// <param name="builtIns">The items a recipe step may name; a recipe is an error without them.</param>
    public static int Run(IReadOnlyList<string> args, string toolsDirectory, IProcessRunner runner, CommandResolver resolver, TextWriter output, IReadOnlyList<IWheelOperation>? builtIns = null, ConversionGraph? graph = null)
    {
        var catalog = new UserToolCatalog(toolsDirectory, runner, resolver, builtIns: builtIns, graph: graph);
        if (args.Contains(List))
        {
            var listing = catalog.Inspect();
            output.Write(JsonSerializer.Serialize(listing, ToolsJson.Default.ToolListing));
            return listing.Tools.Any(t => t.HasErrors) ? 1 : 0;
        }

        var at = args.ToList().IndexOf(Validate);
        if (at + 1 >= args.Count || args[at + 1].StartsWith("--", StringComparison.Ordinal))
        {
            output.Write(JsonSerializer.Serialize(new ToolUsage($"{Validate} <folder> | {List}"), ToolsJson.Default.ToolUsage));
            return 2;
        }

        var report = ToolReport.From(catalog.Validate(args[at + 1]));
        output.Write(JsonSerializer.Serialize(report, ToolsJson.Default.ToolReport));
        return report.HasErrors ? 1 : 0;
    }
}
