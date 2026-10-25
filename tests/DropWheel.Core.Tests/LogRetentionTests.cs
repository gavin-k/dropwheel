using DropWheel.Core.Privacy;
using FluentAssertions;

namespace DropWheel.Core.Tests;

public class LogRetentionTests
{
    private static readonly DateOnly Today = new(2026, 10, 25);

    [Fact]
    public void The_last_seven_days_are_kept_and_older_logs_go()
    {
        string[] files = [Log("20261025"), Log("20261019"), Log("20261018"), Log("20250101")];

        LogRetention.Expired(files, Today).Should().Equal(Log("20261018"), Log("20250101"));
    }

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("dropwheel-old.log")]
    [InlineData("dropwheel-20200101.txt")]
    [InlineData("other-20200101.log")]
    public void Files_that_are_not_daily_logs_are_left_alone(string name)
        => LogRetention.Expired([name], Today).Should().BeEmpty();

    private static string Log(string day) => Path.Combine("logs", $"dropwheel-{day}.log");
}
