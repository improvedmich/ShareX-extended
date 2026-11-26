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
using ShareX.AvaloniaUI.Theming;
using System;

namespace ShareX;

public partial class ImageBrowserStatsWindow : Window
{
    private readonly ImageBrowserStatsViewModel? _viewModel;

    public ImageBrowserStatsWindow()
    {
        InitializeComponent();
        RequestedThemeVariant = ThemeManager.GetCurrentTheme();
        KeyDown += OnWindowKeyDown;
    }

    public ImageBrowserStatsWindow(ImageBrowserViewModel browser, ImageBrowserOptions options) : this()
    {
        _viewModel = new ImageBrowserStatsViewModel(browser, options);
        DataContext = _viewModel;

        Opened += async (_, _) =>
        {
            if (_viewModel != null)
            {
                await _viewModel.ComputeAsync();
            }
        };

        Closed += (_, _) => _viewModel?.Dispose();
    }

    private void OnAddRuleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } &&
            Enum.TryParse(tag, out ImageBrowserStatsRuleKind kind))
        {
            _viewModel?.AddRule(kind);
        }
    }

    private void OnRemoveRuleClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ImageBrowserStatsRuleCardViewModel card })
        {
            _viewModel?.RemoveRule(card);
        }
    }

    private void OnToggleValuesClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ImageBrowserStatsRuleCardViewModel card })
        {
            card.IsExpanded = !card.IsExpanded;
        }
    }

    private void OnSortValuesClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string columnName, DataContext: ImageBrowserStatsRuleCardViewModel card } &&
            Enum.TryParse(columnName, out ImageBrowserStatsSortColumn column))
        {
            card.ToggleSort(column);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
