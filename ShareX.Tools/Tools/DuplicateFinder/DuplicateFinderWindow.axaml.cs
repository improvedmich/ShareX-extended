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

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using ShareX.AvaloniaUI.Theming;

namespace ShareX.Tools;

public partial class DuplicateFinderWindow : Window
{
    private readonly DuplicateFinderViewModel _viewModel;

    public DuplicateFinderWindow()
    {
        _viewModel = new DuplicateFinderViewModel();
        DataContext = _viewModel;
        InitializeComponent();
        RequestedThemeVariant = ThemeManager.GetCurrentTheme();

        _viewModel.SelectFolderRequested = SelectFolderAsync;
        _viewModel.CopyPathRequested = CopyPathAsync;

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Closed += (_, _) => _viewModel.Dispose();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private async Task<string?> SelectFolderAsync()
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Localization.Strings.DuplicateFinderWindow_Select_folder_dialog,
            AllowMultiple = false
        });

        return folders.FirstOrDefault()?.Path.LocalPath;
    }

    private async Task CopyPathAsync(string path)
    {
        if (Clipboard != null)
        {
            await Clipboard.SetTextAsync(path);
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        bool hasFolder = e.DataTransfer.TryGetFiles()?.OfType<IStorageFolder>().Any() == true;
        e.DragEffects = _viewModel.IsIdle && hasFolder ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        IStorageFolder? folder = e.DataTransfer.TryGetFiles()?.OfType<IStorageFolder>().FirstOrDefault();
        if (_viewModel.IsIdle && folder != null)
        {
            _viewModel.SetFolder(folder.Path.LocalPath);
            _viewModel.ScanCommand.Execute(null);
            e.Handled = true;
        }
    }
}
