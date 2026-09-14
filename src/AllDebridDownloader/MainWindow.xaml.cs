using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AllDebridDownloader.Services;
using AllDebridDownloader.ViewModels;
using AllDebridDownloader.Views;

namespace AllDebridDownloader;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<string> _logLines = new();
    private LogLevel _minimumDisplayedLevel = LogLevel.Info;
    private bool _closingConfirmed;

    public MainWindow()
    {
        InitializeComponent();

        LogList.ItemsSource = _logLines;

        LogLevelFilter.ItemsSource = Enum.GetValues<LogLevel>();
        LogLevelFilter.SelectedItem = LogLevel.Info;

        DataContextChanged += OnDataContextChanged;

        Loaded += OnLoaded;
        Closing += OnClosing;

        // Drag-and-drop for .torrent files and magnet text.
        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        Drop += OnDrop;
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (Vm is null) return;

        // Seed the log view with whatever happened before the window existed.
        foreach (var entry in Vm.Log.Buffer)
            if (entry.Level >= _minimumDisplayedLevel) _logLines.Add(entry.ToString());

        Vm.Log.EntryLogged += OnLogEntry;
        Vm.TorrentBroughtToTop += OnTorrentBroughtToTop;

        Vm.Settings.ChangeApiKeyRequested = () => ChangeApiKey(usePin: false);
        Vm.Settings.SignOutRequested = SignOut;
    }

    /// <summary>
    /// Bring a just-submitted torrent into view. The grid virtualises its rows, so being
    /// at the top of the collection is not enough when it is scrolled down.
    /// </summary>
    private void OnTorrentBroughtToTop(ViewModels.TorrentViewModel torrent)
    {
        Dispatcher.BeginInvoke(() =>
        {
            try
            {
                TorrentGrid.ScrollIntoView(torrent);
            }
            catch
            {
                // Scrolling is a convenience; never let it break an add.
            }
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Vm is null) return;

        // Restore the saved window geometry, clamped to a visible area.
        var ws = _windowStateAccessor?.Invoke();
        if (ws is not null)
        {
            Width = Math.Max(MinWidth, ws.Width);
            Height = Math.Max(MinHeight, ws.Height);

            if (ws.Left is not null && ws.Top is not null
                && ws.Left.Value + 100 < SystemParameters.VirtualScreenWidth
                && ws.Top.Value + 100 < SystemParameters.VirtualScreenHeight
                && ws.Left.Value > -50 && ws.Top.Value > -50)
            {
                Left = ws.Left.Value;
                Top = ws.Top.Value;
            }

            if (ws.Maximized) WindowState = WindowState.Maximized;
        }
    }

    private Func<Configuration.WindowStateConfig>? _windowStateAccessor;

    /// <summary>Lets App supply the persisted geometry without another DI hop.</summary>
    public void UseWindowState(Func<Configuration.WindowStateConfig> accessor) =>
        _windowStateAccessor = accessor;

    // -----------------------------------------------------------------------
    // Log tab
    // -----------------------------------------------------------------------

    private void OnLogEntry(LogEntry entry)
    {
        if (entry.Level < _minimumDisplayedLevel) return;

        Dispatcher.BeginInvoke(() =>
        {
            _logLines.Add(entry.ToString());

            // Keep the visible tail bounded; the file on disk has everything.
            while (_logLines.Count > 3000) _logLines.RemoveAt(0);

            if (AutoScrollLog.IsChecked == true && _logLines.Count > 0)
                LogList.ScrollIntoView(_logLines[^1]);
        });
    }

    private void OnLogFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogLevelFilter.SelectedItem is not LogLevel level || Vm is null) return;

        _minimumDisplayedLevel = level;

        _logLines.Clear();
        foreach (var entry in Vm.Log.Buffer)
            if (entry.Level >= level) _logLines.Add(entry.ToString());
    }

    private void OnCopyLog(object sender, RoutedEventArgs e)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var line in _logLines) sb.AppendLine(line);
            Clipboard.SetText(sb.ToString());
        }
        catch
        {
            // Clipboard unavailable; nothing worth interrupting the user over.
        }
    }

    // -----------------------------------------------------------------------
    // Add box
    // -----------------------------------------------------------------------

    private void OnMagnetBoxKeyDown(object sender, KeyEventArgs e)
    {
        // Enter adds; Shift+Enter inserts a newline for pasting several magnets.
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;

        e.Handled = true;
        if (Vm?.AddMagnetCommand.CanExecute(null) == true) Vm.AddMagnetCommand.Execute(null);
    }

    // -----------------------------------------------------------------------
    // Drag and drop
    // -----------------------------------------------------------------------

    private static bool HasUsableDropData(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.FileDrop)) return true;

        if (data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text))
        {
            var text = (data.GetData(DataFormats.UnicodeText) ?? data.GetData(DataFormats.Text))
                as string;
            return !string.IsNullOrWhiteSpace(text);
        }

        return false;
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (HasUsableDropData(e.Data)) DropOverlay.Visibility = Visibility.Visible;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = HasUsableDropData(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (Vm is null) return;

        if (e.Data.GetDataPresent(DataFormats.FileDrop)
            && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            // Non-.torrent files are filtered out quietly rather than failing the drop.
            var torrents = paths
                .Where(p => p.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (torrents.Count > 0)
            {
                await Vm.AddTorrentFilesAsync(torrents);
                return;
            }
        }

        var text = (e.Data.GetData(DataFormats.UnicodeText) ?? e.Data.GetData(DataFormats.Text))
            as string;

        if (string.IsNullOrWhiteSpace(text)) return;

        var magnets = text
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(MainViewModel.LooksLikeMagnet)
            .ToList();

        if (magnets.Count == 0) return;

        Vm.MagnetInput = string.Join(Environment.NewLine, magnets);
        if (Vm.AddMagnetCommand.CanExecute(null)) Vm.AddMagnetCommand.Execute(null);
    }

    // -----------------------------------------------------------------------
    // Account buttons
    // -----------------------------------------------------------------------

    private void OnChangeApiKey(object sender, RoutedEventArgs e) => ChangeApiKey(usePin: false);

    private void OnRelinkWithPin(object sender, RoutedEventArgs e) => ChangeApiKey(usePin: true);

    private void ChangeApiKey(bool usePin)
    {
        if (Vm is null) return;

        var dialog = new ConnectDialog(Vm.Client, Vm.Log) { Owner = this };
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.ApiKey)) return;

        Vm.ApplyConnectedKey(dialog.ApiKey!, dialog.User);
        Vm.StatusMessage = Vm.AccountText;
    }

    private void OnSignOut(object sender, RoutedEventArgs e) => SignOut();

    private void SignOut()
    {
        if (Vm is null) return;

        var confirm = MessageBox.Show(
            "Forget the saved API key?\n\nDownloads already on this PC are not affected.",
            "Sign out",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        Vm.SignOut();

        MessageBox.Show("The API key has been removed. Restart the app to connect again.",
            "Signed out", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // -----------------------------------------------------------------------
    // Shutdown
    // -----------------------------------------------------------------------

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closingConfirmed || Vm is null) return;

        // Don't lose 30 GB of progress to a stray Alt+F4.
        if (Vm.HasActiveDownloads)
        {
            var answer = MessageBox.Show(
                "Downloads are still running.\n\n"
                + "They will be paused and can be resumed next time you open the app. Close now?",
                "Close AllDebrid Downloader",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        e.Cancel = true;     // hold the window open while state is flushed
        _closingConfirmed = true;

        SaveWindowState();

        try
        {
            await Vm.ShutdownAsync();
        }
        catch
        {
            // Nothing useful to do while closing.
        }

        Vm.Dispose();
        Close();
    }

    private void SaveWindowState()
    {
        var state = _windowStateAccessor?.Invoke();
        if (state is null) return;

        state.Maximized = WindowState == WindowState.Maximized;

        // Record the restore bounds, not the maximized ones.
        if (WindowState == WindowState.Normal)
        {
            state.Width = Width;
            state.Height = Height;
            state.Left = Left;
            state.Top = Top;
        }
        else
        {
            state.Width = RestoreBounds.Width;
            state.Height = RestoreBounds.Height;
            state.Left = RestoreBounds.Left;
            state.Top = RestoreBounds.Top;
        }
    }
}
