using System.Collections.ObjectModel;
using AllDebridDownloader.Helpers;
using AllDebridDownloader.Models;

namespace AllDebridDownloader.ViewModels;

/// <summary>
/// One node in the checkbox file tree. Selection propagates down to children and up to
/// parents, with a suppression flag so the two directions cannot recurse into each other.
/// </summary>
public sealed class FileNodeViewModel : ObservableObject
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".webm", ".mpg", ".mpeg", ".flv"
    };

    private bool? _isSelected = true;
    private bool _isExpanded = true;
    private bool _suppressPropagation;

    public FileNodeViewModel(string name, bool isFolder, FileNodeViewModel? parent)
    {
        Name = name;
        IsFolder = isFolder;
        Parent = parent;
    }

    public string Name { get; }
    public bool IsFolder { get; }
    public FileNodeViewModel? Parent { get; }

    public ObservableCollection<FileNodeViewModel> Children { get; } = new();

    /// <summary>File only: byte size from the API.</summary>
    public long FileSize { get; set; }

    /// <summary>File only: the alldebrid.com/f/ link, which still needs unlocking.</summary>
    public string? Link { get; set; }

    /// <summary>Folder size is the sum of its descendants.</summary>
    public long TotalSize => IsFolder ? Children.Sum(c => c.TotalSize) : FileSize;

    public string SizeText => TotalSize > 0 ? ByteFormatter.Format(TotalSize) : "";

    public bool IsVideo => !IsFolder && VideoExtensions.Contains(Path.GetExtension(Name));

    /// <summary>
    /// Three-state: true all selected, false none, null a mix (indeterminate).
    /// </summary>
    public bool? IsSelected
    {
        get => _isSelected;
        set => SetSelected(value, fromUi: true);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>Raised whenever any node's selection changed, so totals can be recomputed.</summary>
    public event Action? SelectionChanged;

    private void SetSelected(bool? value, bool fromUi)
    {
        // A user click on an indeterminate folder means "select everything".
        if (fromUi && value is null) value = true;

        if (_isSelected == value && !fromUi) return;

        _isSelected = value;
        OnPropertyChanged(nameof(IsSelected));

        if (!_suppressPropagation && value is not null)
        {
            foreach (var child in Children) child.SetSelectedFromParent(value.Value);
        }

        if (!_suppressPropagation) Parent?.RecomputeFromChildren();

        if (fromUi) RaiseSelectionChangedToRoot();
    }

    private void SetSelectedFromParent(bool value)
    {
        _suppressPropagation = true;
        try
        {
            _isSelected = value;
            OnPropertyChanged(nameof(IsSelected));
            foreach (var child in Children) child.SetSelectedFromParent(value);
        }
        finally
        {
            _suppressPropagation = false;
        }
    }

    /// <summary>Recompute this folder's state from its children, then tell our parent.</summary>
    private void RecomputeFromChildren()
    {
        if (Children.Count == 0) return;

        var allSelected = Children.All(c => c.IsSelected == true);
        var noneSelected = Children.All(c => c.IsSelected == false);

        bool? newState = allSelected ? true : noneSelected ? false : null;

        if (_isSelected != newState)
        {
            _suppressPropagation = true;
            try
            {
                _isSelected = newState;
                OnPropertyChanged(nameof(IsSelected));
            }
            finally
            {
                _suppressPropagation = false;
            }
        }

        Parent?.RecomputeFromChildren();
    }

    private void RaiseSelectionChangedToRoot()
    {
        var node = this;
        while (node.Parent is not null) node = node.Parent;
        node.SelectionChanged?.Invoke();
    }

    /// <summary>Set this node and everything under it, without a UI-origin click.</summary>
    public void SetSelectedRecursive(bool value)
    {
        SetSelectedFromParent(value);
        Parent?.RecomputeFromChildren();
    }

    /// <summary>Select only video files; folders settle into the right tri-state.</summary>
    public void SelectVideoOnly()
    {
        ApplyPredicate(n => n.IsVideo);
        RaiseSelectionChangedToRoot();
    }

    private void ApplyPredicate(Func<FileNodeViewModel, bool> predicate)
    {
        if (IsFolder)
        {
            foreach (var child in Children) child.ApplyPredicate(predicate);
            RecomputeSelfFromChildren();
        }
        else
        {
            _suppressPropagation = true;
            try
            {
                _isSelected = predicate(this);
                OnPropertyChanged(nameof(IsSelected));
            }
            finally
            {
                _suppressPropagation = false;
            }
        }
    }

    private void RecomputeSelfFromChildren()
    {
        if (Children.Count == 0) return;

        var allSelected = Children.All(c => c.IsSelected == true);
        var noneSelected = Children.All(c => c.IsSelected == false);

        _suppressPropagation = true;
        try
        {
            _isSelected = allSelected ? true : noneSelected ? false : null;
            OnPropertyChanged(nameof(IsSelected));
        }
        finally
        {
            _suppressPropagation = false;
        }
    }

    /// <summary>Every selected file at or below this node.</summary>
    public IEnumerable<FileNodeViewModel> SelectedFiles()
    {
        if (!IsFolder)
        {
            if (IsSelected == true && !string.IsNullOrEmpty(Link)) yield return this;
            yield break;
        }

        foreach (var child in Children)
            foreach (var file in child.SelectedFiles())
                yield return file;
    }

    /// <summary>Every file at or below this node, selected or not.</summary>
    public IEnumerable<FileNodeViewModel> AllFiles()
    {
        if (!IsFolder)
        {
            yield return this;
            yield break;
        }

        foreach (var child in Children)
            foreach (var file in child.AllFiles())
                yield return file;
    }

    /// <summary>
    /// Path segments from the tree root down to this node, for rebuilding the folder
    /// structure on disk. The synthetic root itself is excluded.
    /// </summary>
    public List<string> PathSegments()
    {
        var segments = new List<string>();
        var node = this;
        while (node is not null)
        {
            if (node.Parent is not null) segments.Insert(0, node.Name);
            node = node.Parent;
        }
        return segments;
    }

    // -----------------------------------------------------------------------
    // Tree construction
    // -----------------------------------------------------------------------

    /// <summary>
    /// Build a tree from an AllDebrid file list. A synthetic root named after the
    /// torrent holds the API's top-level nodes, which is also what the user ticks to
    /// take everything.
    /// </summary>
    public static FileNodeViewModel BuildTree(string torrentName, IEnumerable<MagnetFileNode> nodes)
    {
        var root = new FileNodeViewModel(torrentName, isFolder: true, parent: null);

        foreach (var node in nodes) AddNode(root, node);

        // Default to everything selected: it is the common case, and deselecting a few
        // files is less work than selecting most of them.
        root.SetSelectedRecursive(true);
        return root;
    }

    private static void AddNode(FileNodeViewModel parent, MagnetFileNode node)
    {
        var name = string.IsNullOrWhiteSpace(node.Name) ? "unnamed" : node.Name!;

        // A folder is identified by the presence of "e", not by s/l being null.
        if (node.IsFolder)
        {
            var folder = new FileNodeViewModel(name, isFolder: true, parent);
            parent.Children.Add(folder);
            foreach (var child in node.Entries!) AddNode(folder, child);
        }
        else
        {
            var file = new FileNodeViewModel(name, isFolder: false, parent)
            {
                FileSize = node.Size ?? 0,
                Link = node.Link
            };
            parent.Children.Add(file);
        }
    }
}
