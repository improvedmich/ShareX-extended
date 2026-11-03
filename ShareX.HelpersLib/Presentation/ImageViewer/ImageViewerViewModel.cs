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

using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;


namespace ShareX.HelpersLib;

public sealed class ImageViewerViewModel : INotifyPropertyChanged, IDisposable
{
    // How many decoded frames are kept aside from the displayed one.
    private const int MaxPrefetchedImages = 2;

    private string[] _images = [];
    // Files that failed to decode during this viewing session, skipped without retrying.
    private readonly HashSet<string> _unreadableFiles = new(StringComparer.OrdinalIgnoreCase);
    // Decoded neighbors kept ready so that next/previous navigation shows large images instantly.
    private readonly Dictionary<string, Bitmap> _prefetched = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _navigationCancellation;
    private int _currentImageIndex;
    private bool _disposed;


    private Bitmap? _currentImage;
    private string? _currentImageFilePath;
    private string _statusText = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Bitmap? CurrentImage
    {
        get => _currentImage;
        private set
        {
            if (ReferenceEquals(_currentImage, value))
            {
                return;
            }

            _currentImage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasImage));
        }
    }

    public string? CurrentImageFilePath
    {
        get => _currentImageFilePath;
        private set
        {
            if (_currentImageFilePath == value)
            {
                return;
            }

            _currentImageFilePath = value;
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
        }
    }

    public bool SupportWrap { get; set; }
    public bool HasImage => CurrentImage != null;
    public bool CanNavigate => _images.Length > 1;
    public bool CanNavigateLeft => CanNavigate && (SupportWrap || _currentImageIndex > 0);
    public bool CanNavigateRight => CanNavigate && (SupportWrap || _currentImageIndex < _images.Length - 1);

    public bool LoadFile(string filePath)
    {
        if (!File.Exists(filePath) || !FileHelpers.IsImageFile(filePath))
        {
            return false;
        }

        string? folder = Path.GetDirectoryName(filePath);
        _images = !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)
            ? Directory.GetFiles(folder).Where(FileHelpers.IsImageFile).ToArray()
            : [filePath];

        _unreadableFiles.Clear();
        ClearPrefetched();
        _currentImageIndex = Array.FindIndex(_images,

            path => string.Equals(path, filePath, StringComparison.OrdinalIgnoreCase));
        if (_currentImageIndex < 0)
        {
            _images = [filePath];
            _currentImageIndex = 0;
        }

        return LoadCurrentImage();
    }

    public bool LoadFiles(IReadOnlyList<string> filePaths, int selectedIndex)
    {
        List<string> images = [];
        int filteredSelectedIndex = -1;
        for (int index = 0; index < filePaths.Count; index++)
        {
            string path = filePaths[index];
            if (!File.Exists(path) || !FileHelpers.IsImageFile(path))
            {
                continue;
            }

            if (index == selectedIndex)
            {
                filteredSelectedIndex = images.Count;
            }
            images.Add(path);
        }

        _images = images.ToArray();
        if (_images.Length == 0)
        {
            return false;
        }

        _unreadableFiles.Clear();
        ClearPrefetched();
        _currentImageIndex = filteredSelectedIndex >= 0

            ? filteredSelectedIndex
            : Math.Clamp(selectedIndex, 0, _images.Length - 1);

        return LoadCurrentImage();
    }

    public bool LoadEncodedImage(byte[] imageData, string? displayName = null)
    {
        if (imageData.Length == 0)
        {
            return false;
        }

        try
        {
            using MemoryStream stream = new(imageData, writable: false);
            ReplaceImage(new Bitmap(stream));
            _images = [];
            _currentImageIndex = 0;
            CurrentImageFilePath = displayName;
            UpdateStatus();
            return true;
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, "Failed to load image viewer data.");
            return false;
        }
    }

    // Decoding runs off the UI thread: large images can take hundreds of milliseconds, and a
    // synchronous decode would freeze the window. A new navigation cancels the pending decode,
    // so rapid key presses only ever show the latest requested image.
    public Task NavigateAsync(int offset)
    {
        if (!CanNavigate)
        {
            return Task.CompletedTask;
        }

        // The previous source is only cancelled, never disposed here: a decode that is still
        // awaiting it would then observe ObjectDisposedException instead of cancellation.
        _navigationCancellation?.Cancel();
        _navigationCancellation = new CancellationTokenSource();
        return NavigateCoreAsync(offset, _navigationCancellation.Token);
    }

    private async Task NavigateCoreAsync(int offset, CancellationToken token)
    {
        int startIndex = _currentImageIndex;

        // Walk forward/backward past files that fail to decode, so the index always matches the
        // image actually shown. Stopping on a broken file would make the counter drift and leave
        // the user stuck, since every key press would retry the same unreadable file.
        for (int step = 1; step < _images.Length; step++)
        {
            int index = SupportWrap
                ? ((startIndex + offset * step) % _images.Length + _images.Length) % _images.Length
                : startIndex + offset * step;

            if (index < 0 || index >= _images.Length)
            {
                break;
            }

            string filePath = _images[index];

            if (_unreadableFiles.Contains(filePath))
            {
                continue;
            }

            Bitmap? bitmap = await LoadBitmapAsync(filePath, token);

            if (token.IsCancellationRequested)
            {
                // A newer navigation took over; keep the currently displayed image untouched.
                bitmap?.Dispose();
                return;
            }

            if (bitmap == null)
            {
                continue;
            }

            _currentImageIndex = index;
            CurrentImageFilePath = filePath;
            ReplaceImage(bitmap);
            UpdateStatus();
            PrefetchNeighbors(index, offset);
            return;
        }

        // Nothing else could be loaded, keep the currently displayed image.
    }

    private async Task<Bitmap?> LoadBitmapAsync(string filePath, CancellationToken token)
    {
        if (_prefetched.Remove(filePath, out Bitmap? prefetched))
        {
            return prefetched;
        }

        try
        {
            Bitmap bitmap = await Task.Run(() => new Bitmap(filePath), token);

            if (token.IsCancellationRequested)
            {
                bitmap.Dispose();
                return null;
            }

            return bitmap;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            // The navigation was cancelled and its source released while the decode was awaiting it.
            return null;
        }
        catch (Exception exception)
        {
            _unreadableFiles.Add(filePath);
            DebugHelper.WriteException(exception, $"Failed to load image '{filePath}'.");
            return null;
        }
    }

    private bool LoadCurrentImage()
    {
        if (_images.Length == 0)
        {
            return false;
        }

        string filePath = _images[_currentImageIndex];

        try
        {
            Bitmap bitmap = new(filePath);
            // Commit the path only after a successful decode, so a failed load leaves the previous
            // image, file name and status counter untouched.
            CurrentImageFilePath = filePath;
            ReplaceImage(bitmap);
            UpdateStatus();
            return true;
        }
        catch (Exception exception)
        {
            _unreadableFiles.Add(filePath);
            DebugHelper.WriteException(exception,
                $"Failed to load image '{filePath}'.");
            return false;
        }
    }

    // Decodes the neighbor the user is most likely to open next, so that the following
    // navigation shows an already decoded bitmap instead of waiting for the file to be read.
    private void PrefetchNeighbors(int index, int navigationOffset)
    {
        if (navigationOffset > 0)
        {
            Prefetch(index + 1);
            Prefetch(index - 1);
        }
        else
        {
            Prefetch(index - 1);
            Prefetch(index + 1);
        }
    }

    private void Prefetch(int index)
    {
        if (index < 0 || index >= _images.Length)
        {
            return;
        }

        string filePath = _images[index];

        // The displayed bitmap is disposed on navigation, so it must never be cached as a spare.
        if (_disposed || filePath == CurrentImageFilePath || _unreadableFiles.Contains(filePath) ||
            _prefetched.ContainsKey(filePath))
        {
            return;
        }

        _ = Task.Run(() => new Bitmap(filePath)).ContinueWith(task =>
        {
            // Background continuations must not touch the UI bound collections directly.
            Dispatcher.UIThread.Post(() =>
            {
                // Reading task.Result of a faulted task would throw here, so check the status first.
                if (task.IsCompletedSuccessfully)
                {
                    Bitmap bitmap = task.Result;

                    if (_disposed || filePath == CurrentImageFilePath)
                    {
                        bitmap.Dispose();
                        return;
                    }

                    // Keeps only the frames around the current position, the rest are dropped right away.
                    while (_prefetched.Count >= MaxPrefetchedImages)
                    {
                        string oldKey = _prefetched.Keys.First();
                        _prefetched[oldKey].Dispose();
                        _prefetched.Remove(oldKey);
                    }

                    _prefetched[filePath] = bitmap;
                }
                else if (task.Exception != null)
                {
                    _unreadableFiles.Add(filePath);
                }
            });
        }, TaskScheduler.Default);
    }

    private void ReplaceImage(Bitmap image)
    {
        CurrentImage?.Dispose();
        CurrentImage = image;
        NotifyNavigationChanged();
    }

    private void UpdateStatus()
    {
        List<string> parts = [];
        if (CanNavigate)
        {
            parts.Add($"{_currentImageIndex + 1} / {_images.Length}");
        }

        if (!string.IsNullOrWhiteSpace(CurrentImageFilePath))
        {
            string fileName = Path.GetFileName(CurrentImageFilePath);
            parts.Add(fileName.Length > 128 ? $"{fileName[..125]}..." : fileName);
        }

        if (CurrentImage != null)
        {
            parts.Add($"{CurrentImage.PixelSize.Width} × {CurrentImage.PixelSize.Height}");
        }

        try
        {
            if (!string.IsNullOrEmpty(CurrentImageFilePath) && File.Exists(CurrentImageFilePath))
            {
                FileInfo info = new(CurrentImageFilePath);
                parts.Add(info.Length.ToSizeString());
                parts.Add(info.LastWriteTime.ToString("g"));
            }
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, $"Failed to read file information for '{CurrentImageFilePath}'.");
        }

        StatusText = string.Join("  |  ", parts);
        NotifyNavigationChanged();
    }


    private void NotifyNavigationChanged()
    {
        OnPropertyChanged(nameof(CanNavigate));
        OnPropertyChanged(nameof(CanNavigateLeft));
        OnPropertyChanged(nameof(CanNavigateRight));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        _disposed = true;
        _navigationCancellation?.Cancel();
        _navigationCancellation?.Dispose();
        _navigationCancellation = null;
        ClearPrefetched();
        CurrentImage?.Dispose();
        CurrentImage = null;
    }

    private void ClearPrefetched()
    {
        foreach (Bitmap prefetched in _prefetched.Values)
        {
            prefetched.Dispose();
        }

        _prefetched.Clear();
    }
}
