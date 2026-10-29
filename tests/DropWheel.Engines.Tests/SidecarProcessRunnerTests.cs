using DropWheel.Core.Tools;
using DropWheel.Engines.Tools;
using FluentAssertions;

namespace DropWheel.Engines.Tests;

/// <summary>Runs this platform's shell, so the tests mean the same on macOS and on Windows.</summary>
public sealed class SidecarProcessRunnerTests
{
    private static ProcessStart Shell(string script, TimeSpan? timeout = null, IReadOnlyDictionary<string, string>? environment = null)
    {
        var resolver = CommandResolver.FromEnvironment();
        var (shell, flag) = OperatingSystem.IsWindows() ? ("cmd", "/c") : ("sh", "-c");
        return new(resolver.Resolve(shell, Path.GetTempPath())!.Path, [flag, script], Path.GetTempPath(), environment ?? new Dictionary<string, string>(), timeout ?? TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Stdout_lines_and_the_exit_code_come_back()
    {
        var lines = new List<string>();
        var script = OperatingSystem.IsWindows() ? "echo one& echo two& exit 3" : "echo one; echo two; exit 3";

        var outcome = await new SidecarProcessRunner().RunAsync(Shell(script), lines.Add, CancellationToken.None);

        outcome.ExitCode.Should().Be(3);
        outcome.TimedOut.Should().BeFalse();
        lines.Select(l => l.Trim()).Should().Equal("one", "two");
    }

    [Fact]
    public async Task The_environment_reaches_the_process_and_stderr_is_kept()
    {
        var lines = new List<string>();
        var script = OperatingSystem.IsWindows() ? "echo %DW_PROBE%& echo oops 1>&2& exit 1" : "echo $DW_PROBE; echo oops >&2; exit 1";

        var outcome = await new SidecarProcessRunner().RunAsync(Shell(script, environment: new Dictionary<string, string> { ["DW_PROBE"] = "hello" }), lines.Add, CancellationToken.None);

        lines.Select(l => l.Trim()).Should().Equal("hello");
        outcome.ExitCode.Should().Be(1);
        outcome.StderrTail.Trim().Should().Be("oops");
    }

    [Fact]
    public async Task A_process_past_its_timeout_is_killed_and_reported()
    {
        var script = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 > nul" : "sleep 30";

        var outcome = await new SidecarProcessRunner().RunAsync(Shell(script, TimeSpan.FromMilliseconds(300)), null, CancellationToken.None);

        outcome.TimedOut.Should().BeTrue();
        outcome.ExitCode.Should().Be(-1);
    }

    [Fact]
    public async Task Cancellation_is_still_cancellation()
    {
        var script = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 > nul" : "sleep 30";
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var run = () => new SidecarProcessRunner().RunAsync(Shell(script), null, cts.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }
}
