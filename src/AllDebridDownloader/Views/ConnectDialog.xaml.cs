using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using AllDebridDownloader.Models;
using AllDebridDownloader.Services;

namespace AllDebridDownloader.Views;

/// <summary>
/// First-run connect dialog. Both auth paths are first-class: the PIN flow
/// (/v4.1/pin/get then polling /v4/pin/check) and pasting an API key. Either way the
/// result is a key validated against /v4/user.
/// </summary>
public partial class ConnectDialog : Window
{
    private readonly AllDebridClient _client;
    private readonly Logger _log;
    private readonly DispatcherTimer _countdown;

    private CancellationTokenSource? _pinCts;
    private PinRequest? _pin;
    private DateTime _pinExpiresAt;
    private string _typedKey = string.Empty;

    public ConnectDialog(AllDebridClient client, Logger log)
    {
        InitializeComponent();
        _client = client;
        _log = log;

        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += (_, _) => UpdateCountdown();

        Loaded += async (_, _) => await StartPinFlowAsync();
        Closed += (_, _) => StopPinFlow();
    }

    /// <summary>The validated key, set once the dialog closes successfully.</summary>
    public string? ApiKey { get; private set; }

    /// <summary>Account details fetched while validating, so the caller need not refetch.</summary>
    public UserInfo? User { get; private set; }

    private bool UsingPin => PinMode.IsChecked == true;

    // -----------------------------------------------------------------------
    // Mode switching
    // -----------------------------------------------------------------------

    private void OnModeChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        ClearError();

        PinPanel.Visibility = UsingPin ? Visibility.Visible : Visibility.Collapsed;
        KeyPanel.Visibility = UsingPin ? Visibility.Collapsed : Visibility.Visible;

        // The Connect button is only meaningful for the API key path; the PIN path
        // completes on its own.
        ConnectButton.Visibility = UsingPin ? Visibility.Collapsed : Visibility.Visible;

        if (UsingPin)
        {
            _ = StartPinFlowAsync();
        }
        else
        {
            StopPinFlow();
            KeyBox.Focus();
            UpdateConnectEnabled();
        }
    }

    // -----------------------------------------------------------------------
    // PIN flow
    // -----------------------------------------------------------------------

    private async Task StartPinFlowAsync()
    {
        if (!UsingPin) return;

        StopPinFlow();
        ClearError();

        NewPinButton.Visibility = Visibility.Collapsed;
        PinText.Text = "…";
        PinStatus.Text = "Getting a PIN…";
        PinCountdown.Text = string.Empty;
        OpenBrowserButton.IsEnabled = false;
        CopyLinkButton.IsEnabled = false;

        _pinCts = new CancellationTokenSource();
        var ct = _pinCts.Token;

        try
        {
            _pin = await _client.GetPinAsync(ct);

            if (string.IsNullOrWhiteSpace(_pin.Pin) || string.IsNullOrWhiteSpace(_pin.Check))
                throw new AllDebridApiException(null, null, "AllDebrid returned an unusable PIN.");

            PinText.Text = string.Join(" ", _pin.Pin!.ToCharArray());
            _pinExpiresAt = DateTime.UtcNow.AddSeconds(_pin.ExpiresIn > 0 ? _pin.ExpiresIn : 600);

            OpenBrowserButton.IsEnabled = true;
            CopyLinkButton.IsEnabled = true;
            PinStatus.Text = "Waiting for you to submit the PIN…";

            _countdown.Start();
            UpdateCountdown();

            _log.Info("Auth", "PIN flow started.");

            await PollPinAsync(_pin.Pin!, _pin.Check!, ct);
        }
        catch (OperationCanceledException)
        {
            // Dialog closed or mode switched.
        }
        catch (AllDebridApiException ex)
        {
            ShowError(ex.Message);
            PinStatus.Text = "Could not start the PIN flow.";
            NewPinButton.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Polls /v4/pin/check every 5 seconds, as the docs ask.</summary>
    private async Task PollPinAsync(string pin, string check, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);

            PinCheckResult result;
            try
            {
                result = await _client.CheckPinAsync(pin, check, ct);
            }
            catch (AllDebridApiException ex) when (ex.Code == "PIN_EXPIRED")
            {
                PinExpired("That PIN expired. Get a new one.");
                return;
            }
            catch (AllDebridApiException ex) when (ex.Code == "PIN_INVALID")
            {
                PinExpired("That PIN is no longer valid. Get a new one.");
                return;
            }
            catch (AllDebridApiException ex) when (ex.Code == "PIN_ALREADY_AUTHED")
            {
                ShowError("This account already has a valid API key. Use the API key option instead.");
                KeyMode.IsChecked = true;
                return;
            }
            catch (AllDebridApiException ex)
            {
                // Transient: keep polling rather than abandoning the flow.
                PinStatus.Text = "Retrying… (" + ex.Message + ")";
                continue;
            }

            if (!result.Activated)
            {
                if (result.ExpiresIn > 0)
                    _pinExpiresAt = DateTime.UtcNow.AddSeconds(result.ExpiresIn);
                PinStatus.Text = "Waiting for you to submit the PIN…";
                continue;
            }

            if (string.IsNullOrWhiteSpace(result.ApiKey))
            {
                ShowError("AllDebrid said the PIN was accepted but sent no API key.");
                return;
            }

            PinStatus.Text = "Checking the key…";
            await ValidateAndAcceptAsync(result.ApiKey!, ct);
            return;
        }
    }

    private void PinExpired(string message)
    {
        _countdown.Stop();
        PinCountdown.Text = string.Empty;
        PinStatus.Text = message;
        NewPinButton.Visibility = Visibility.Visible;
        OpenBrowserButton.IsEnabled = false;
        CopyLinkButton.IsEnabled = false;
    }

    private void UpdateCountdown()
    {
        var left = _pinExpiresAt - DateTime.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            PinExpired("That PIN expired. Get a new one.");
            _pinCts?.Cancel();
            return;
        }
        PinCountdown.Text = "(" + left.Minutes + ":" + left.Seconds.ToString("00") + " left)";
    }

    private void StopPinFlow()
    {
        _countdown.Stop();
        try { _pinCts?.Cancel(); } catch { /* already gone */ }
        _pinCts?.Dispose();
        _pinCts = null;
    }

    private async void OnNewPin(object sender, RoutedEventArgs e) => await StartPinFlowAsync();

    private void OnOpenBrowser(object sender, RoutedEventArgs e)
    {
        var url = _pin?.UserUrl;
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError("Could not open a browser: " + ex.Message + " -- use Copy link instead.");
        }
    }

    private void OnCopyLink(object sender, RoutedEventArgs e)
    {
        var url = _pin?.UserUrl;
        if (string.IsNullOrWhiteSpace(url)) return;

        try
        {
            Clipboard.SetText(url);
            PinStatus.Text = "Link copied. Open it, then submit the PIN.";
        }
        catch
        {
            ShowError("Windows would not let the app use the clipboard.");
        }
    }

    // -----------------------------------------------------------------------
    // API key flow
    // -----------------------------------------------------------------------

    private void OnKeyChanged(object sender, RoutedEventArgs e)
    {
        _typedKey = KeyBox.Password;
        UpdateConnectEnabled();
    }

    private void OnKeyVisibleChanged(object sender, RoutedEventArgs e)
    {
        _typedKey = KeyBoxVisible.Text;
        UpdateConnectEnabled();
    }

    private void OnToggleShowKey(object sender, RoutedEventArgs e)
    {
        var show = ShowKeyToggle.IsChecked == true;

        if (show)
        {
            KeyBoxVisible.Text = _typedKey;
            KeyBoxVisible.Visibility = Visibility.Visible;
            KeyBox.Visibility = Visibility.Collapsed;
            ShowKeyToggle.Content = "Hide";
            KeyBoxVisible.Focus();
            KeyBoxVisible.CaretIndex = KeyBoxVisible.Text.Length;
        }
        else
        {
            KeyBox.Password = _typedKey;
            KeyBox.Visibility = Visibility.Visible;
            KeyBoxVisible.Visibility = Visibility.Collapsed;
            ShowKeyToggle.Content = "Show";
            KeyBox.Focus();
        }
    }

    private void UpdateConnectEnabled() =>
        ConnectButton.IsEnabled = !UsingPin && _typedKey.Trim().Length >= 8;

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        var key = _typedKey.Trim();
        if (key.Length == 0) return;

        ClearError();
        ConnectButton.IsEnabled = false;
        ConnectButton.Content = "Checking…";

        try
        {
            await ValidateAndAcceptAsync(key, CancellationToken.None);
        }
        finally
        {
            ConnectButton.Content = "Connect";
            UpdateConnectEnabled();
        }
    }

    // -----------------------------------------------------------------------
    // Shared validation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Confirm the key works with /v4/user before saving it. An invalid key is never
    /// persisted.
    /// </summary>
    private async Task ValidateAndAcceptAsync(string apiKey, CancellationToken ct)
    {
        try
        {
            var user = await _client.ValidateKeyAsync(apiKey, ct);

            ApiKey = apiKey;
            User = user;
            _client.ApiKey = apiKey;

            _log.Info("Auth", "Connected as " + (user.Username ?? "(unknown)")
                + " (" + user.AccountSummary + ").");

            StopPinFlow();
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AllDebridApiException ex)
        {
            _log.Warn("Auth", "Key rejected: " + (ex.Code ?? "(no code)"));
            ShowError(ex.Message);

            if (UsingPin)
            {
                PinStatus.Text = "That key did not work.";
                NewPinButton.Visibility = Visibility.Visible;
            }
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
    }

    private void ClearError() => ErrorBanner.Visibility = Visibility.Collapsed;

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        StopPinFlow();
        DialogResult = false;
        Close();
    }
}
