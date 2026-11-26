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

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShareX.HelpersLib;
using System.Buffers;
using System.Security;
using System.Security.Cryptography;

namespace ShareX.Tools;

public sealed record SearchModeItem(DuplicateFinderSearchMode Mode, string DisplayName);

public static class DuplicateFinderSearchModeExtensions
{
    public static IReadOnlyList<SearchModeItem> All { get; } =
    [
        new(DuplicateFinderSearchMode.ByName, Localization.Strings.DuplicateFinderViewModel_ByName),
        new(DuplicateFinderSearchMode.ByHash, Localization.Strings.DuplicateFinderViewModel_ByHash)
    ];
}

public enum DuplicateFinderSearchMode
{
    ByName,
    ByHash
}

public sealed record DuplicateFileData(string Path, long Size, string? Hash = null);

public sealed record DuplicateGroup(
    string Title,
    IReadOnlyList<DuplicateFileData> Files,
    long ReclaimableSize,
    string? Hash = null)
{
    public string ReclaimableText => ReclaimableSize.ToSizeString();
    public string TotalSizeText => Files.Sum(f => f.Size).ToSizeString();
    public string FilesText => string.Format(Localization.Strings.DuplicateFinderViewModel_N_files, Files.Count);
    public bool HasHash => !string.IsNullOrEmpty(Hash);
    public string HashText => string.Format(Localization.Strings.DuplicateFinderViewModel_Hash, Hash);
}

public readonly record struct ScanProgress(double Percent, string? Status = null);

public readonly record struct DuplicateScanResult(IReadOnlyList<DuplicateGroup> Groups, int FileCount);

public sealed partial class DuplicateFinderViewModel : ViewModelBase, IDisposable
{
    private const int HashBufferSize = 1024 * 1024;
    private const int ProgressReportInterval = 100;

    private CancellationTokenSource? _cancellationTokenSource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanScan))]
    [NotifyPropertyChangedFor(nameof(NoDuplicatesMessage))]
    private string _folderPath = string.Empty;

    [ObservableProperty]
    private SearchModeItem _selectedMode = DuplicateFinderSearchModeExtensions.All[0];

    [ObservableProperty]
    private bool _skipHiddenItems;

    [ObservableProperty]
    private decimal _maxDepth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResults))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private IReadOnlyList<DuplicateGroup> _groups = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResults))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyPropertyChangedFor(nameof(TotalDuplicatesText))]
    private long _totalDuplicates;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalReclaimableText))]
    [NotifyPropertyChangedFor(nameof(TotalReclaimableDescription))]
    private long _totalReclaimable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyPropertyChangedFor(nameof(CanScan))]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    private bool _isScanning;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    [NotifyPropertyChangedFor(nameof(NoDuplicatesMessage))]
    private string _statusText = string.Empty;

    public Func<Task<string?>>? SelectFolderRequested { get; set; }
    public Func<string, Task>? CopyPathRequested { get; set; }

    public bool IsIdle => !IsScanning;
    public bool CanScan => !IsScanning && Directory.Exists(FolderPath);
    public bool HasResults => TotalDuplicates > 0;
    public bool ShowEmptyState => IsIdle && !HasResults;
    public bool HasStatus => !string.IsNullOrWhiteSpace(StatusText);

    public string NoDuplicatesMessage =>
        !string.IsNullOrWhiteSpace(StatusText)
            ? StatusText
            : string.IsNullOrWhiteSpace(FolderPath)
                ? Localization.Strings.DuplicateFinderViewModel_Select_a_folder
                : Localization.Strings.DuplicateFinderViewModel_Press_scan;

    public string TotalReclaimableText => TotalReclaimable.ToSizeString();

    public string TotalReclaimableDescription =>
        string.Format(Localization.Strings.DuplicateFinderWindow_Total_reclaimable, TotalReclaimableText);

    public string TotalDuplicatesText =>
        string.Format(Localization.Strings.DuplicateFinderViewModel_Total_duplicates, TotalDuplicates);

    [RelayCommand]
    private async Task BrowseFolderAsync()
    {
        if (IsScanning || SelectFolderRequested == null)
        {
            return;
        }

        string? folder = await SelectFolderRequested();
        if (!string.IsNullOrWhiteSpace(folder))
        {
            FolderPath = folder;
            await ScanAsync();
        }
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (!CanScan)
        {
            return;
        }

        ResetResults();
        IsScanning = true;

        _cancellationTokenSource?.Cancel();
        _cancellationTokenSource?.Dispose();
        _cancellationTokenSource = new CancellationTokenSource();
        CancellationToken ct = _cancellationTokenSource.Token;

        string folderPath = FolderPath;
        DuplicateFinderSearchMode mode = SelectedMode.Mode;
        bool skipHiddenItems = SkipHiddenItems;
        int maxDepth = (int)Math.Max(0, MaxDepth);

        IProgress<ScanProgress> progress = new System.Progress<ScanProgress>(p =>
        {
            Progress = p.Percent;

            if (!string.IsNullOrEmpty(p.Status))
            {
                StatusText = p.Status;
            }
        });

        try
        {
            DuplicateScanResult result = await Task.Run(() => mode == DuplicateFinderSearchMode.ByName
                ? FindByName(folderPath, skipHiddenItems, maxDepth, progress, ct)
                : FindByHash(folderPath, skipHiddenItems, maxDepth, progress, ct), ct);

            ct.ThrowIfCancellationRequested();

            Groups = result.Groups;
            TotalDuplicates = result.Groups.Sum(g => (long)g.Files.Count);
            TotalReclaimable = result.Groups.Sum(g => g.ReclaimableSize);
            Progress = 100;

            StatusText = result.FileCount == 0
                ? Localization.Strings.DuplicateFinderViewModel_No_files_in_folder
                : result.Groups.Count == 0
                    ? Localization.Strings.DuplicateFinderViewModel_No_duplicates_found
                    : string.Format(Localization.Strings.DuplicateFinderViewModel_Duplicates_found, result.Groups.Count, TotalDuplicates);
        }
        catch (OperationCanceledException)
        {
            StatusText = Localization.Strings.DuplicateFinderViewModel_Scan_stopped;
        }
        catch (Exception ex)
        {
            StatusText = string.Format(Localization.Strings.DuplicateFinderViewModel_Scan_failed, ex.Message);
            ToolsDiagnostics.ReportWarning(nameof(DuplicateFinderViewModel), "Duplicate scan failed.", ex);
        }
        finally
        {
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
            IsScanning = false;
            Progress = 0;
        }
    }

    [RelayCommand]
    private void Stop()
    {
        if (IsScanning)
        {
            StatusText = Localization.Strings.DuplicateFinderViewModel_Stopping;
            _cancellationTokenSource?.Cancel();
        }
    }

    [RelayCommand]
    private async Task CopyPathAsync(string path)
    {
        if (!string.IsNullOrEmpty(path) && CopyPathRequested != null)
        {
            await CopyPathRequested(path);
        }
    }

    [RelayCommand]
    private void OpenParentFolder(string filePath)
    {
        if (!string.IsNullOrEmpty(filePath))
        {
            FileHelpers.OpenFolderWithFile(filePath);
        }
    }

    private static DuplicateScanResult FindByName(string rootPath, bool skipHiddenItems, int maxDepth,
        IProgress<ScanProgress> progress, CancellationToken ct)
    {
        List<string> files = CollectFiles(rootPath, skipHiddenItems, maxDepth, progress, ct);
        int total = files.Count;

        if (total == 0)
        {
            return new DuplicateScanResult([], 0);
        }

        Dictionary<string, List<DuplicateFileData>> groups = new(StringComparer.Ordinal);

        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                string file = files[i];
                string name = Path.GetFileName(file);
                long size = new FileInfo(file).Length;

                if (!groups.TryGetValue(name, out List<DuplicateFileData>? list))
                {
                    list = [];
                    groups[name] = list;
                }

                list.Add(new DuplicateFileData(file, size));
            }
            catch (UnauthorizedAccessException) { }
            catch (FileNotFoundException) { }
            catch (IOException) { }

            if (i % ProgressReportInterval == 0 || i == total - 1)
            {
                progress.Report(new ScanProgress((double)(i + 1) / total * 100));
            }
        }

        List<DuplicateGroup> result = groups
            .Where(g => g.Value.Count > 1)
            .Select(g =>
            {
                long totalSize = g.Value.Sum(f => f.Size);
                long reclaimable = totalSize - g.Value.Max(f => f.Size);
                return new DuplicateGroup(g.Key, g.Value, reclaimable);
            })
            .OrderByDescending(g => g.ReclaimableSize)
            .ToList();

        return new DuplicateScanResult(result, total);
    }

    private static DuplicateScanResult FindByHash(string rootPath, bool skipHiddenItems, int maxDepth,
        IProgress<ScanProgress> progress, CancellationToken ct)
    {
        List<string> files = CollectFiles(rootPath, skipHiddenItems, maxDepth, progress, ct);
        int total = files.Count;

        if (total == 0)
        {
            return new DuplicateScanResult([], 0);
        }

        Dictionary<long, List<DuplicateFileData>> sizeGroups = new();

        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                string file = files[i];
                long size = new FileInfo(file).Length;

                if (!sizeGroups.TryGetValue(size, out List<DuplicateFileData>? list))
                {
                    list = [];
                    sizeGroups[size] = list;
                }

                list.Add(new DuplicateFileData(file, size));
            }
            catch (UnauthorizedAccessException) { }
            catch (FileNotFoundException) { }
            catch (IOException) { }

            if (i % ProgressReportInterval == 0 || i == total - 1)
            {
                progress.Report(new ScanProgress((double)(i + 1) / total * 30,
                    string.Format(Localization.Strings.DuplicateFinderViewModel_Reading_files, i + 1)));
            }
        }

        List<List<DuplicateFileData>> candidates = sizeGroups.Values.Where(g => g.Count > 1).ToList();
        int hashTotal = candidates.Sum(g => g.Count);
        int hashProcessed = 0;

        List<DuplicateGroup> result = new();
        byte[] hashBuffer = ArrayPool<byte>.Shared.Rent(HashBufferSize);

        try
        {
            foreach (List<DuplicateFileData> sizeGroup in candidates)
            {
                ct.ThrowIfCancellationRequested();

                Dictionary<string, List<DuplicateFileData>> hashGroups = new(StringComparer.Ordinal);

                foreach (DuplicateFileData item in sizeGroup)
                {
                    ct.ThrowIfCancellationRequested();

                    string? hash = ComputeSha256(item.Path, hashBuffer, ct);

                    if (hash != null)
                    {
                        DuplicateFileData hashed = item with { Hash = hash };

                        if (!hashGroups.TryGetValue(hash, out List<DuplicateFileData>? list))
                        {
                            list = [];
                            hashGroups[hash] = list;
                        }

                        list.Add(hashed);
                    }

                    hashProcessed++;

                    if (hashProcessed == 1 || hashProcessed % ProgressReportInterval == 0 || hashProcessed == hashTotal)
                    {
                        progress.Report(new ScanProgress(30 + (double)hashProcessed / Math.Max(hashTotal, 1) * 70,
                            string.Format(Localization.Strings.DuplicateFinderViewModel_Hashing_files, hashProcessed, hashTotal)));
                    }
                }

                foreach (List<DuplicateFileData> hashGroup in hashGroups.Values.Where(g => g.Count > 1))
                {
                    long totalSize = hashGroup.Sum(f => f.Size);
                    long reclaimable = totalSize - hashGroup[0].Size;
                    result.Add(new DuplicateGroup(Path.GetFileName(hashGroup[0].Path), hashGroup, reclaimable, hashGroup[0].Hash));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(hashBuffer);
        }

        return new DuplicateScanResult(result.OrderByDescending(g => g.ReclaimableSize).ToList(), total);
    }

    private static string? ComputeSha256(string filePath, byte[] buffer, CancellationToken ct)
    {
        try
        {
            using SHA256 sha256 = SHA256.Create();
            using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);

            int read;

            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                sha256.TransformBlock(buffer, 0, read, null, 0);
            }

            sha256.TransformFinalBlock(buffer, 0, 0);

            byte[]? hash = sha256.Hash;
            return hash == null ? null : Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (UnauthorizedAccessException) { return null; }
        catch (FileNotFoundException) { return null; }
        catch (IOException) { return null; }
    }

    private static List<string> CollectFiles(string rootPath, bool skipHiddenItems, int maxDepth,
        IProgress<ScanProgress> progress, CancellationToken ct)
    {
        FileAttributes skipAttributes = skipHiddenItems
            ? FileAttributes.Hidden | FileAttributes.System
            : 0;

        EnumerationOptions fileOptions = new()
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = skipAttributes
        };

        EnumerationOptions directoryOptions = new()
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = skipAttributes | FileAttributes.ReparsePoint
        };

        List<string> files = new();
        Stack<(string Path, int Depth)> pending = new();
        pending.Push((Path.TrimEndingDirectorySeparator(rootPath), 0));

        int directoriesProcessed = 0;

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            (string currentPath, int currentDepth) = pending.Pop();

            try
            {
                foreach (string file in Directory.EnumerateFiles(currentPath, "*", fileOptions))
                {
                    ct.ThrowIfCancellationRequested();
                    files.Add(file);
                }
            }
            catch (Exception ex) when (IsEnumerationException(ex)) { }

            directoriesProcessed++;

            if (directoriesProcessed % ProgressReportInterval == 0)
            {
                progress.Report(new ScanProgress(0,
                    string.Format(Localization.Strings.DuplicateFinderViewModel_Reading_files, files.Count)));
            }

            if (maxDepth > 0 && currentDepth >= maxDepth)
            {
                continue;
            }

            try
            {
                foreach (string subDirectory in Directory.EnumerateDirectories(currentPath, "*", directoryOptions))
                {
                    ct.ThrowIfCancellationRequested();
                    pending.Push((subDirectory, currentDepth + 1));
                }
            }
            catch (Exception ex) when (IsEnumerationException(ex)) { }
        }

        progress.Report(new ScanProgress(0,
            string.Format(Localization.Strings.DuplicateFinderViewModel_Reading_files, files.Count)));

        return files;
    }

    private static bool IsEnumerationException(Exception ex) =>
        ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException or SecurityException;

    public void SetFolder(string folderPath)
    {
        if (!IsScanning && Directory.Exists(folderPath))
        {
            FolderPath = folderPath;
        }
    }

    private void ResetResults()
    {
        Groups = [];
        TotalDuplicates = 0;
        TotalReclaimable = 0;
        StatusText = string.Empty;
        Progress = 0;
    }

    partial void OnFolderPathChanged(string value) => ResetResults();

    partial void OnSelectedModeChanged(SearchModeItem value) => ResetResults();

    public void Dispose()
    {
        _cancellationTokenSource?.Cancel();
    }
}
