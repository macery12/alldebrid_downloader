using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace AllDebridDownloader.Services;

public enum LogLevel { Debug, Info, Warn, Error }

public sealed class LogEntry
{
    public DateTime Timestamp { get; init; }
    public LogLevel Level { get; init; }
    public string Category { get; init; } = "";
    public string Message { get; init; } = "";

    public override string ToString() =>
        "[" + Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "] "
        + "[" + Level.ToString().ToUpperInvariant().PadRight(5) + "] "
        + "[" + Category + "] " + Message;
}

/// <summary>
/// Small append-only file logger with an in-memory tail for the Log tab.
/// Writes happen on a background pump: logging never blocks the UI thread and never
/// throws into the caller, because a failure to log must not break a download.
/// </summary>
public sealed class Logger : IDisposable
{
    private const int MaxBufferedEntries = 5000;
    private const int RetainDays = 7;

    private readonly BlockingCollection<LogEntry> _queue = new(new ConcurrentQueue<LogEntry>());
    private readonly Task _pump;
    private readonly string _logDirectory;

    public Logger(string logDirectory, LogLevel minimumLevel = LogLevel.Info)
    {
        _logDirectory = logDirectory;
        MinimumLevel = minimumLevel;

        try
        {
            Directory.CreateDirectory(_logDirectory);
            PruneOldLogs();
        }
        catch
        {
            // A logger that cannot create its folder still has to let the app run.
        }

        _pump = Task.Run(PumpAsync);
    }

    public LogLevel MinimumLevel { get; set; }

    /// <summary>Bounded in-memory tail, newest last. Bound to by the Log tab.</summary>
    public ConcurrentQueue<LogEntry> Buffer { get; } = new();

    /// <summary>Raised on the thread that logged; the UI marshals it itself.</summary>
    public event Action<LogEntry>? EntryLogged;

    public string LogDirectory => _logDirectory;

    public void Debug(string category, string message) => Write(LogLevel.Debug, category, message);
    public void Info(string category, string message) => Write(LogLevel.Info, category, message);
    public void Warn(string category, string message) => Write(LogLevel.Warn, category, message);

    public void Error(string category, string message, Exception? ex = null) =>
        Write(LogLevel.Error, category, ex is null ? message : message + " -- " + Describe(ex));

    private static string Describe(Exception ex)
    {
        var sb = new StringBuilder();
        var current = ex;
        var depth = 0;
        while (current is not null && depth < 5)
        {
            if (depth > 0) sb.Append(" <- ");
            sb.Append(current.GetType().Name).Append(": ").Append(current.Message);
            current = current.InnerException;
            depth++;
        }
        return sb.ToString();
    }

    private void Write(LogLevel level, string category, string message)
    {
        if (level < MinimumLevel) return;

        var entry = new LogEntry
        {
            Timestamp = DateTime.Now,
            Level = level,
            Category = category,
            Message = message
        };

        Buffer.Enqueue(entry);
        while (Buffer.Count > MaxBufferedEntries) Buffer.TryDequeue(out _);

        try
        {
            EntryLogged?.Invoke(entry);
        }
        catch
        {
            // A misbehaving subscriber must not stop the log.
        }

        try
        {
            if (!_queue.IsAddingCompleted) _queue.Add(entry);
        }
        catch (InvalidOperationException)
        {
            // Shutting down.
        }
    }

    private async Task PumpAsync()
    {
        foreach (var entry in _queue.GetConsumingEnumerable())
        {
            try
            {
                var path = Path.Combine(
                    _logDirectory,
                    "app-" + entry.Timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log");
                await File.AppendAllTextAsync(path, entry + Environment.NewLine, Encoding.UTF8)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Disk full, file locked, folder gone: drop the line, keep the app alive.
            }
        }
    }

    private void PruneOldLogs()
    {
        var cutoff = DateTime.Now.Date.AddDays(-RetainDays);
        foreach (var file in Directory.EnumerateFiles(_logDirectory, "app-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file).Date < cutoff) File.Delete(file);
            }
            catch
            {
                // In use or not ours: leave it.
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _queue.CompleteAdding();
            _pump.Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Best effort on shutdown.
        }
        _queue.Dispose();
    }
}
