using System.Windows;

namespace HistoryVulcan.Shell;

internal sealed class DelayedDragGesture(TimeSpan holdDuration)
{
    private readonly long _holdMilliseconds = checked((long)holdDuration.TotalMilliseconds);
    private long _pressedAt;
    private Point _start;
    private Point _latest;
    private double _horizontalThreshold;
    private double _verticalThreshold;

    public bool IsActive { get; private set; }
    public bool HasReachedThreshold { get; private set; }

    public void Begin(
        long timestamp,
        Point start,
        double horizontalThreshold,
        double verticalThreshold)
    {
        _pressedAt = timestamp;
        _start = start;
        _latest = start;
        _horizontalThreshold = horizontalThreshold;
        _verticalThreshold = verticalThreshold;
        IsActive = true;
        HasReachedThreshold = false;
    }

    public bool Update(long timestamp, Point position)
    {
        if (!IsActive)
            return false;

        _latest = position;
        HasReachedThreshold = Math.Abs(_latest.X - _start.X) >= _horizontalThreshold ||
                              Math.Abs(_latest.Y - _start.Y) >= _verticalThreshold;
        return CanStart(timestamp);
    }

    public bool TryActivate(long timestamp)
        => IsActive && CanStart(timestamp);

    public void Cancel()
    {
        IsActive = false;
        HasReachedThreshold = false;
    }

    private bool CanStart(long timestamp)
        => timestamp >= _pressedAt &&
           timestamp - _pressedAt >= _holdMilliseconds &&
           HasReachedThreshold;
}
