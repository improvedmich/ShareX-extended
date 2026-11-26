#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

#nullable enable

using ShareX.HelpersLib;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

public sealed class ImageFileInfo
{
    public required string FilePath { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string FolderPath { get; init; } = string.Empty;
    public string Extension { get; init; } = string.Empty;
    public long Size { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime ModifiedAt { get; init; }

    public string SizeText => Size.ToSizeString();
}

public enum ImageBrowserSortColumn
{
    Name,
    Size,
    ModifiedAt
}

// Three-state sort: none -> ascending -> descending -> none.
public enum ImageBrowserSortOrder
{
    None,
    Ascending,
    Descending
}

public sealed class ImageBrowserViewModel : INotifyPropertyChanged
{
    // Safety limit so huge recursive scans do not freeze the list.
    public const int MaxFileCount = 20_000;

    private readonly List<ImageFileInfo> _allFiles = [];
    private ImageFileInfo[] _filteredFiles = [];
    private string _folderPath = string.Empty;
    private bool _recursiveSearch;
    private string _searchText = string.Empty;
    private ImageBrowserSortColumn? _sortColumn;
    private ImageBrowserSortOrder _sortOrder = ImageBrowserSortOrder.None;
    private bool _isBusy;

    public event PropertyChangedEventHandler? PropertyChanged;

    // Raised whenever filter inputs change; the window debounces the actual refresh.
    public event Action? FilterRequested;

    // Raised after the filtered list has been rebuilt, so observers can refresh derived views.
    public event Action? FilterApplied;

    public string FolderPath
    {
        get => _folderPath;
        set
        {
            if (_folderPath == value)
            {
                return;
            }

            _folderPath = value;
            OnPropertyChanged();
        }
    }

    public bool RecursiveSearch
    {
        get => _recursiveSearch;
        set
        {
            if (_recursiveSearch == value)
            {
                return;
            }

            _recursiveSearch = value;
            OnPropertyChanged();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value)
            {
                return;
            }

            _searchText = value;
            OnPropertyChanged();
            FilterRequested?.Invoke();
        }
    }

    public ImageBrowserSortColumn? SortColumn
    {
        get => _sortColumn;
        private set
        {
            if (_sortColumn == value)
            {
                return;
            }

            _sortColumn = value;
            OnPropertyChanged();
        }
    }

    public ImageBrowserSortOrder SortOrder
    {
        get => _sortOrder;
        private set
        {
            if (_sortOrder == value)
            {
                return;
            }

            _sortOrder = value;
            OnPropertyChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value)
            {
                return;
            }

            _isBusy = value;
            OnPropertyChanged();
        }
    }

    public int TotalFiles => _allFiles.Count;

    public long TotalSize => _allFiles.Sum(file => file.Size);

    public ImageFileInfo[] FilteredFiles => _filteredFiles;

    public IReadOnlyList<ImageFileInfo> AllFiles => _allFiles;

    public void ToggleSort(ImageBrowserSortColumn column)
    {
        if (_sortColumn != column)
        {
            _sortColumn = column;
            _sortOrder = ImageBrowserSortOrder.Ascending;
        }
        else
        {
            _sortOrder = _sortOrder switch
            {
                ImageBrowserSortOrder.Ascending => ImageBrowserSortOrder.Descending,
                ImageBrowserSortOrder.Descending => ImageBrowserSortOrder.None,
                _ => ImageBrowserSortOrder.Ascending
            };

            if (_sortOrder == ImageBrowserSortOrder.None)
            {
                _sortColumn = null;
            }
        }

        OnPropertyChanged(nameof(SortColumn));
        OnPropertyChanged(nameof(SortOrder));
        FilterRequested?.Invoke();
    }

    public async Task<int> LoadFolderAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        _allFiles.Clear();
        FolderPath = folderPath;
        IsBusy = true;

        try
        {
            List<ImageFileInfo> files = await Task.Run(() =>
            {
                List<ImageFileInfo> loaded = [];

                if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                {
                    return loaded;
                }

                EnumerationOptions options = new()
                {
                    RecurseSubdirectories = RecursiveSearch,
                    IgnoreInaccessible = true
                };

                foreach (string filePath in Directory.EnumerateFiles(folderPath, "*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!FileHelpers.IsImageFile(filePath))
                    {
                        continue;
                    }

                    try
                    {
                        FileInfo info = new(filePath);
                        loaded.Add(new ImageFileInfo
                        {
                            FilePath = filePath,
                            FileName = info.Name,
                            FolderPath = info.DirectoryName ?? folderPath,
                            Extension = info.Extension.ToLowerInvariant(),
                            Size = info.Length,
                            CreatedAt = info.CreationTime,
                            ModifiedAt = info.LastWriteTime
                        });

                        if (loaded.Count >= MaxFileCount)
                        {
                            break;
                        }
                    }
                    catch (Exception exception)
                    {
                        DebugHelper.WriteException(exception, $"Failed to read file information: {filePath}");
                    }
                }

                return loaded;
            }, cancellationToken);

            _allFiles.AddRange(files);
        }
        finally
        {
            IsBusy = false;
        }

        return _allFiles.Count;
    }

    public ImageFileInfo[] ApplyFilterAndSort()
    {
        IEnumerable<ImageFileInfo> result = _allFiles;

        string search = _searchText.Trim();
        if (search.Length > 0)
        {
            result = result.Where(file =>
                file.FileName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                file.FilePath.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        if (_sortColumn != null && _sortOrder != ImageBrowserSortOrder.None)
        {
            result = (_sortColumn, _sortOrder) switch
            {
                (ImageBrowserSortColumn.Name, ImageBrowserSortOrder.Ascending) =>
                    result.OrderBy(file => file.FileName, StringComparer.OrdinalIgnoreCase),
                (ImageBrowserSortColumn.Name, ImageBrowserSortOrder.Descending) =>
                    result.OrderByDescending(file => file.FileName, StringComparer.OrdinalIgnoreCase),
                (ImageBrowserSortColumn.Size, ImageBrowserSortOrder.Ascending) =>
                    result.OrderBy(file => file.Size),
                (ImageBrowserSortColumn.Size, ImageBrowserSortOrder.Descending) =>
                    result.OrderByDescending(file => file.Size),
                (ImageBrowserSortColumn.ModifiedAt, ImageBrowserSortOrder.Ascending) =>
                    result.OrderBy(file => file.ModifiedAt),
                (ImageBrowserSortColumn.ModifiedAt, ImageBrowserSortOrder.Descending) =>
                    result.OrderByDescending(file => file.ModifiedAt),
                _ => result
            };
        }

        _filteredFiles = result.ToArray();
        OnPropertyChanged(nameof(TotalFiles));
        OnPropertyChanged(nameof(TotalSize));
        OnPropertyChanged(nameof(FilteredFiles));
        FilterApplied?.Invoke();
        return _filteredFiles;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
