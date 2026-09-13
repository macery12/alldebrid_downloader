using System.Text.Json.Serialization;

namespace AllDebridDownloader.Configuration;

public enum ExistingFilePolicy
{
    Ask,
    Overwrite,
    Skip,
    Rename
}

public sealed class WindowStateConfig
{
    public double Width { get; set; } = 1180;
    public double Height { get; set; } = 800;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>
/// The whole of the app's persisted settings. Serialized to
/// %APPDATA%\AllDebridDownloader\config.json.
/// </summary>
public sealed class AppConfig
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>AllDebrid auth apikey. Never log this, never put it in a URL.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Root output folder. Each torrent gets its own subfolder underneath.</summary>
    public string? DownloadDirectory { get; set; }

    public int MaxConcurrentFiles { get; set; } = 3;
    public int ConnectionsPerFile { get; set; } = 8;
    public long GlobalSpeedLimitBytesPerSecond { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ExistingFilePolicy ExistingFilePolicy { get; set; } = ExistingFilePolicy.Ask;

    public bool ResumeOnStartup { get; set; } = true;
    public int StatusPollIntervalSeconds { get; set; } = 3;
    public bool DeleteMagnetAfterDownload { get; set; }
    public string? LastTorrentPickerDirectory { get; set; }

    public WindowStateConfig WindowState { get; set; } = new();

    /// <summary>Clamp every tunable into its supported range.</summary>
    public void Normalize()
    {
        if (SchemaVersion <= 0) SchemaVersion = CurrentSchemaVersion;
        MaxConcurrentFiles = Math.Clamp(MaxConcurrentFiles, 1, 8);
        ConnectionsPerFile = Math.Clamp(ConnectionsPerFile, 1, 16);
        StatusPollIntervalSeconds = Math.Clamp(StatusPollIntervalSeconds, 2, 15);
        if (GlobalSpeedLimitBytesPerSecond < 0) GlobalSpeedLimitBytesPerSecond = 0;
        if (WindowState.Width < 900) WindowState.Width = 900;
        if (WindowState.Height < 600) WindowState.Height = 600;
        if (string.IsNullOrWhiteSpace(ApiKey)) ApiKey = null;
        if (string.IsNullOrWhiteSpace(DownloadDirectory)) DownloadDirectory = null;
    }
}
