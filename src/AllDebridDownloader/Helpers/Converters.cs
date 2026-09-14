using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AllDebridDownloader.Models;
using AllDebridDownloader.Services;

namespace AllDebridDownloader.Helpers;

/// <summary>bool -> Visibility, with Invert for the negated case.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public bool UseHidden { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value switch
        {
            bool b => b,
            null => false,
            string s => !string.IsNullOrEmpty(s),
            int i => i != 0,
            long l => l != 0,
            _ => true
        };

        if (Invert) flag = !flag;
        return flag ? Visibility.Visible : UseHidden ? Visibility.Hidden : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>Formats a byte count for display.</summary>
public sealed class ByteSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            long l => ByteFormatter.Format(l),
            int i => ByteFormatter.Format(i),
            double d => ByteFormatter.Format((long)d),
            _ => "—"
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Colours a torrent or transfer row by its state.</summary>
public sealed class StateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            MagnetStatusKind.Ready => "Success",
            MagnetStatusKind.Error => "Error",
            MagnetStatusKind.Unknown => "Warn",
            MagnetStatusKind.Processing => "Accent",

            TransferState.Completed => "Success",
            TransferState.Failed => "Error",
            TransferState.Retrying => "Warn",
            TransferState.Paused => "Muted",
            TransferState.Cancelled => "Muted",
            TransferState.Skipped => "Muted",
            TransferState.Downloading => "Accent",
            TransferState.Resolving => "Accent",
            TransferState.Queued => "Muted",
            _ => "Muted"
        };

        return Application.Current?.TryFindResource(key) as Brush
            ?? new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Colours a log line by its level.</summary>
public sealed class LogLevelToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            LogLevel.Error => "Error",
            LogLevel.Warn => "Warn",
            LogLevel.Debug => "Muted",
            _ => "Text"
        };

        return Application.Current?.TryFindResource(key) as Brush
            ?? new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Picks a folder or file glyph for a tree node.</summary>
public sealed class NodeGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "📁" : "·";   // folder glyph vs a plain dot for files

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Indents a tree row without a full TreeView, used by the flattened file list.</summary>
public sealed class DepthToMarginConverter : IValueConverter
{
    public double PerLevel { get; set; } = 18;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => new Thickness(value is int depth ? depth * PerLevel : 0, 0, 0, 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
