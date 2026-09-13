namespace AllDebridDownloader.Helpers;

public static class TimeFormatter
{
    /// <summary>Formats an ETA as hh:mm:ss, or an em dash when it is unknowable.</summary>
    public static string FormatEta(TimeSpan? eta)
    {
        if (eta is null) return "—";
        var v = eta.Value;
        if (v < TimeSpan.Zero || v.TotalDays >= 7) return "—";
        return v.TotalHours >= 1
            ? $"{(int)v.TotalHours:00}:{v.Minutes:00}:{v.Seconds:00}"
            : $"00:{v.Minutes:00}:{v.Seconds:00}";
    }

    /// <summary>ETA from remaining bytes and a measured rate. Null when not computable.</summary>
    public static TimeSpan? EstimateEta(long remainingBytes, double bytesPerSecond)
    {
        if (remainingBytes <= 0) return TimeSpan.Zero;
        if (bytesPerSecond <= 1) return null;
        var seconds = remainingBytes / bytesPerSecond;
        return seconds > TimeSpan.MaxValue.TotalSeconds - 1 ? null : TimeSpan.FromSeconds(seconds);
    }
}
