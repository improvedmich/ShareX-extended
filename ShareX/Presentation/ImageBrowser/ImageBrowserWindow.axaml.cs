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

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ShareX.AvaloniaUI.Theming;
using ShareX.HelpersLib;
using ShareX.Icons;
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
    // Decode animated previews at up to this size so frames stay sharp when the splitter is widened.
    private const int MaxAnimatedPreviewSize = 2048;

    private readonly ImageBrowserViewModel _viewModel = new();
    private readonly DispatcherTimer _filterTimer;
    private readonly ImageBrowserOptions _fallbackOptions = new();
    private CancellationTokenSource? _loadCancellation;
    private CancellationTokenSource? _previewCancellation;
    private ImageBrowserStatsWindow? _statsWindow;
    private Bitmap? _previewBitmap;
    private int _previewVersion;
    private bool _suppressEvents;

    private static ImageBrowserOptions SharedOptions =>
        ApplicationState.DefaultTaskSettings?.ImageBrowserOptions ?? throw new InvalidOperationException("Task settings have not been loaded.");

    public ImageBrowserWindow()
    {
        InitializeComponent();
        RequestedThemeVariant = ThemeManager.GetCurrentTheme();

        _filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FilterDebounceMilliseconds) };
        _filterTimer.Tick += OnFilterTimerTick;
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
        _suppressEvents = false;

        _viewModel.RecursiveSearch = Options.RecursiveSearch;

        string folder = TaskHelpers.GetScreenshotsFolder();
        await LoadFolderAsync(folder);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _filterTimer.Stop();
        _previewVersion++;
        _statsWindow?.Close();
        _statsWindow = null;
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _previewCancellation?.Cancel();
        _previewCancellation?.Dispose();
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
        SortNameIcon.Icon = GetSortIcon(ImageBrowserSortColumn.Name);
        SortSizeIcon.Icon = GetSortIcon(ImageBrowserSortColumn.Size);
        SortModifiedIcon.Icon = GetSortIcon(ImageBrowserSortColumn.ModifiedAt);
    }

    private string GetSortIcon(ImageBrowserSortColumn column)
    {
        if (_viewModel.SortColumn != column)
        {
            return string.Empty;
        }

        return _viewModel.SortOrder switch
        {
            ImageBrowserSortOrder.Ascending => AppIcons.ArrowUp,
            ImageBrowserSortOrder.Descending => AppIcons.ArrowDown,
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
        int version = ++_previewVersion;

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

            if (version != _previewVersion || token.IsCancellationRequested)
            {
                bitmap.Dispose();
                return;
            }

            _previewBitmap = bitmap;
            PreviewImage.Source = bitmap;
            PreviewUnavailable.IsVisible = false;

            string dimensions = $"{bitmap.PixelSize.Width} × {bitmap.PixelSize.Height}";
            PreviewDetails.Text = $"{Strings.ImageBrowserWindow_Dimensions}: {dimensions}" + Environment.NewLine + detailsText;

            if (AnimatedImagePreview.CanBeAnimated(file.FilePath))
            {
                _ = PlayPreviewAnimationAsync(version, file.FilePath, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, $"Failed to load preview for '{file.FilePath}'.");
        }
    }

    private async Task PlayPreviewAnimationAsync(int version, string filePath, CancellationToken token)
    {
        // Plays animated GIFs and WebP files the same way the image viewer does, until another file is
        // selected or the window closes. Frames are not decoded while the window is in the background.
        try
        {
            await AnimatedImagePreview.PlayAsync(filePath, MaxAnimatedPreviewSize,
                () => version == _previewVersion && !token.IsCancellationRequested,
                frame => ShowPreviewFrame(version, token, frame),
                () => !IsVisible || WindowState == Avalonia.Controls.WindowState.Minimized);
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, $"Failed to play animated preview for '{filePath}'.");
        }
    }

    private void ShowPreviewFrame(int version, CancellationToken token, Bitmap frame)
    {
        if (version != _previewVersion || token.IsCancellationRequested)
        {
            frame.Dispose();
            return;
        }

        Bitmap? previous = _previewBitmap;
        _previewBitmap = frame;
        PreviewImage.Source = frame;
        previous?.Dispose();
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

    private void OnStatsClick(object? sender, RoutedEventArgs e)
    {
        if (_statsWindow != null)
        {
            if (!_statsWindow.IsVisible)
            {
                _statsWindow.Show(this);
            }

            if (_statsWindow.WindowState == Avalonia.Controls.WindowState.Minimized)
            {
                _statsWindow.WindowState = Avalonia.Controls.WindowState.Normal;
            }

            _statsWindow.Activate();
            return;
        }

        _statsWindow = new ImageBrowserStatsWindow(_viewModel, Options);
        _statsWindow.Closed += (_, _) => _statsWindow = null;
        _statsWindow.Show(this);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
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
