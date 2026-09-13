using System.Windows;
using System.Windows.Threading;
using AllDebridDownloader.Services;
using AllDebridDownloader.ViewModels;
using AllDebridDownloader.Views;

namespace AllDebridDownloader;

public partial class App : Application
{
    private Logger? _log;
    private ConfigService? _configService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var verbose = e.Args.Any(a =>
            a.Equals("--verbose", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("-v", StringComparison.OrdinalIgnoreCase));

        // Config has to be located before logging, since the log folder lives beside it.
        _configService = new ConfigService();
        _log = new Logger(_configService.LogDirectory, verbose ? LogLevel.Debug : LogLevel.Info);
        _configService = new ConfigService(_log);

        var config = _configService.Load();

        _log.Info("App", "AllDebridDownloader 1.0.0 starting"
            + (verbose ? " (verbose logging)" : "") + ".");

        // A crash in a UI handler or an orphaned task should apologise, not vanish.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        var vm = new MainViewModel(_log, _configService);

        // Connect (API key or PIN) comes first, then the output folder.
        if (!TryConnect(vm, config.ApiKey))
        {
            Shutdown();
            return;
        }

        if (!EnsureDownloadFolder(vm))
        {
            Shutdown();
            return;
        }

        var window = new MainWindow { DataContext = vm };
        window.UseWindowState(() => _configService.Current.WindowState);
        MainWindow = window;
        window.Show();

        _ = vm.StartAsync();
    }

    /// <summary>
    /// Validate a saved key silently; only show the connect dialog when there is no
    /// usable key. Returns false if the user cancelled out of connecting.
    /// </summary>
    private bool TryConnect(MainViewModel vm, string? savedKey)
    {
        if (!string.IsNullOrWhiteSpace(savedKey) && vm.TryUseSavedKey(savedKey))
            return true;

        var dialog = new ConnectDialog(vm.Client, _log!);
        var result = dialog.ShowDialog();

        if (result != true || string.IsNullOrWhiteSpace(dialog.ApiKey)) return false;

        vm.ApplyConnectedKey(dialog.ApiKey!, dialog.User);
        return true;
    }

    private bool EnsureDownloadFolder(MainViewModel vm)
    {
        var current = _configService!.Current.DownloadDirectory;

        if (ConfigService.IsUsableDownloadDirectory(current, out _)) return true;

        var banner = current is null
            ? null
            : "The folder that was saved (" + current + ") is not available any more.";

        var dialog = new DownloadFolderDialog(current ?? ConfigService.DefaultDownloadDirectory, banner);
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.SelectedPath))
            return false;

        _configService.Current.DownloadDirectory = dialog.SelectedPath;
        _configService.SaveSoon();
        vm.RaiseDownloadDirectoryChanged();

        _log!.Info("Config", "Download folder set.");
        return true;
    }

    // -----------------------------------------------------------------------
    // Crash handling
    // -----------------------------------------------------------------------

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log?.Error("App", "Unhandled UI exception.", e.Exception);
        ShowCrashDialog(e.Exception);
        e.Handled = true;   // stay alive; a broken click should not lose a download
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _log?.Error("App", "Unobserved task exception.", e.Exception);
        e.SetObserved();
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex) _log?.Error("App", "Fatal exception.", ex);
    }

    private void ShowCrashDialog(Exception ex)
    {
        try
        {
            MessageBox.Show(
                "Something went wrong.\n\n" + ex.Message
                + "\n\nThe details have been written to the log; the app is still running.",
                "AllDebrid Downloader",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch
        {
            // If even the message box fails there is nothing useful left to do.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _log?.Info("App", "Shutting down.");
        _log?.Dispose();
        base.OnExit(e);
    }
}
