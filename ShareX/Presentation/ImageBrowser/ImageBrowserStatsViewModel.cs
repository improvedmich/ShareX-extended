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

using Avalonia.Threading;
using ShareX.HelpersLib;
using ShareX.Localization;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

public sealed class ImageBrowserStatsViewModel : INotifyPropertyChanged, IDisposable
{
    private const int DebounceMilliseconds = 280;

    private readonly ImageBrowserViewModel _browser;
    private readonly ImageBrowserOptions _options;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _cancellation;
    private bool _useAllFiles;
    private bool _isBusy;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ImageBrowserStatsRuleCardViewModel> Cards { get; } = [];

    public ImageBrowserStatsViewModel(ImageBrowserViewModel browser, ImageBrowserOptions options)
    {
        _browser = browser;
        _options = options;
        _useAllFiles = options.StatsUseAllFiles;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DebounceMilliseconds) };
        _timer.Tick += OnTimerTick;

        _browser.FilterApplied += OnFilterApplied;
        LoadRules();
    }

    public bool UseAllFiles
    {
        get => _useAllFiles;
        set
        {
            if (_useAllFiles == value)
            {
                return;
            }

            _useAllFiles = value;
            _options.StatsUseAllFiles = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ScopeText));
            ScheduleCompute();
        }
    }

    public string ScopeText
    {
        get
        {
            long size = ScopeFiles.Sum(file => file.Size);
            return string.Format(Strings.ImageBrowserWindow_Stats_Scope_format,
                ScopeFiles.Count.ToString("N0", CultureInfo.CurrentCulture),
                _browser.AllFiles.Count.ToString("N0", CultureInfo.CurrentCulture)) + " · " + size.ToSizeString();
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

    public bool HasNoRules => Cards.Count == 0;

    private IReadOnlyList<ImageFileInfo> ScopeFiles => _useAllFiles ? _browser.AllFiles : _browser.FilteredFiles;

    public void AddRule(ImageBrowserStatsRuleKind kind)
    {
        ImageBrowserStatsRule rule = new() { Kind = kind };
        _options.StatsRules.Add(rule);
        Cards.Add(new ImageBrowserStatsRuleCardViewModel(rule, ScheduleCompute));
        OnPropertyChanged(nameof(HasNoRules));
        ScheduleCompute();
    }

    public void RemoveRule(ImageBrowserStatsRuleCardViewModel card)
    {
        if (!Cards.Remove(card))
        {
            return;
        }

        _options.StatsRules.Remove(card.Rule);
        OnPropertyChanged(nameof(HasNoRules));
        ScheduleCompute();
    }

    public async Task ComputeAsync()
    {
        _timer.Stop();

        CancellationTokenSource source = new();
        CancellationTokenSource? previous = _cancellation;
        _cancellation = source;
        previous?.Cancel();
        previous?.Dispose();

        ImageFileInfo[] files = [.. ScopeFiles];
        ImageBrowserStatsRule[] rules = [.. Cards.Select(card => card.Rule)];
        IsBusy = true;

        try
        {
            ImageBrowserStatsSnapshot snapshot =
                await ImageBrowserStatsCalculator.ComputeAsync(files, _browser.FolderPath, rules, source.Token);

            if (source.IsCancellationRequested)
            {
                return;
            }

            foreach (ImageBrowserStatsRuleResult result in snapshot.Rules)
            {
                ImageBrowserStatsRuleCardViewModel? card = Cards.FirstOrDefault(item => ReferenceEquals(item.Rule, result.Rule));
                card?.ApplyResult(result);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DebugHelper.WriteException(exception, "Failed to compute image browser statistics.");
        }
        finally
        {
            if (ReferenceEquals(_cancellation, source))
            {
                IsBusy = false;
            }
        }
    }

    public void Dispose()
    {
        _browser.FilterApplied -= OnFilterApplied;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
    }

    private void OnFilterApplied()
    {
        OnPropertyChanged(nameof(ScopeText));
        ScheduleCompute();
    }

    private void LoadRules()
    {
        if (_options.StatsRules == null)
        {
            _options.StatsRules = ImageBrowserStatsRule.CreateDefaultRules();
        }

        if (!string.IsNullOrWhiteSpace(_options.StatsRegexPattern))
        {
            MigrateLegacyRegex(_options.StatsRegexPattern);
            _options.StatsRegexPattern = string.Empty;
        }

        foreach (ImageBrowserStatsRule rule in _options.StatsRules)
        {
            Cards.Add(new ImageBrowserStatsRuleCardViewModel(rule, ScheduleCompute));
        }
    }

    private void MigrateLegacyRegex(string pattern)
    {
        List<string> groupNames = [];

        try
        {
            Regex regex = new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(500));
            groupNames.AddRange(regex.GetGroupNames().Where(name => !int.TryParse(name, out _)));
        }
        catch (ArgumentException)
        {
        }

        // The old statistics dialog grouped by every named capture; with none it matched the whole name.
        if (groupNames.Count == 0)
        {
            groupNames.Add(string.Empty);
        }

        foreach (string groupName in groupNames)
        {
            if (_options.StatsRules.Any(rule =>
                rule.Kind == ImageBrowserStatsRuleKind.Regex &&
                string.Equals(rule.RegexPattern, pattern, StringComparison.Ordinal) &&
                string.Equals(rule.RegexGroup, groupName, StringComparison.Ordinal)))
            {
                continue;
            }

            _options.StatsRules.Add(new ImageBrowserStatsRule
            {
                Kind = ImageBrowserStatsRuleKind.Regex,
                RegexPattern = pattern,
                RegexGroup = groupName
            });
        }
    }

    private void ScheduleCompute()
    {
        _cancellation?.Cancel();
        _timer.Stop();

        if (Cards.Count == 0)
        {
            IsBusy = false;
            return;
        }

        _timer.Start();
    }

    private void OnTimerTick(object? sender, EventArgs e) => _ = ComputeAsync();

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class ImageBrowserStatsRuleCardViewModel : INotifyPropertyChanged
{
    private const int TopValueCount = 10;

    private readonly Action _changed;
    private ImageBrowserStatsRuleResult? _result;
    private ImageBrowserStatsSortColumn? _sortColumn;
    private bool _sortDescending;
    private bool _isExpanded;
    private string _partIndexText;
    private IReadOnlyList<ImageBrowserStatsValue> _sortedValues = [];
    private IReadOnlyList<ImageBrowserStatsValue> _visibleValues = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    public ImageBrowserStatsRuleCardViewModel(ImageBrowserStatsRule rule, Action changed)
    {
        Rule = rule;
        _changed = changed;
        _partIndexText = rule.PartIndex.ToString(CultureInfo.CurrentCulture);
    }

    public ImageBrowserStatsRule Rule { get; }

    public string Title => Rule.Kind switch
    {
        ImageBrowserStatsRuleKind.Date => Strings.ImageBrowserWindow_Stats_Rule_Date,
        ImageBrowserStatsRuleKind.Extension => Strings.ImageBrowserWindow_Stats_Rule_Extension,
        ImageBrowserStatsRuleKind.Folder => Strings.MainWindow_Folder,
        ImageBrowserStatsRuleKind.FileNamePart => Strings.ImageBrowserWindow_Stats_Rule_FileNamePart,
        ImageBrowserStatsRuleKind.Size => Strings.ImageBrowserWindow_Size,
        ImageBrowserStatsRuleKind.Regex => Strings.ImageBrowserWindow_Regex_pattern,
        _ => string.Empty
    };

    public string Example => string.Format(Strings.ImageBrowserWindow_Stats_Example_format, GetExample());

    public bool ShowDateOptions => Rule.Kind == ImageBrowserStatsRuleKind.Date;
    public bool ShowPartOptions => Rule.Kind == ImageBrowserStatsRuleKind.FileNamePart;
    public bool ShowRegexOptions => Rule.Kind == ImageBrowserStatsRuleKind.Regex;

    public int DateSourceIndex
    {
        get => (int)Rule.DateSource;
        set
        {
            if (value < 0 || value > (int)ImageBrowserStatsDateSource.Created ||
                Rule.DateSource == (ImageBrowserStatsDateSource)value)
            {
                return;
            }

            Rule.DateSource = (ImageBrowserStatsDateSource)value;
            _changed();
        }
    }

    public int DateBucketIndex
    {
        get => (int)Rule.DateBucket;
        set
        {
            if (value < 0 || value > (int)ImageBrowserStatsDateBucket.Year ||
                Rule.DateBucket == (ImageBrowserStatsDateBucket)value)
            {
                return;
            }

            Rule.DateBucket = (ImageBrowserStatsDateBucket)value;
            OnPropertyChanged(nameof(Example));
            _changed();
        }
    }

    public string Separator
    {
        get => Rule.Separator;
        set
        {
            if (Rule.Separator == value)
            {
                return;
            }

            Rule.Separator = value ?? string.Empty;
            OnPropertyChanged(nameof(Example));
            _changed();
        }
    }

    public string PartIndexText
    {
        get => _partIndexText;
        set
        {
            if (_partIndexText == value)
            {
                return;
            }

            _partIndexText = value ?? string.Empty;

            if (int.TryParse(_partIndexText, NumberStyles.Integer, CultureInfo.CurrentCulture, out int partIndex) && partIndex != 0)
            {
                Rule.PartIndex = partIndex;
                OnPropertyChanged(nameof(Example));
                _changed();
            }
        }
    }

    public string RegexPattern
    {
        get => Rule.RegexPattern;
        set
        {
            if (Rule.RegexPattern == value)
            {
                return;
            }

            Rule.RegexPattern = value ?? string.Empty;
            OnPropertyChanged(nameof(Example));
            _changed();
        }
    }

    public string RegexGroup
    {
        get => Rule.RegexGroup;
        set
        {
            if (Rule.RegexGroup == value)
            {
                return;
            }

            Rule.RegexGroup = value ?? string.Empty;
            OnPropertyChanged(nameof(Example));
            _changed();
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            OnPropertyChanged();
            RefreshValues();
        }
    }

    public IReadOnlyList<ImageBrowserStatsValue> Values => _sortedValues;

    public IReadOnlyList<ImageBrowserStatsValue> VisibleValues => _visibleValues;

    public bool HasMore => _sortedValues.Count > TopValueCount;

    public string ToggleText => IsExpanded
        ? Strings.ImageBrowserWindow_Stats_Collapse
        : string.Format(Strings.ImageBrowserWindow_Stats_ShowAll_format, _sortedValues.Count.ToString("N0", CultureInfo.CurrentCulture));

    public string ValueSortIndicator => GetSortIndicator(ImageBrowserStatsSortColumn.Label);
    public string CountSortIndicator => GetSortIndicator(ImageBrowserStatsSortColumn.Count);
    public string SizeSortIndicator => GetSortIndicator(ImageBrowserStatsSortColumn.Size);
    public string ShareSortIndicator => GetSortIndicator(ImageBrowserStatsSortColumn.Share);

    public void ToggleSort(ImageBrowserStatsSortColumn column)
    {
        if (_sortColumn == column)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortColumn = column;
            _sortDescending = column != ImageBrowserStatsSortColumn.Label;
        }

        RefreshValues();
        OnPropertyChanged(nameof(ValueSortIndicator));
        OnPropertyChanged(nameof(CountSortIndicator));
        OnPropertyChanged(nameof(SizeSortIndicator));
        OnPropertyChanged(nameof(ShareSortIndicator));
    }

    public string SummaryText
    {
        get
        {
            if (_result == null || _result.HasError)
            {
                return string.Empty;
            }

            string text = string.Join(" · ",
                string.Format(Strings.ImageBrowserWindow_Group_files_format, _result.TotalFiles.ToString("N0", CultureInfo.CurrentCulture)),
                _result.TotalSize.ToSizeString(),
                string.Format(Strings.ImageBrowserWindow_Stats_Values_format, _result.ValueCount.ToString("N0", CultureInfo.CurrentCulture)));

            if (_result.NoValueCount > 0)
            {
                text += " · " + string.Format(Strings.ImageBrowserWindow_Stats_NoValue_format,
                    _result.NoValueCount.ToString("N0", CultureInfo.CurrentCulture));
            }

            return text;
        }
    }

    public string ErrorText => _result?.Error ?? string.Empty;
    public bool HasError => _result?.HasError == true;

    public void ApplyResult(ImageBrowserStatsRuleResult result)
    {
        _result = result;
        RefreshValues();
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(HasError));
    }

    private void RefreshValues()
    {
        IEnumerable<ImageBrowserStatsValue> source = _result?.Values ?? [];

        if (_sortColumn is ImageBrowserStatsSortColumn column)
        {
            switch (column)
            {
                case ImageBrowserStatsSortColumn.Label:
                    source = _sortDescending
                        ? source.OrderByDescending(value => value.Label, StringComparer.CurrentCultureIgnoreCase)
                        : source.OrderBy(value => value.Label, StringComparer.CurrentCultureIgnoreCase);
                    break;

                case ImageBrowserStatsSortColumn.Count:
                    source = _sortDescending
                        ? source.OrderByDescending(value => value.Count).ThenBy(value => value.Label, StringComparer.CurrentCultureIgnoreCase)
                        : source.OrderBy(value => value.Count).ThenBy(value => value.Label, StringComparer.CurrentCultureIgnoreCase);
                    break;

                case ImageBrowserStatsSortColumn.Size:
                    source = _sortDescending
                        ? source.OrderByDescending(value => value.Size).ThenBy(value => value.Label, StringComparer.CurrentCultureIgnoreCase)
                        : source.OrderBy(value => value.Size).ThenBy(value => value.Label, StringComparer.CurrentCultureIgnoreCase);
                    break;

                case ImageBrowserStatsSortColumn.Share:
                    source = _sortDescending
                        ? source.OrderByDescending(value => value.Share).ThenBy(value => value.Label, StringComparer.CurrentCultureIgnoreCase)
                        : source.OrderBy(value => value.Share).ThenBy(value => value.Label, StringComparer.CurrentCultureIgnoreCase);
                    break;
            }
        }

        _sortedValues = [.. source];
        _visibleValues = IsExpanded ? _sortedValues : [.. _sortedValues.Take(TopValueCount)];

        OnPropertyChanged(nameof(Values));
        OnPropertyChanged(nameof(VisibleValues));
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(ToggleText));
    }

    private string GetSortIndicator(ImageBrowserStatsSortColumn column) =>
        _sortColumn == column ? (_sortDescending ? "▼" : "▲") : string.Empty;

    private string GetExample()
    {
        CultureInfo culture = CultureInfo.CurrentCulture;

        return Rule.Kind switch
        {
            ImageBrowserStatsRuleKind.Date => Rule.DateBucket switch
            {
                ImageBrowserStatsDateBucket.DayOfWeek => culture.DateTimeFormat.GetDayName(DateTime.Now.DayOfWeek),
                ImageBrowserStatsDateBucket.Hour => DateTime.Now.ToString("HH:00", culture),
                ImageBrowserStatsDateBucket.Day => DateTime.Now.ToString("d", culture),
                ImageBrowserStatsDateBucket.Week => string.Format(Strings.ImageBrowserWindow_Stats_WeekOf_format,
                    GetWeekStart(DateTime.Now, culture.DateTimeFormat.FirstDayOfWeek).ToString("d", culture)),
                ImageBrowserStatsDateBucket.Month => DateTime.Now.ToString("MMMM yyyy", culture),
                ImageBrowserStatsDateBucket.Year => DateTime.Now.ToString("yyyy", culture),
                _ => string.Empty
            },
            ImageBrowserStatsRuleKind.Extension => "png",
            ImageBrowserStatsRuleKind.Folder => "2026/11",
            ImageBrowserStatsRuleKind.FileNamePart => GetFileNamePartExample(),
            ImageBrowserStatsRuleKind.Size => "1 MB – 5 MB",
            ImageBrowserStatsRuleKind.Regex => string.Format("{0}  ·  {1} {2}",
                string.IsNullOrWhiteSpace(Rule.RegexPattern) ? "^(.+?)_\\d{4}-" : Rule.RegexPattern,
                Strings.ImageBrowserWindow_Stats_Group,
                string.IsNullOrWhiteSpace(Rule.RegexGroup) ? "1" : Rule.RegexGroup),
            _ => string.Empty
        };
    }

    private string GetFileNamePartExample()
    {
        const string sample = "Screenshot_2026-11-27_14-30-00";

        if (string.IsNullOrEmpty(Rule.Separator) || Rule.PartIndex == 0)
        {
            return "—";
        }

        string[] parts = sample.Split(Rule.Separator, StringSplitOptions.None);
        int index = Rule.PartIndex > 0 ? Rule.PartIndex - 1 : parts.Length + Rule.PartIndex;
        string value = index >= 0 && index < parts.Length ? parts[index] : "—";
        return $"{sample} → {Rule.PartIndex} → {value}";
    }

    private static int GetDayOfWeekOrder(DayOfWeek day, DayOfWeek firstDayOfWeek) =>
        ((int)day - (int)firstDayOfWeek + 7) % 7;

    private static DateTime GetWeekStart(DateTime date, DayOfWeek firstDayOfWeek) =>
        date.Date.AddDays(-GetDayOfWeekOrder(date.DayOfWeek, firstDayOfWeek));

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
