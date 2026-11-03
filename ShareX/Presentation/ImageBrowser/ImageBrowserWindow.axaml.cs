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

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ShareX.AvaloniaUI.Theming;
using ShareX.HelpersLib;
using ShareX.Localization;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

public partial class ImageBrowserWindow : Window
{
    private const int FilterDebounceMilliseconds = 220;
    // Guard against extremely wide group breakdowns.
    private const int MaxDisplayedGroups = 25;
    // Number of distinct group values rendered per page; the "show more" button adds another page.
    private const int ValuesPerPage = 100;

    private readonly ImageBrowserViewModel _viewModel = new();
    private readonly DispatcherTimer _filterTimer;
    private readonly DispatcherTimer _statsTimer;
    private readonly ImageBrowserOptions _fallbackOptions = new();
    private readonly Dictionary<string, int> _groupValueLimits = [];
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _previewCancellation;
    private CancellationTokenSource? _statsCancellation;
    private ImageBrowserRegexStats? _lastStats;
    private Bitmap? _previewBitmap;
    private bool _suppressEvents;

    private static ImageBrowserOptions SharedOptions =>
        ApplicationState.DefaultTaskSettings?.ImageBrowserOptions ?? throw new InvalidOperationException("Task settings have not been loaded.");

    public ImageBrowserWindow()
    {
        InitializeComponent();
        RequestedThemeVariant = ThemeManager.GetCurrentTheme();

        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FilterDebounceMilliseconds) };
        _filterTimer.Tick += OnFilterTimerTick;
        _statsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FilterDebounceMilliseconds) };
        _statsTimer.Tick += OnStatsTimerTick;
        _viewModel.FilterRequested += ScheduleFilter;

        Opened += OnOpened;
        Closed += OnClosed;
        KeyDown += OnWindowKeyDown;
    }

    private ImageBrowserOptions Options
    {
        get
        {
            try
            {
                return SharedOptions;
            }
            catch (InvalidOperationException)
            {
                // Allows the window to be created before the application settings are loaded (design-time/tests).
                return _fallbackOptions;
            }
        }
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        Activate();
        Focus();

        _suppressEvents = true;
        RecursiveCheckBox.IsChecked = Options.RecursiveSearch;
        StatsRegexTextBox.Text = Options.StatsRegexPattern;
        _suppressEvents = false;

        _viewModel.RecursiveSearch = Options.RecursiveSearch;

        string folder = TaskHelpers.GetScreenshotsFolder();
        await LoadFolderAsync(folder);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _filterTimer.Stop();
        _statsTimer.Stop();
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _statsCancellation?.Cancel();
        _statsCancellation?.Dispose();
        _previewBitmap?.Dispose();
    }

    private async Task LoadFolderAsync(string folderPath)
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        CancellationToken token = _loadCancellation.Token;

        LoadingState.IsVisible = Directory.Exists(folderPath);
        EmptyState.IsVisible = false;
        FileList.ItemsSource = null;
        await UpdatePreviewAsync(null);

        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            FolderPathText.Text = string.Empty;
            LoadingState.IsVisible = false;
            EmptyStateText.Text = Strings.ImageBrowserWindow_Folder_missing;
            EmptyState.IsVisible = true;
            UpdateTitle(0);
            return;
        }

        try
        {
            int count = await _viewModel.LoadFolderAsync(folderPath, token);
            FolderPathText.Text = folderPath;
            UpdateTitle(count);
            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, $"Failed to load image files from '{folderPath}'.");
            LoadingState.IsVisible = false;
            EmptyStateText.Text = Strings.ImageBrowserWindow_Folder_missing;
            EmptyState.IsVisible = true;
        }
    }

    private void ScheduleFilter()
    {
        if (_suppressEvents)
        {
            return;
        }

        _filterTimer.Stop();
        _filterTimer.Start();
    }

    private async void OnFilterTimerTick(object? sender, EventArgs e)
    {
        _filterTimer.Stop();
        ApplyFilter();

        if (_viewModel.TotalFiles == 0 && !string.IsNullOrEmpty(_viewModel.FolderPath) && Directory.Exists(_viewModel.FolderPath))
        {
            await LoadFolderAsync(_viewModel.FolderPath);
        }
    }

    private void ApplyFilter()
    {
        ImageFileInfo[] items = _viewModel.ApplyFilterAndSort();
        FileList.ItemsSource = items;
        UpdateTitle(items.Length);
        LoadingState.IsVisible = false;
        EmptyState.IsVisible = items.Length == 0;
        EmptyStateText.Text = _viewModel.TotalFiles == 0
            ? Strings.ImageBrowserWindow_No_files
            : Strings.ImageBrowserWindow_No_matches;

        if (items.Length > 0)
        {
            FileList.SelectedIndex = 0;
        }
        else
        {
            _ = UpdatePreviewAsync(null);
        }

        UpdateSortIcons();
    }

    private void UpdateTitle(int itemCount)
    {
        Title = $"{Strings.ImageBrowserWindow_Title} - {string.Format(Strings.ImageBrowserWindow_Items_format, itemCount)}";
    }

    private void UpdateSortIcons()
    {
        SortNameIcon.Text = GetSortIcon(ImageBrowserSortColumn.Name);
        SortSizeIcon.Text = GetSortIcon(ImageBrowserSortColumn.Size);
        SortModifiedIcon.Text = GetSortIcon(ImageBrowserSortColumn.ModifiedAt);
    }

    private string GetSortIcon(ImageBrowserSortColumn column)
    {
        if (_viewModel.SortColumn != column)
        {
            return string.Empty;
        }

        return _viewModel.SortOrder switch
        {
            ImageBrowserSortOrder.Ascending => LucideIcons.arrow_up,
            ImageBrowserSortOrder.Descending => LucideIcons.arrow_down,
            _ => string.Empty
        };
    }

    private void OnSortNameClick(object? sender, RoutedEventArgs e) => _viewModel.ToggleSort(ImageBrowserSortColumn.Name);

    private void OnSortSizeClick(object? sender, RoutedEventArgs e) => _viewModel.ToggleSort(ImageBrowserSortColumn.Size);

    private void OnSortModifiedClick(object? sender, RoutedEventArgs e) => _viewModel.ToggleSort(ImageBrowserSortColumn.ModifiedAt);

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        _viewModel.SearchText = SearchTextBox.Text ?? string.Empty;
    }

    private void OnRecursiveCheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        bool recursive = RecursiveCheckBox.IsChecked == true;
        Options.RecursiveSearch = recursive;
        _viewModel.RecursiveSearch = recursive;

        if (!string.IsNullOrEmpty(_viewModel.FolderPath))
        {
            _ = LoadFolderAsync(_viewModel.FolderPath);
        }
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        string folder = string.IsNullOrEmpty(_viewModel.FolderPath)
            ? TaskHelpers.GetScreenshotsFolder()
            : _viewModel.FolderPath;
        _ = LoadFolderAsync(folder);
    }

    private async void OnBrowseFolderClick(object? sender, RoutedEventArgs e)
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.ImageBrowserWindow_Choose_folder,
            AllowMultiple = false
        });

        string? folderPath = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrEmpty(folderPath))
        {
            await LoadFolderAsync(folderPath);
        }
    }

    private async void OnFileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        await UpdatePreviewAsync(GetSelectedItem());
    }

    private ImageFileInfo? GetSelectedItem() => FileList.SelectedItem as ImageFileInfo;

    private async Task UpdatePreviewAsync(ImageFileInfo? file)
    {
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
        _previewCancellation = new CancellationTokenSource();
        CancellationToken token = _previewCancellation.Token;

        Bitmap? oldBitmap = _previewBitmap;
        _previewBitmap = null;
        PreviewImage.Source = null;
        oldBitmap?.Dispose();

        PreviewInfoPanel.IsVisible = file != null;
        PreviewContent.IsVisible = file != null;
        NoSelectionState.IsVisible = file == null;

        if (file == null)
        {
            return;
        }

        PreviewUnavailable.IsVisible = true;
        PreviewFileName.Text = file.FileName;

        List<string> details =
        [
            $"{Strings.ImageBrowserWindow_Size}: {file.SizeText}",
            $"{Strings.ImageBrowserWindow_Modified}: {file.ModifiedAt.ToString("g", CultureInfo.CurrentCulture)}"
        ];

        if (!string.IsNullOrEmpty(file.FolderPath))
        {
            details.Add($"{Strings.ImageBrowserWindow_Folder_path}: {file.FolderPath}");
        }

        string detailsText = string.Join(Environment.NewLine, details);
        PreviewDetails.Text = detailsText;

        if (!File.Exists(file.FilePath))
        {
            return;
        }

        try
        {
            Bitmap bitmap = await Task.Run(() => new Bitmap(file.FilePath), token);

            if (token.IsCancellationRequested)
            {
                bitmap.Dispose();
                return;
            }

            _previewBitmap = bitmap;
            PreviewImage.Source = bitmap;
            PreviewUnavailable.IsVisible = false;

            string dimensions = $"{bitmap.PixelSize.Width} × {bitmap.PixelSize.Height}";
            PreviewDetails.Text = $"{Strings.ImageBrowserWindow_Dimensions}: {dimensions}" + Environment.NewLine + detailsText;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, $"Failed to load preview for '{file.FilePath}'.");
        }
    }

    private void OnFileDoubleTapped(object? sender, TappedEventArgs e) => OpenSelectedImageInViewer();

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(PreviewContent).Properties.IsLeftButtonPressed)
        {
            OpenSelectedImageInViewer();
        }
    }

    private void OpenSelectedImageInViewer()
    {
        ImageFileInfo? file = GetSelectedItem();
        if (file == null)
        {
            return;
        }

        string[] paths = _viewModel.FilteredFiles.Select(item => item.FilePath).ToArray();
        int index = Array.FindIndex(paths, path => string.Equals(path, file.FilePath, StringComparison.OrdinalIgnoreCase));
        ImageViewerWindowIntegration.ShowImage(paths, Math.Max(index, 0));
    }

    private void OnOpenFileClick(object? sender, RoutedEventArgs e)
    {
        ImageFileInfo? file = GetSelectedItem();
        if (file != null)
        {
            FileHelpers.OpenFile(file.FilePath);
        }
    }

    private void OnOpenFolderClick(object? sender, RoutedEventArgs e)
    {
        ImageFileInfo? file = GetSelectedItem();
        if (file != null)
        {
            FileHelpers.OpenFolderWithFile(file.FilePath);
        }
    }

    private void OnCopyFilePathClick(object? sender, RoutedEventArgs e)
    {
        ImageFileInfo? file = GetSelectedItem();
        if (file != null)
        {
            ClipboardHelpers.CopyText(file.FilePath);
        }
    }

    private void OnCopyFileNameClick(object? sender, RoutedEventArgs e)
    {
        ImageFileInfo? file = GetSelectedItem();
        if (file != null)
        {
            ClipboardHelpers.CopyText(file.FileName);
        }
    }

    private async void OnStatsClick(object? sender, RoutedEventArgs e)
    {
        ModalOverlay.IsVisible = true;
        StatsDialog.IsVisible = true;
        StatsRegexTextBox.Focus();
        await RefreshStatsAsync();
    }

    private void OnStatsRegexTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        Options.StatsRegexPattern = StatsRegexTextBox.Text ?? string.Empty;

        // Recompute only while the statistics dialog is open; opening the window must not trigger stats work.
        if (ModalOverlay.IsVisible && !string.IsNullOrEmpty(_viewModel.FolderPath))
        {
            _statsTimer.Stop();
            _statsTimer.Start();
        }
    }

    private async void OnStatsTimerTick(object? sender, EventArgs e)
    {
        _statsTimer.Stop();

        if (ModalOverlay.IsVisible)
        {
            await RefreshStatsAsync();
        }
    }

    private async Task RefreshStatsAsync()
    {
        _statsCancellation?.Cancel();
        _statsCancellation?.Dispose();
        _statsCancellation = new CancellationTokenSource();
        CancellationToken token = _statsCancellation.Token;

        try
        {
            ImageBrowserRegexStats stats = await _viewModel.ComputeRegexStatsAsync(
                StatsRegexTextBox.Text ?? string.Empty, token);

            if (token.IsCancellationRequested)
            {
                return;
            }

            _lastStats = stats;
            _groupValueLimits.Clear();
            RenderStats();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, "Failed to compute statistics.");
        }
    }

    private void RenderStats()
    {
        ImageBrowserRegexStats? stats = _lastStats;

        StatsGroupsPanel.Children.Clear();

        if (stats == null)
        {
            StatsSummaryText.Text = string.Empty;
            return;
        }

        StatsErrorText.IsVisible = stats.HasError;
        StatsErrorText.Text = stats.Error ?? string.Empty;

        if (stats.HasError)
        {
            StatsSummaryText.Text = string.Empty;
            return;
        }

        NumberFormatInfo format = CultureInfo.CurrentCulture.NumberFormat;

        StatsSummaryText.Text = string.Join("  |  ",
        [
            $"{Strings.ImageBrowserWindow_Total}: {_viewModel.TotalFiles.ToString(format)} ({_viewModel.TotalSize.ToSizeString()})",
            $"{Strings.ImageBrowserWindow_Filtered}: {stats.TotalFiles.ToString(format)} ({(stats.MatchedSize + stats.UnmatchedSize).ToSizeString()})",
            $"{Strings.ImageBrowserWindow_Matched}: {stats.MatchedFiles.ToString(format)} ({stats.MatchedSize.ToSizeString()})",
            $"{Strings.ImageBrowserWindow_Unmatched}: {stats.UnmatchedFiles.ToString(format)} ({stats.UnmatchedSize.ToSizeString()})"
        ]);

        string pattern = StatsRegexTextBox.Text ?? string.Empty;

        if (string.IsNullOrWhiteSpace(pattern))
        {
            StatsGroupsPanel.Children.Add(CreateStatsNote(Strings.ImageBrowserWindow_Groups_hint));
            return;
        }

        IReadOnlyList<ImageBrowserGroupStats> groups = stats.Groups ?? [];

        if (groups.Count == 0)
        {
            StatsGroupsPanel.Children.Add(CreateStatsNote(Strings.ImageBrowserWindow_Groups_no_named_groups));
            return;
        }

        foreach (ImageBrowserGroupStats group in groups.Take(MaxDisplayedGroups))
        {
            StatsGroupsPanel.Children.Add(CreateStatsGroupPanel(group, format));
        }

        if (groups.Count > MaxDisplayedGroups)
        {
            StatsGroupsPanel.Children.Add(CreateStatsNote(
                string.Format(Strings.ImageBrowserWindow_More_groups_format, groups.Count - MaxDisplayedGroups)));
        }
    }

    private Control CreateStatsGroupPanel(ImageBrowserGroupStats group, NumberFormatInfo format)
    {
        StackPanel panel = new() { Spacing = 3 };

        panel.Children.Add(new TextBlock
        {
            Text = $"(?<{group.GroupName}>)  ·  {string.Format(Strings.ImageBrowserWindow_Group_files_format, group.FilesWithGroup.ToString(format))}",
            FontWeight = FontWeight.SemiBold
        });

        panel.Children.Add(CreateStatsRow(Strings.ImageBrowserWindow_Group_value,
            Strings.ImageBrowserWindow_Group_count, Strings.ImageBrowserWindow_Size, isHeader: true));

        int limit = _groupValueLimits.TryGetValue(group.GroupName, out int savedLimit)
            ? savedLimit
            : ValuesPerPage;

        foreach (ImageBrowserGroupValue value in group.Values.Take(limit))
        {
            panel.Children.Add(CreateStatsRow(value.Value, value.Count.ToString(format), value.SizeText, isHeader: false));
        }

        int remaining = group.Values.Count - limit;

        if (remaining > 0)
        {
            Button showMoreButton = new()
            {
                Content = string.Format(Strings.ImageBrowserWindow_More_values_format, remaining),
                Tag = group.GroupName,
                HorizontalAlignment = HorizontalAlignment.Left,
                Height = 28,
                Padding = new Thickness(8, 2),
                Background = null,
                BorderThickness = new Thickness(0),
                FontWeight = FontWeight.Normal,
                Foreground = SecondaryTextBrush
            };
            showMoreButton.Click += OnShowMoreValuesClick;
            panel.Children.Add(showMoreButton);
        }

        return panel;
    }

    private void OnShowMoreValuesClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string groupName })
        {
            _groupValueLimits[groupName] =
                (_groupValueLimits.TryGetValue(groupName, out int limit) ? limit : ValuesPerPage) + ValuesPerPage;
            RenderStats();
        }
    }

    private Grid CreateStatsRow(string value, string count, string size, bool isHeader)
    {
        Grid row = new()
        {
            ColumnDefinitions = new ColumnDefinitions("*,80,100"),
            ColumnSpacing = 8
        };

        // Never assign a null local value: it would override the themed Foreground style setter
        // and render the text invisible.
        IBrush? foreground = isHeader ? SecondaryTextBrush : PrimaryTextBrush;
        FontWeight weight = isHeader ? FontWeight.SemiBold : FontWeight.Normal;

        TextBlock valueText = new()
        {
            Text = value,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = weight
        };

        if (foreground != null)
        {
            valueText.Foreground = foreground;
        }

        row.Children.Add(valueText);

        TextBlock countText = new()
        {
            Text = count,
            HorizontalAlignment = HorizontalAlignment.Right,
            FontWeight = weight
        };

        if (foreground != null)
        {
            countText.Foreground = foreground;
        }

        Grid.SetColumn(countText, 1);
        row.Children.Add(countText);

        TextBlock sizeText = new()
        {
            Text = size,
            HorizontalAlignment = HorizontalAlignment.Right,
            FontWeight = weight
        };

        if (foreground != null)
        {
            sizeText.Foreground = foreground;
        }

        Grid.SetColumn(sizeText, 2);
        row.Children.Add(sizeText);

        return row;
    }

    private TextBlock CreateStatsNote(string text) => new()
    {
        Text = text,
        FontWeight = FontWeight.Normal,
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap
    };

    private IBrush? SecondaryTextBrush => this.FindResource("ShareX.Brush.Text.Secondary") as IBrush;

    private IBrush? PrimaryTextBrush => this.FindResource("ShareX.Brush.Text") as IBrush;

    private void OnCloseModalClick(object? sender, RoutedEventArgs e)
    {
        ModalOverlay.IsVisible = false;
        StatsDialog.IsVisible = false;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (ModalOverlay.IsVisible)
                {
                    OnCloseModalClick(sender, e);
                }
                else
                {
                    Close();
                }
                e.Handled = true;
                break;
            case Key.F5:
                OnRefreshClick(sender, e);
                e.Handled = true;
                break;
            case Key.Enter:
                if (FileList.IsFocused)
                {
                    OpenSelectedImageInViewer();
                    e.Handled = true;
                }
                break;
            case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control | KeyModifiers.Shift):
                OnCopyFilePathClick(sender, e);
                e.Handled = true;
                break;
            case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                OnCopyFileNameClick(sender, e);
                e.Handled = true;
                break;
        }
    }
}
