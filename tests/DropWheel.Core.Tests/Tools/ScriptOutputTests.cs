using DropWheel.Core.Tools;
using FluentAssertions;

namespace DropWheel.Core.Tests.Tools;

public sealed class ScriptOutputTests
{
    [Theory]
    [InlineData("progress 0.5", ScriptLineKind.Progress, "0.5", 0.5)]
    [InlineData("  progress 1.5 ", ScriptLineKind.Progress, "1.5", 1)]
    [InlineData("progress -1", ScriptLineKind.Progress, "-1", 0)]
    [InlineData("progress soon", ScriptLineKind.Other, "progress soon", 0)]
    [InlineData("detail 3 张图", ScriptLineKind.Detail, "3 张图", 0)]
    [InlineData("detail", ScriptLineKind.Other, "detail", 0)]
    [InlineData("skip already clean", ScriptLineKind.Skip, "already clean", 0)]
    [InlineData("skip", ScriptLineKind.Skip, "", 0)]
    [InlineData("Processing...", ScriptLineKind.Other, "Processing...", 0)]
    [InlineData("", ScriptLineKind.Other, "", 0)]
    public void Lines_are_parsed_by_their_keyword(string line, ScriptLineKind kind, string text, double progress)
    {
        ScriptOutput.Parse(line).Should().Be(new ScriptLine(kind, text, progress));
    }
}
