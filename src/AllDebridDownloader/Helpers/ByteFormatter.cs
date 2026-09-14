using System.Globalization;

namespace AllDebridDownloader.Helpers;

public static class ByteFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>Human-readable size, e.g. "2.1 GB". Binary (1024) units.</summary>
    public static string Format(long bytes)
    {
        if (bytes < 0) return "—";
        if (bytes == 0) return "0 B";

        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // Bytes never get a decimal; small values get one more digit of precision.
        var digits = unit == 0 ? 0 : value < 10 ? 2 : value < 100 ? 1 : 0;
        return string.Create(CultureInfo.InvariantCulture,
            $"{Math.Round(value, digits).ToString("N" + digits, CultureInfo.InvariantCulture)} {Units[unit]}");
    }

    /// <summary>Human-readable throughput, e.g. "82 MB/s".</summary>
    public static string FormatRate(double bytesPerSecond)
    {
        if (double.IsNaN(bytesPerSecond) || bytesPerSecond <= 0) return "—";
        return Format((long)Math.Round(bytesPerSecond)) + "/s";
    }
}
