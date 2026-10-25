using DropWheel.Core.Input;
using DropWheel.Core.Settings;
using FluentAssertions;

namespace DropWheel.Core.Tests;

public class EdgeDockTests
{
    private const int Zone = 2;
    private readonly List<DockSide> _asked = [];

    private DockSide? Move(EdgeDock dock, int x, int left = 0, int right = 1920, bool open = true)
        => dock.OnMove(x, left, right, Zone, side =>
        {
            _asked.Add(side);
            return open;
        });

    [Fact]
    public void Pushing_against_the_right_edge_counts_once_per_visit()
    {
        var dock = new EdgeDock(KeyBindings.Default);
        Move(dock, 900).Should().BeNull();
        Move(dock, 1917).Should().BeNull();
        Move(dock, 1918).Should().Be(DockSide.Right);
        Move(dock, 1919).Should().BeNull("still the same visit");
        Move(dock, 1800).Should().BeNull();
        Move(dock, 1919).Should().Be(DockSide.Right, "it left the edge and came back");
    }

    [Fact]
    public void The_left_edge_counts_too_on_a_monitor_left_of_the_primary()
    {
        var dock = new EdgeDock(KeyBindings.Default);
        Move(dock, -1919, left: -1920, right: 0).Should().Be(DockSide.Left);
        Move(dock, -1, left: -1920, right: 0).Should().Be(DockSide.Right, "the right edge is exclusive");
    }

    [Fact]
    public void An_edge_with_a_monitor_beyond_it_never_counts_and_is_asked_about_once()
    {
        var dock = new EdgeDock(KeyBindings.Default);
        Move(dock, 1919, open: false).Should().BeNull();
        Move(dock, 1918, open: false).Should().BeNull();

        _asked.Should().Equal(DockSide.Right);
    }

    [Fact]
    public void A_new_drag_counts_again_at_the_same_edge()
    {
        var dock = new EdgeDock(KeyBindings.Default);
        Move(dock, 1919);
        dock.Reset();
        Move(dock, 1919).Should().Be(DockSide.Right);
    }

    [Fact]
    public void Switched_off_in_settings_no_edge_counts()
    {
        var dock = new EdgeDock(KeyBindings.Default with { EdgeDock = false });
        dock.Enabled.Should().BeFalse();
        Move(dock, 1919).Should().BeNull();
        Move(dock, 0).Should().BeNull();
        _asked.Should().BeEmpty();

        dock.Rebind(KeyBindings.Default);
        Move(dock, 1919).Should().Be(DockSide.Right);
    }
}
