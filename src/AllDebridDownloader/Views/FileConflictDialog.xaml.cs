using System.Windows;
using AllDebridDownloader.Helpers;
using AllDebridDownloader.Services;

namespace AllDebridDownloader.Views;

/// <summary>
/// Asked per file when the destination already exists. Nothing is ever silently
/// overwritten. Closing the dialog without choosing cancels the rest of the batch.
/// </summary>
public partial class FileConflictDialog : Window
{
    public FileConflictDialog(ConflictRequest request)
    {
        InitializeComponent();

        FileNameText.Text = request.FileName;

        ExistingText.Text = ByteFormatter.Format(request.ExistingSize)
            + "   modified " + request.ExistingModified.ToString("yyyy-MM-dd HH:mm");

        IncomingText.Text = request.IncomingSize > 0
            ? ByteFormatter.Format(request.IncomingSize)
            : "unknown size";
    }

    public ConflictChoice Choice { get; private set; } = ConflictChoice.Cancel;

    public bool ApplyToAllRemaining => ApplyToAll.IsChecked == true;

    private void OnSkip(object sender, RoutedEventArgs e) => Finish(ConflictChoice.Skip);
    private void OnRename(object sender, RoutedEventArgs e) => Finish(ConflictChoice.Rename);
    private void OnOverwrite(object sender, RoutedEventArgs e) => Finish(ConflictChoice.Overwrite);

    private void Finish(ConflictChoice choice)
    {
        Choice = choice;
        DialogResult = true;
        Close();
    }
}
