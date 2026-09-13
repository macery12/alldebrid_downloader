using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AllDebridDownloader.Configuration;

namespace AllDebridDownloader.Services;

/// <summary>
/// Loads and saves the single config.json. Writes are atomic (tmp + move) and a
/// corrupt file is quarantined rather than allowed to crash startup.
/// </summary>
public sealed class ConfigService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private readonly Logger? _log;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ConfigService(Logger? log = null, string? appDataRoot = null)
    {
        _log = log;
        ConfigDirectory = appDataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AllDebridDownloader");
        ConfigPath = Path.Combine(ConfigDirectory, "config.json");
        LogDirectory = Path.Combine(ConfigDirectory, "logs");
    }

    public string ConfigDirectory { get; }
    public string ConfigPath { get; }
    public string LogDirectory { get; }

    public AppConfig Current { get; private set; } = new();

    public AppConfig Load()
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
        }
        catch (Exception ex)
        {
            _log?.Error("Config", "Could not create the config folder.", ex);
        }

        if (!File.Exists(ConfigPath))
        {
            Current = new AppConfig();
            Current.Normalize();
            _log?.Info("Config", "No config file yet; starting from defaults.");
            return Current;
        }

        try
        {
            var json = File.ReadAllText(ConfigPath);
            var loaded = JsonSerializer.Deserialize<AppConfig>(json, Options);
            if (loaded is null) throw new JsonException("Config deserialized to null.");
            loaded.Normalize();
            Current = loaded;
            _log?.Info("Config", "Loaded config (schema " + loaded.SchemaVersion + ").");
            return Current;
        }
        catch (Exception ex)
        {
            Quarantine(ex);
            Current = new AppConfig();
            Current.Normalize();
            return Current;
        }
    }

    private void Quarantine(Exception ex)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var bad = Path.Combine(ConfigDirectory, "config.bad-" + stamp + ".json");
        try
        {
            File.Move(ConfigPath, bad, overwrite: true);
            _log?.Error("Config",
                "Config file was unreadable and has been moved to " + Path.GetFileName(bad) +
                ". Starting from defaults.", ex);
        }
        catch (Exception moveEx)
        {
            _log?.Error("Config", "Config file was unreadable and could not be quarantined.", moveEx);
        }
    }

    public async Task SaveAsync(CancellationToken ct = default)
    {
        Current.Normalize();

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            var tmp = ConfigPath + ".tmp";
            var json = JsonSerializer.Serialize(Current, Options);
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            File.Move(tmp, ConfigPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _log?.Error("Config", "Could not save the config file.", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Fire-and-forget save, for settings controls that change constantly.</summary>
    public void SaveSoon()
    {
        _ = Task.Run(async () =>
        {
            try { await SaveAsync().ConfigureAwait(false); }
            catch { /* SaveAsync already logged it. */ }
        });
    }

    /// <summary>
    /// True when the configured download folder is present and writable. A folder that
    /// has gone away (unplugged drive) sends the user back to the folder dialog.
    /// </summary>
    public static bool IsUsableDownloadDirectory(string? path, out string? problem)
    {
        problem = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            problem = "No folder has been chosen yet.";
            return false;
        }

        if (!Path.IsPathRooted(path))
        {
            problem = "That is not a full path. Include the drive, for example D:\\Downloads.";
            return false;
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(root) && !Directory.Exists(root))
            {
                problem = "The drive " + root + " is not available.";
                return false;
            }

            Directory.CreateDirectory(path);

            // Creating the folder is not proof that we can write into it.
            var probe = Path.Combine(path, ".adw-write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            problem = "Windows denied access to that folder. Pick one inside your user folder.";
            return false;
        }
        catch (IOException ex)
        {
            problem = "That folder could not be used: " + ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            problem = "That folder could not be used: " + ex.Message;
            return false;
        }
    }

    public static string DefaultDownloadDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads",
        "AllDebrid");
}
