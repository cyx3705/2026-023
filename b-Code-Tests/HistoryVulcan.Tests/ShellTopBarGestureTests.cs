using System.Windows;
using HistoryVulcan.Shell;
using Xunit;

namespace HistoryVulcan.Tests;

public sealed class ShellTopBarGestureTests
{
    [Fact]
    public void FastDoubleClickAcceptsExactlyTwoHundredFiftyMilliseconds()
    {
        var gesture = new FastDoubleClickGesture(TimeSpan.FromMilliseconds(250));

        Assert.False(gesture.RegisterPress("header:console", 1_000, new Point(10, 10), 4, 4));
        Assert.True(gesture.RegisterPress("header:console", 1_250, new Point(14, 14), 4, 4));
    }

    [Fact]
    public void FastDoubleClickRejectsTwoHundredFiftyOneMilliseconds()
    {
        var gesture = new FastDoubleClickGesture(TimeSpan.FromMilliseconds(250));

        Assert.False(gesture.RegisterPress("header:console", 1_000, new Point(10, 10), 4, 4));
        Assert.False(gesture.RegisterPress("header:console", 1_251, new Point(10, 10), 4, 4));
    }

    [Fact]
    public void FastDoubleClickRequiresTheSameTarget()
    {
        var gesture = new FastDoubleClickGesture(TimeSpan.FromMilliseconds(250));

        Assert.False(gesture.RegisterPress("header:console", 1_000, new Point(10, 10), 4, 4));
        Assert.False(gesture.RegisterPress("header:modules", 1_100, new Point(10, 10), 4, 4));
        Assert.True(gesture.RegisterPress("header:modules", 1_200, new Point(10, 10), 4, 4));
    }

    [Fact]
    public void FastDoubleClickRejectsMovementOutsideTheConfiguredRange()
    {
        var gesture = new FastDoubleClickGesture(TimeSpan.FromMilliseconds(250));

        Assert.False(gesture.RegisterPress("header:console", 1_000, new Point(10, 10), 4, 4));
        Assert.False(gesture.RegisterPress("header:console", 1_100, new Point(14.1, 10), 4, 4));
    }

    [Fact]
    public void DraggingCancelsThePendingDoubleClick()
    {
        var gesture = new FastDoubleClickGesture(TimeSpan.FromMilliseconds(250));

        Assert.False(gesture.RegisterPress("header:console", 1_000, new Point(10, 10), 4, 4));
        gesture.Cancel("header:console");
        Assert.False(gesture.RegisterPress("header:console", 1_100, new Point(10, 10), 4, 4));
    }

    [Fact]
    public void MaximizedWindowRequiresTwiceTheSystemDragThreshold()
    {
        var start = new Point(10, 10);

        Assert.False(ShellTopBarCoordinator.HasReachedDragThreshold(
            start, new Point(17.99, 10), 4, 4, multiplier: 2));
        Assert.True(ShellTopBarCoordinator.HasReachedDragThreshold(
            start, new Point(18, 10), 4, 4, multiplier: 2));
        Assert.True(ShellTopBarCoordinator.HasReachedDragThreshold(
            start, new Point(10, 18), 4, 4, multiplier: 2));
    }

    [Theory]
    [InlineData(true, WindowState.Normal, false)]
    [InlineData(true, WindowState.Maximized, true)]
    [InlineData(false, WindowState.Normal, true)]
    [InlineData(false, WindowState.Maximized, true)]
    public void OnlyNormalMainWindowSkipsTheTopBarHold(
        bool isMainWindow,
        WindowState state,
        bool expected)
        => Assert.Equal(
            expected,
            ShellTopBarCoordinator.ShouldDelayHostDrag(isMainWindow, state));

    [Fact]
    public void DelayedDragRejectsOneHundredNineteenMilliseconds()
    {
        var gesture = new DelayedDragGesture(TimeSpan.FromMilliseconds(120));
        gesture.Begin(1_000, new Point(10, 10), 4, 4);

        Assert.False(gesture.Update(1_119, new Point(14, 10)));
        Assert.True(gesture.IsActive);
    }

    [Fact]
    public void DelayedDragAcceptsOneHundredTwentyMilliseconds()
    {
        var gesture = new DelayedDragGesture(TimeSpan.FromMilliseconds(120));
        gesture.Begin(1_000, new Point(10, 10), 4, 4);

        Assert.True(gesture.Update(1_120, new Point(14, 10)));
    }

    [Fact]
    public void DelayedDragRemembersEarlyMovementUntilHoldCompletes()
    {
        var gesture = new DelayedDragGesture(TimeSpan.FromMilliseconds(120));
        gesture.Begin(1_000, new Point(10, 10), 4, 4);

        Assert.False(gesture.Update(1_050, new Point(18, 10)));
        Assert.True(gesture.HasReachedThreshold);
        Assert.False(gesture.TryActivate(1_119));
        Assert.True(gesture.TryActivate(1_120));
    }

    [Fact]
    public void DelayedDragWaitsForMovementAfterHoldCompletes()
    {
        var gesture = new DelayedDragGesture(TimeSpan.FromMilliseconds(120));
        gesture.Begin(1_000, new Point(10, 10), 4, 4);

        Assert.False(gesture.TryActivate(1_120));
        Assert.False(gesture.Update(1_121, new Point(13.99, 10)));
        Assert.True(gesture.Update(1_122, new Point(14, 10)));
    }

    [Fact]
    public void DelayedDragCancellationPreventsActivation()
    {
        var gesture = new DelayedDragGesture(TimeSpan.FromMilliseconds(120));
        gesture.Begin(1_000, new Point(10, 10), 4, 4);
        gesture.Update(1_050, new Point(18, 10));

        gesture.Cancel();

        Assert.False(gesture.IsActive);
        Assert.False(gesture.HasReachedThreshold);
        Assert.False(gesture.TryActivate(1_120));
    }

    [Fact]
    public void MovementBeforeTheHoldExpiresInvalidatesThePreviousClick()
    {
        var doubleClick = new FastDoubleClickGesture(TimeSpan.FromMilliseconds(250));
        var drag = new DelayedDragGesture(TimeSpan.FromMilliseconds(120));
        Assert.False(doubleClick.RegisterPress("header:main", 1_000, new Point(10, 10), 4, 4));
        drag.Begin(1_000, new Point(10, 10), 4, 4);

        Assert.False(drag.Update(1_050, new Point(18, 10)));
        Assert.True(drag.HasReachedThreshold);
        doubleClick.Cancel("header:main");

        Assert.False(doubleClick.RegisterPress("header:main", 1_100, new Point(10, 10), 4, 4));
    }

    [Theory]
    [InlineData(WindowState.Normal, WindowState.Maximized)]
    [InlineData(WindowState.Maximized, WindowState.Normal)]
    [InlineData(WindowState.Minimized, WindowState.Maximized)]
    public void FloatingWindowToggleHasOneDeterministicTarget(
        WindowState current,
        WindowState expected)
        => Assert.Equal(expected, ShellTopBarCoordinator.GetToggledWindowState(current));

    [Theory]
    [InlineData(96, 720, 520)]
    [InlineData(120, 900, 650)]
    [InlineData(144, 1080, 780)]
    public void FloatingPlacementConvertsDipSizeToMonitorPixels(
        double dpi,
        int expectedWidth,
        int expectedHeight)
    {
        var placement = FloatingWindowGeometry.CalculatePlacement(
            new Point(1_000, 500),
            new Rect(0, 0, 1_920, 1_080),
            new Size(720, 520),
            new Point(100, 20),
            dpi,
            dpi);

        Assert.Equal(expectedWidth, placement.Width);
        Assert.Equal(expectedHeight, placement.Height);
    }

    [Fact]
    public void FloatingPlacementKeepsPointerAtTheOriginalTabAnchor()
    {
        var placement = FloatingWindowGeometry.CalculatePlacement(
            new Point(1_000, 500),
            new Rect(0, 0, 1_920, 1_080),
            new Size(720, 520),
            new Point(120, 18),
            96,
            96);

        Assert.Equal(880, placement.Left);
        Assert.Equal(482, placement.Top);
    }

    [Fact]
    public void FloatingPlacementShrinksOnlyWhenLargerThanTheWorkArea()
    {
        var placement = FloatingWindowGeometry.CalculatePlacement(
            new Point(-500, 200),
            new Rect(-1_280, 0, 1_280, 720),
            new Size(2_000, 1_000),
            new Point(100, 20),
            96,
            96);

        Assert.Equal(1_280, placement.Width);
        Assert.Equal(720, placement.Height);
        Assert.Equal(-1_280, placement.Left);
        Assert.Equal(0, placement.Top);
    }
}
