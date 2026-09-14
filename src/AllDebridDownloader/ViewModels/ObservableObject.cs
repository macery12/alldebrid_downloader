using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace AllDebridDownloader.ViewModels;

/// <summary>Minimal INotifyPropertyChanged base -- no MVVM framework needed for this.</summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    /// <summary>Raise change notifications for computed properties that depend on others.</summary>
    protected void OnPropertiesChanged(params string[] names)
    {
        foreach (var n in names) OnPropertyChanged(n);
    }

    /// <summary>Marshal an action onto the UI thread, or run it now if already there.</summary>
    protected static void OnUi(Action action) => UiDispatch.Invoke(action);
}

/// <summary>
/// One place that decides whether work needs marshalling to the UI thread.
/// </summary>
public static class UiDispatch
{
    public static void Invoke(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;

        // No Application, already on the UI thread, or that thread is gone or going:
        // run inline. Posting to a dispatcher that will never pump again would drop the
        // update silently.
        if (dispatcher is null
            || dispatcher.CheckAccess()
            || dispatcher.HasShutdownStarted
            || dispatcher.HasShutdownFinished
            || !dispatcher.Thread.IsAlive)
        {
            action();
            return;
        }

        dispatcher.Post(action);
    }
}

public static class DispatcherExtensions
{
    /// <summary>Fire-and-forget dispatch that never throws into the caller.</summary>
    public static void Post(this System.Windows.Threading.Dispatcher dispatcher, Action action)
    {
        try
        {
            dispatcher.BeginInvoke(action);
        }
        catch (TaskCanceledException)
        {
            // Shutting down.
        }
    }
}

/// <summary>Standard ICommand for synchronous handlers.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => _execute(parameter);

    public void RaiseCanExecuteChanged() =>
        OnUiThread(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));

    internal static void OnUiThread(Action action) => UiDispatch.Invoke(action);
}

/// <summary>
/// ICommand for async handlers. Blocks re-entry while running and surfaces faults
/// through <see cref="OnError"/> rather than crashing on an unobserved task.
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _execute;
    private readonly Func<object?, bool>? _canExecute;
    private bool _running;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute()) { }

    public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    /// <summary>Set by the owning view model to report a failure to the user.</summary>
    public Action<Exception>? OnError { get; set; }

    public bool CanExecute(object? parameter) =>
        !_running && (_canExecute?.Invoke(parameter) ?? true);

    public async void Execute(object? parameter)
    {
        if (_running) return;

        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await _execute(parameter).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Cancelling is a normal outcome, not an error to report.
        }
        catch (Exception ex)
        {
            OnError?.Invoke(ex);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() =>
        RelayCommand.OnUiThread(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
}
