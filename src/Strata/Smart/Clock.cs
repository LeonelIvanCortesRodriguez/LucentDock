namespace Strata.Smart;

/// <summary>Time source. Production uses the system clock; tests and previews use a fixed or manual clock.</summary>
public interface IClock
{
    DateTime Now { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime Now => DateTime.Now;
}

public sealed class ManualClock(DateTime start) : IClock
{
    public DateTime Now { get; set; } = start;
    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>The clock the UI reads. Swapped for a fixed clock in preview mode so renders are deterministic.</summary>
public static class AppClock
{
    public static IClock Current { get; set; } = new SystemClock();
    public static DateTime Now => Current.Now;
    public static DateTime Today => Current.Now.Date;
}
