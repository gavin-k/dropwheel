namespace DropWheel.Core.Jobs;

/// <summary>
/// Local-only daily quota. Rules are public on the landing page and must stay in sync:
/// one successful output = 1; failed / canceled / undone / file actions = 0; resets at local midnight.
/// </summary>
public sealed class QuotaCounter(int dailyLimit, TimeProvider time)
{
    private DateOnly _day = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
    private int _used;

    public int DailyLimit { get; } = dailyLimit;

    /// <summary><c>null</c> = unlimited (Pro / lifetime).</summary>
    public bool IsUnlimited => DailyLimit <= 0;

    public int Used
    {
        get
        {
            RollOverIfNeeded();
            return _used;
        }
    }

    public int Remaining => IsUnlimited ? int.MaxValue : Math.Max(0, DailyLimit - Used);

    public bool CanStart(int outputs = 1) => IsUnlimited || Remaining >= outputs;

    public void RecordSuccess(int outputs = 1)
    {
        RollOverIfNeeded();
        _used += outputs;
    }

    private void RollOverIfNeeded()
    {
        var today = DateOnly.FromDateTime(time.GetLocalNow().DateTime);
        if (today != _day)
        {
            _day = today;
            _used = 0;
        }
    }
}
