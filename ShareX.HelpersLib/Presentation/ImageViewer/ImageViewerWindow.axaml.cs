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
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ShareX.AvaloniaUI.Theming;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ShareX.HelpersLib;

public partial class ImageViewerWindow : Window
{
    private const double MinZoom = 0.1;
    private const double MaxZoom = 10;
    private const double ZoomFactor = 1.2;

    private readonly ImageViewerViewModel _viewModel = new();
    private readonly MatrixTransform _previewTransform = new() { Matrix = Matrix.Identity };
    private bool _closeOnDeactivate;
    // Space toggles the whole overlay UI; keyboard navigation hides only the navigation buttons.
    private bool _chromeVisible = true;
    private bool _navButtonsVisible = true;
    private bool _isPanning;
    private Point _panStartPosition;
    private Matrix _panStartMatrix;
    // A left-button release below this movement threshold counts as a close click, not a pan.
    private const double ClickThresholdPixels = 6;



    public ImageViewerWindow()
    {
        Initialize();
        Opened += OnOpened;
    }

    public ImageViewerWindow(string filePath)
    {
        Initialize();
        _closeOnDeactivate = _viewModel.LoadFile(filePath);
    }

    public ImageViewerWindow(IReadOnlyList<string> filePaths, int selectedIndex)
    {
        Initialize();
        _closeOnDeactivate = _viewModel.LoadFiles(filePaths, selectedIndex);
    }

    public ImageViewerWindow(byte[] imageData, string? displayName = null)
    {
        Initialize();
        _closeOnDeactivate = _viewModel.LoadEncodedImage(imageData, displayName);
    }

    private void Initialize()
    {
        DataContext = _viewModel;
        AvaloniaXamlLoader.Load(this);
        this.FindControl<Image>("PreviewImage")!.RenderTransform = _previewTransform;
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ImageViewerViewModel.CurrentImage))
            {
                _previewTransform.Matrix = Matrix.Identity;
            }
        };
        PointerMoved += OnPointerMoved;

        System.Drawing.Rectangle activeScreen = CaptureHelpers.GetActiveScreenBounds();
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(activeScreen.X, activeScreen.Y);
        RequestedThemeVariant = ThemeManager.GetCurrentTheme();
        Title = Localization.Strings.ImageViewerWindow_Title;
        KeyDown += OnKeyDown;
        Deactivated += OnDeactivated;
        Closed += (_, _) => _viewModel.Dispose();
        ApplyChrome();
    }


    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        if (!await SelectImageAsync())
        {
            Close();
        }
    }

    private async Task<bool> SelectImageAsync()
    {
        _closeOnDeactivate = false;
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localization.Strings.ImageViewerWindow_Open_image,
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll]
        });

        string? filePath = files.FirstOrDefault()?.Path.LocalPath;
        bool loaded = !string.IsNullOrWhiteSpace(filePath) && _viewModel.LoadFile(filePath);
        _closeOnDeactivate = _viewModel.HasImage;
        Activate();
        Focus();
        return loaded;
    }

    private void OnPreviousClick(object? sender, RoutedEventArgs e) => _ = _viewModel.NavigateAsync(-1);
    private void OnNextClick(object? sender, RoutedEventArgs e) => _ = _viewModel.NavigateAsync(1);


    // Opacity is used instead of IsVisible so the CanNavigate bindings on the buttons keep working.
    private void ApplyChrome()
    {
        Border? statusPanel = this.FindControl<Border>("StatusPanel");
        Button? previousButton = this.FindControl<Button>("PreviousButton");
        Button? nextButton = this.FindControl<Button>("NextButton");

        if (statusPanel != null)
        {
            statusPanel.Opacity = _chromeVisible ? 1.0 : 0.0;
            statusPanel.IsHitTestVisible = _chromeVisible;
        }

        bool buttonsVisible = _chromeVisible && _navButtonsVisible;

        foreach (Button? button in new[] { previousButton, nextButton })
        {
            if (button != null)
            {
                button.Opacity = buttonsVisible ? 1.0 : 0.0;
                button.IsHitTestVisible = buttonsVisible;
            }
        }
    }


    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        // Mouse activity restores the overlay hidden by keyboard navigation or Space.
        if (!_chromeVisible || !_navButtonsVisible)
        {
            _chromeVisible = true;
            _navButtonsVisible = true;
            ApplyChrome();
        }
    }


    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border border || !e.GetCurrentPoint(border).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _isPanning = true;
        _panStartPosition = e.GetPosition(border);
        _panStartMatrix = _previewTransform.Matrix;
        e.Pointer.Capture(border);
        Cursor = new Cursor(StandardCursorType.SizeAll);
        e.Handled = true;
    }

    private void OnPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning || sender is not Border border)
        {
            return;
        }

        Point position = e.GetPosition(border);
        double offsetX = _panStartMatrix.M31 + position.X - _panStartPosition.X;
        double offsetY = _panStartMatrix.M32 + position.Y - _panStartPosition.Y;
        Matrix matrix = _panStartMatrix;
        _previewTransform.Matrix = new Matrix(matrix.M11, 0, 0, matrix.M22, offsetX, offsetY);
        e.Handled = true;
    }

    private void OnPreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isPanning && e.InitialPressMouseButton == MouseButton.Left)
        {
            _isPanning = false;
            e.Pointer.Capture(null);
            Cursor = Cursor.Default;

            if (sender is Border border)
            {
                Point position = e.GetPosition(border);
                double distance = Math.Abs(position.X - _panStartPosition.X) +
                    Math.Abs(position.Y - _panStartPosition.Y);

                // A drag moves the canvas; a plain click still closes the viewer.
                if (distance > ClickThresholdPixels)
                {
                    e.Handled = true;
                    return;
                }
            }

            Close();
            e.Handled = true;
        }
        else if (e.InitialPressMouseButton == MouseButton.Right)
        {
            Close();
            e.Handled = true;
        }
        else if (e.InitialPressMouseButton == MouseButton.Middle)
        {
            _previewTransform.Matrix = Matrix.Identity;
            e.Handled = true;
        }
    }



    private void OnPreviewPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (_viewModel.HasImage && e.Delta.Y != 0 && sender is Border { Child: Image previewImage })
            {
                Matrix matrix = _previewTransform.Matrix;
                double zoom = Math.Clamp(matrix.M11 * Math.Pow(ZoomFactor, e.Delta.Y), MinZoom, MaxZoom);
                // Account for the image's layout offset and current transform when finding the anchor.
                Point pointerPosition = e.GetPosition(previewImage);
                _previewTransform.Matrix = new Matrix(zoom, 0, 0, zoom,
                    matrix.M31 + pointerPosition.X * (matrix.M11 - zoom),
                    matrix.M32 + pointerPosition.Y * (matrix.M22 - zoom));
            }

            e.Handled = true;
            return;
        }

        if (e.Delta.Y > 0 && _viewModel.CanNavigateLeft)
        {
            _ = _viewModel.NavigateAsync(-1);
            e.Handled = true;
        }
        else if (e.Delta.Y < 0 && _viewModel.CanNavigateRight)
        {
            _ = _viewModel.NavigateAsync(1);
            e.Handled = true;
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
                _ = _viewModel.NavigateAsync(-1);
                HideNavButtons();
                e.Handled = true;
                break;
            case Key.Right:
                _ = _viewModel.NavigateAsync(1);
                HideNavButtons();
                e.Handled = true;
                break;
            case Key.Space:
                _chromeVisible = !_chromeVisible;
                ApplyChrome();
                e.Handled = true;
                break;
            case Key.Escape:
            case Key.Enter:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void HideNavButtons()
    {
        if (_navButtonsVisible)
        {
            _navButtonsVisible = false;
            ApplyChrome();
        }
    }


    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_closeOnDeactivate)
        {
            Close();
        }
    }
}
