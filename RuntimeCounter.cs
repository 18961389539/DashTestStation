using System.Diagnostics;
using System.Globalization;

namespace DustLoadScada;

// Accumulates the time a PLC coil stays at 1. Intervals are only counted between two successful reads.
internal sealed class RuntimeCounter(TimeSpan total)
{
    private static readonly TimeSpan MaxStep = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(10);
    private long? lastOnTimestamp;
    private TimeSpan unsaved;

    public TimeSpan Total { get; private set; } = total;
    public bool? Running { get; private set; }
    public bool Dirty => unsaved > TimeSpan.Zero;

    // Returns true when the caller should persist the total now.
    public bool Sample(bool on, long timestamp)
    {
        if (lastOnTimestamp is long previous)
        {
            var step = Stopwatch.GetElapsedTime(previous, timestamp);
            if (step > TimeSpan.Zero && step <= MaxStep)
            {
                Total += step;
                unsaved += step;
            }
        }
        Running = on;
        lastOnTimestamp = on ? timestamp : null;
        return Dirty && (!on || unsaved >= SaveInterval);
    }

    public void Lost()
    {
        Running = null;
        lastOnTimestamp = null;
    }

    public void Reset()
    {
        Total = TimeSpan.Zero;
        unsaved = TimeSpan.Zero;
        lastOnTimestamp = Running == true ? Stopwatch.GetTimestamp() : null;
    }

    public void MarkSaved() => unsaved = TimeSpan.Zero;

    public static string Format(TimeSpan value) => string.Create(CultureInfo.InvariantCulture,
        $"{(long)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}");
}
