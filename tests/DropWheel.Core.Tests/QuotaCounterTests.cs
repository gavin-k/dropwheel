using DropWheel.Core.Jobs;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace DropWheel.Core.Tests;

public class QuotaCounterTests
{
    [Fact]
    public void Resets_at_local_midnight()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 23, 50, 0, TimeSpan.FromHours(8)));
        clock.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("sh", TimeSpan.FromHours(8), "sh", "sh"));
        var q = new QuotaCounter(3, clock);

        q.RecordSuccess(3);
        q.CanStart().Should().BeFalse();

        clock.Advance(TimeSpan.FromMinutes(15));
        q.Used.Should().Be(0);
        q.CanStart().Should().BeTrue();
    }

    [Fact]
    public void Zero_limit_means_unlimited()
        => new QuotaCounter(0, TimeProvider.System).CanStart(1_000_000).Should().BeTrue();
}
