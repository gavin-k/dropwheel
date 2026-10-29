using DropWheel.Core.Tools;
using FluentAssertions;

namespace DropWheel.Core.Tests.Tools;

public sealed class CommandResolverTests
{
    private static readonly string Tool = Path.Combine(Path.GetTempPath(), "tools", "strip");
    private static readonly string Bin = Path.Combine(Path.GetTempPath(), "bin");
    private static readonly string Programs = Path.Combine(Path.GetTempPath(), "programs");

    private static CommandResolver Resolver(params string[] files) =>
        new(p => files.Contains(p), [Bin, Programs], [".EXE", ".CMD"]);

    [Fact]
    public void An_absolute_path_is_taken_as_is_when_it_exists()
    {
        var exe = Path.Combine(Programs, "strip.EXE");

        Resolver(exe).Resolve(exe, Tool).Should().Be(new CommandResolution(exe));
        Resolver().Resolve(exe, Tool).Should().BeNull();
    }

    [Fact]
    public void A_relative_path_is_under_the_tool_folder()
    {
        var script = Path.Combine(Tool, "bin", "run.cmd");

        Resolver(script).Resolve($"bin{Path.DirectorySeparatorChar}run.cmd", Tool).Should().Be(new CommandResolution(script));
        Resolver(script).Resolve("./bin/run.cmd", Tool)!.Path.Should().Be(script);
        Resolver().Resolve("bin/run.cmd", Tool).Should().BeNull();
    }

    [Fact]
    public void A_bare_name_is_found_in_the_tool_folder_before_PATH()
    {
        var mine = Path.Combine(Tool, "strip.EXE");
        var onPath = Path.Combine(Bin, "strip.EXE");

        Resolver(mine, onPath).Resolve("strip", Tool)!.Path.Should().Be(mine);
        Resolver(onPath).Resolve("strip", Tool)!.Path.Should().Be(onPath);
        Resolver(onPath).Resolve("strip.EXE", Tool)!.Path.Should().Be(onPath);
    }

    [Fact]
    public void PATH_directories_are_searched_in_order_with_every_extension()
    {
        var cmd = Path.Combine(Programs, "tool.CMD");
        var exe = Path.Combine(Programs, "tool.EXE");

        Resolver(cmd).Resolve("tool", Tool)!.Path.Should().Be(cmd);
        Resolver(cmd, exe).Resolve("tool", Tool)!.Path.Should().Be(exe, "PATHEXT order wins inside a directory");
        Resolver(exe, Path.Combine(Bin, "tool.CMD")).Resolve("tool", Tool)!.Path.Should().Be(Path.Combine(Bin, "tool.CMD"), "an earlier directory wins");
    }

    [Fact]
    public void Pwsh_falls_back_to_Windows_PowerShell_and_says_so()
    {
        var pwsh = Path.Combine(Bin, "pwsh.EXE");
        var legacy = Path.Combine(Programs, "powershell.EXE");

        Resolver(pwsh, legacy).Resolve("pwsh", Tool).Should().Be(new CommandResolution(pwsh));
        Resolver(legacy).Resolve("pwsh", Tool).Should().Be(new CommandResolution(legacy, FellBackToWindowsPowerShell: true));
        Resolver().Resolve("pwsh", Tool).Should().BeNull();
        Resolver(legacy).Resolve("python", Tool).Should().BeNull("only pwsh has a fallback");
    }

    [Fact]
    public void An_empty_command_resolves_to_nothing()
    {
        Resolver().Resolve("", Tool).Should().BeNull();
    }

    [Fact]
    public void The_environment_resolver_finds_this_platform_shell()
    {
        var resolver = CommandResolver.FromEnvironment();
        var shell = OperatingSystem.IsWindows() ? "cmd" : "sh";

        resolver.Resolve(shell, Tool).Should().NotBeNull();
        resolver.Resolve("no-such-command-dropwheel", Tool).Should().BeNull();
    }
}
