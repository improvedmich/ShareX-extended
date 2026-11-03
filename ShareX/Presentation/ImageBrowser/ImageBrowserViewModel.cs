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
using System.Text.RegularExpressions;
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

public sealed record ImageBrowserGroupValue(string Value, int Count, long Size)
{
    public string SizeText => Size.ToSizeString();
}

// Values hold the full sorted breakdown; the window decides how many to display.
public sealed record ImageBrowserGroupStats(string GroupName, int FilesWithGroup, IReadOnlyList<ImageBrowserGroupValue> Values);

public sealed record ImageBrowserRegexStats(
    int TotalFiles,
    int MatchedFiles,
    long MatchedSize,
    int UnmatchedFiles,
    long UnmatchedSize,
    string? Error = null,
    IReadOnlyList<ImageBrowserGroupStats>? Groups = null)
{
    public bool HasError => !string.IsNullOrEmpty(Error);
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
        return _filteredFiles;
    }

    private readonly record struct GroupValueStats(int Count, long Size);

    public Task<ImageBrowserRegexStats> ComputeRegexStatsAsync(string pattern, CancellationToken cancellationToken = default)
    {
        // Snapshot for the worker thread; ApplyFilterAndSort always replaces the array instead of mutating it.
        ImageFileInfo[] files = _filteredFiles;

        return Task.Run(() =>
        {
            Regex? regex = null;
            string[] groupNames = [];

            if (!string.IsNullOrWhiteSpace(pattern))
            {
                try
                {
                    regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                        TimeSpan.FromMilliseconds(500));
                    // GetGroupNames returns "0", "1", ... plus declared named groups; keep only the named ones.
                    groupNames = regex.GetGroupNames().Where(name => !int.TryParse(name, out _)).ToArray();
                }
                catch (ArgumentException exception)
                {
                    return new ImageBrowserRegexStats(files.Length, 0, 0, 0, 0, Error: exception.Message);
                }
            }

            int matchedFiles = 0;
            long matchedSize = 0;

            Dictionary<string, Dictionary<string, GroupValueStats>>? groupData =
                regex != null && groupNames.Length > 0
                    ? groupNames.ToDictionary(name => name, _ => new Dictionary<string, GroupValueStats>(StringComparer.OrdinalIgnoreCase))
                    : null;

            long totalSize = 0;

            try
            {
                foreach (ImageFileInfo file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    totalSize += file.Size;

                    if (regex == null)
                    {
                        matchedFiles++;
                        matchedSize += file.Size;
                        continue;
                    }

                    Match match = regex.Match(file.FileName);

                    if (!match.Success)
                    {
                        continue;
                    }

                    matchedFiles++;
                    matchedSize += file.Size;

                    if (groupData != null)
                    {
                        foreach (string groupName in groupNames)
                        {
                            Group group = match.Groups[groupName];

                            if (group.Success && !string.IsNullOrEmpty(group.Value))
                            {
                                Dictionary<string, GroupValueStats> values = groupData[groupName];
                                values.TryGetValue(group.Value, out GroupValueStats stats);
                                values[group.Value] = new GroupValueStats(stats.Count + 1, stats.Size + file.Size);
                            }
                        }
                    }
                }
            }
            catch (RegexMatchTimeoutException exception)
            {
                return new ImageBrowserRegexStats(files.Length, 0, 0, 0, 0, exception.Message);
            }

            // Groups stay in declaration order of the regular expression (GetGroupNames order).
            List<ImageBrowserGroupStats> groupStats = [];

            if (groupData != null)
            {
                foreach (string groupName in groupNames)
                {
                    KeyValuePair<string, GroupValueStats>[] sortedValues = groupData[groupName]
                        .OrderByDescending(pair => pair.Value.Count)
                        .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                        .ToArray();

                    if (sortedValues.Length == 0)
                    {
                        continue;
                    }

                    int filesWithGroup = sortedValues.Sum(pair => pair.Value.Count);

                    groupStats.Add(new ImageBrowserGroupStats(groupName, filesWithGroup,
                        [.. sortedValues.Select(pair => new ImageBrowserGroupValue(pair.Key, pair.Value.Count, pair.Value.Size))]));
                }
            }

            return new ImageBrowserRegexStats(files.Length, matchedFiles, matchedSize,
                files.Length - matchedFiles, totalSize - matchedSize, Groups: groupStats);
        }, cancellationToken);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
