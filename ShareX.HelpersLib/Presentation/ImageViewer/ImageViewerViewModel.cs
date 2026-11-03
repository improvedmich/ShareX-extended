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
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

namespace ShareX.HelpersLib;

public sealed class ImageViewerViewModel : INotifyPropertyChanged, IDisposable
{
    private string[] _images = [];
    // Files that failed to decode during this viewing session, skipped without retrying.
    private readonly HashSet<string> _unreadableFiles = new(StringComparer.OrdinalIgnoreCase);
    private int _currentImageIndex;

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

    public void Navigate(int offset)
    {
        if (!CanNavigate)
        {
            return;
        }

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

            if (_unreadableFiles.Contains(_images[index]))
            {
                continue;
            }

            _currentImageIndex = index;

            if (LoadCurrentImage())
            {
                return;
            }
        }

        // Nothing else could be loaded, keep the currently displayed image.
        _currentImageIndex = startIndex;
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
        CurrentImage?.Dispose();
        CurrentImage = null;
    }
}
