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
using ShareX.Localization;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ShareX;

public static class ImageBrowserStatsCalculator
{
    private static readonly long[] SizeThresholds =
    [
        100L * 1000,
        500L * 1000,
        1000L * 1000,
        5L * 1000 * 1000,
        20L * 1000 * 1000
    ];

    private sealed class ValueAccumulator
    {
        public string Label = string.Empty;
        public string SortKey = string.Empty;
        public int Count;
        public long Size;
    }

    public static Task<ImageBrowserStatsSnapshot> ComputeAsync(
        IReadOnlyList<ImageFileInfo> files,
        string rootFolder,
        IReadOnlyList<ImageBrowserStatsRule> rules,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Compute(files, rootFolder, rules, cancellationToken), cancellationToken);
    }

    private static ImageBrowserStatsSnapshot Compute(
        IReadOnlyList<ImageFileInfo> files,
        string rootFolder,
        IReadOnlyList<ImageBrowserStatsRule> rules,
        CancellationToken cancellationToken)
    {
        long totalSize = files.Sum(file => file.Size);
        List<ImageBrowserStatsRuleResult> results = new(rules.Count);

        foreach (ImageBrowserStatsRule rule in rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(ComputeRule(files, totalSize, rootFolder, rule, cancellationToken));
        }

        return new ImageBrowserStatsSnapshot(files.Count, totalSize, results);
    }

    private static ImageBrowserStatsRuleResult ComputeRule(
        IReadOnlyList<ImageFileInfo> files,
        long totalSize,
        string rootFolder,
        ImageBrowserStatsRule rule,
        CancellationToken cancellationToken)
    {
        Regex? regex = null;
        string? regexGroupName = null;
        int regexGroupNumber = 0;

        if (rule.Kind == ImageBrowserStatsRuleKind.Regex)
        {
            if (string.IsNullOrWhiteSpace(rule.RegexPattern))
            {
                return CreateErrorResult(files, totalSize, rule, Strings.ImageBrowserWindow_Stats_EmptyPattern);
            }

            try
            {
                regex = new Regex(rule.RegexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(500));
            }
            catch (ArgumentException exception)
            {
                return CreateErrorResult(files, totalSize, rule, exception.Message);
            }

            string group = rule.RegexGroup.Trim();

            if (group.Length > 0)
            {
                if (int.TryParse(group, NumberStyles.Integer, CultureInfo.InvariantCulture, out int groupNumber))
                {
                    if (!regex.GetGroupNumbers().Contains(groupNumber))
                    {
                        return CreateErrorResult(files, totalSize, rule,
                            string.Format(Strings.ImageBrowserWindow_Stats_InvalidGroup_format, group));
                    }

                    regexGroupNumber = groupNumber;
                }
                else
                {
                    if (!regex.GetGroupNames().Contains(group, StringComparer.Ordinal))
                    {
                        return CreateErrorResult(files, totalSize, rule,
                            string.Format(Strings.ImageBrowserWindow_Stats_InvalidGroup_format, group));
                    }

                    regexGroupName = group;
                }
            }
        }

        Dictionary<string, ValueAccumulator> values = new(StringComparer.OrdinalIgnoreCase);
        int noValueCount = 0;
        long noValueSize = 0;

        try
        {
            foreach (ImageFileInfo file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!TryGetValue(file, rootFolder, rule, regex, regexGroupName, regexGroupNumber,
                    out string? label, out string? sortKey) || string.IsNullOrEmpty(label))
                {
                    noValueCount++;
                    noValueSize += file.Size;
                    continue;
                }

                sortKey ??= label;

                if (!values.TryGetValue(label, out ValueAccumulator? value))
                {
                    value = new ValueAccumulator { Label = label, SortKey = sortKey };
                    values[label] = value;
                }

                value.Count++;
                value.Size += file.Size;
            }
        }
        catch (RegexMatchTimeoutException exception)
        {
            return CreateErrorResult(files, totalSize, rule, exception.Message);
        }

        List<ValueAccumulator> ordered = [.. values.Values];
        SortValues(ordered, rule);
        int maxValueCount = ordered.Count > 0 ? ordered.Max(value => value.Count) : 0;
        int maxCount = Math.Max(maxValueCount, noValueCount);
        List<ImageBrowserStatsValue> resultValues = new(ordered.Count + 1);

        foreach (ValueAccumulator value in ordered)
        {
            resultValues.Add(new ImageBrowserStatsValue
            {
                Label = value.Label,
                Count = value.Count,
                Size = value.Size,
                Fraction = maxCount > 0 ? value.Count / (double)maxCount : 0,
                Share = files.Count > 0 ? value.Count / (double)files.Count : 0,
                ShareText = FormatShare(value.Count, files.Count),
                IsPeak = maxValueCount > 0 && value.Count == maxValueCount
            });
        }

        if (noValueCount > 0)
        {
            resultValues.Add(new ImageBrowserStatsValue
            {
                Label = Strings.ImageBrowserWindow_Stats_NoValue,
                Count = noValueCount,
                Size = noValueSize,
                Fraction = maxCount > 0 ? noValueCount / (double)maxCount : 0,
                Share = files.Count > 0 ? noValueCount / (double)files.Count : 0,
                ShareText = FormatShare(noValueCount, files.Count),
                IsNoValue = true
            });
        }

        return new ImageBrowserStatsRuleResult
        {
            Rule = rule,
            TotalFiles = files.Count,
            TotalSize = totalSize,
            Values = resultValues,
            NoValueCount = noValueCount,
            NoValueSize = noValueSize
        };
    }

    private static void SortValues(List<ValueAccumulator> values, ImageBrowserStatsRule rule)
    {
        if (rule.Kind == ImageBrowserStatsRuleKind.Date)
        {
            // Times of day and days of the week read better in their natural order; dates newest first.
            bool ascending = rule.DateBucket is ImageBrowserStatsDateBucket.Hour or ImageBrowserStatsDateBucket.DayOfWeek;

            values.Sort((left, right) =>
            {
                int comparison = string.CompareOrdinal(left.SortKey, right.SortKey);
                if (comparison != 0)
                {
                    return ascending ? comparison : -comparison;
                }

                return string.Compare(left.Label, right.Label, StringComparison.CurrentCultureIgnoreCase);
            });

            return;
        }

        if (rule.Kind == ImageBrowserStatsRuleKind.Size)
        {
            values.Sort((left, right) => string.CompareOrdinal(left.SortKey, right.SortKey));
            return;
        }

        values.Sort((left, right) =>
        {
            int comparison = right.Count.CompareTo(left.Count);
            return comparison != 0 ? comparison : string.Compare(left.Label, right.Label, StringComparison.CurrentCultureIgnoreCase);
        });
    }

    private static bool TryGetValue(
        ImageFileInfo file,
        string rootFolder,
        ImageBrowserStatsRule rule,
        Regex? regex,
        string? regexGroupName,
        int regexGroupNumber,
        out string? label,
        out string? sortKey)
    {
        label = null;
        sortKey = null;

        switch (rule.Kind)
        {
            case ImageBrowserStatsRuleKind.Date:
                return TryGetDateValue(file, rule, out label, out sortKey);

            case ImageBrowserStatsRuleKind.Extension:
                label = file.Extension.TrimStart('.').ToLowerInvariant();
                sortKey = label;
                return label.Length > 0;

            case ImageBrowserStatsRuleKind.Folder:
                label = GetRelativeFolder(file.FolderPath, rootFolder);
                sortKey = label;
                return true;

            case ImageBrowserStatsRuleKind.FileNamePart:
                return TryGetFileNamePartValue(file.FileName, rule, out label, out sortKey);

            case ImageBrowserStatsRuleKind.Size:
                return TryGetSizeValue(file.Size, out label, out sortKey);

            case ImageBrowserStatsRuleKind.Regex:
                return TryGetRegexValue(file.FileName, regex, regexGroupName, regexGroupNumber, out label, out sortKey);
        }

        return false;
    }

    private static bool TryGetDateValue(ImageFileInfo file, ImageBrowserStatsRule rule, out string? label, out string? sortKey)
    {
        label = null;
        sortKey = null;

        DateTime date = rule.DateSource == ImageBrowserStatsDateSource.Created ? file.CreatedAt : file.ModifiedAt;
        CultureInfo culture = CultureInfo.CurrentCulture;

        switch (rule.DateBucket)
        {
            case ImageBrowserStatsDateBucket.DayOfWeek:
                label = culture.DateTimeFormat.GetDayName(date.DayOfWeek);
                sortKey = GetDayOfWeekOrder(date.DayOfWeek, culture.DateTimeFormat.FirstDayOfWeek).ToString("D2", CultureInfo.InvariantCulture);
                return true;

            case ImageBrowserStatsDateBucket.Hour:
                label = date.ToString("HH:00", culture);
                sortKey = date.Hour.ToString("D2", CultureInfo.InvariantCulture);
                return true;

            case ImageBrowserStatsDateBucket.Day:
                label = date.ToString("d", culture);
                sortKey = date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                return true;

            case ImageBrowserStatsDateBucket.Week:
                DateTime weekStart = GetWeekStart(date, culture.DateTimeFormat.FirstDayOfWeek);
                label = string.Format(Strings.ImageBrowserWindow_Stats_WeekOf_format, weekStart.ToString("d", culture));
                sortKey = weekStart.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                return true;

            case ImageBrowserStatsDateBucket.Month:
                label = date.ToString("MMMM yyyy", culture);
                sortKey = date.ToString("yyyyMM", CultureInfo.InvariantCulture);
                return true;

            case ImageBrowserStatsDateBucket.Year:
                label = date.ToString("yyyy", culture);
                sortKey = date.ToString("yyyy", CultureInfo.InvariantCulture);
                return true;
        }

        return false;
    }

    private static bool TryGetFileNamePartValue(string fileName, ImageBrowserStatsRule rule, out string? label, out string? sortKey)
    {
        label = null;
        sortKey = null;

        if (string.IsNullOrEmpty(rule.Separator) || rule.PartIndex == 0)
        {
            return false;
        }

        string name = Path.GetFileNameWithoutExtension(fileName);

        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        string[] parts = name.Split(rule.Separator, StringSplitOptions.None);
        int index = rule.PartIndex > 0 ? rule.PartIndex - 1 : parts.Length + rule.PartIndex;

        if (index < 0 || index >= parts.Length)
        {
            return false;
        }

        string value = parts[index].Trim();

        if (value.Length == 0)
        {
            return false;
        }

        label = value;
        sortKey = value;
        return true;
    }

    private static bool TryGetSizeValue(long size, out string? label, out string? sortKey)
    {
        int bucket = 0;
        while (bucket < SizeThresholds.Length && size >= SizeThresholds[bucket])
        {
            bucket++;
        }

        static string FormatThreshold(long value) => value.ToSizeString(decimalPlaces: 0);

        label = bucket switch
        {
            0 => $"< {FormatThreshold(SizeThresholds[0])}",
            1 => $"{FormatThreshold(SizeThresholds[0])} – {FormatThreshold(SizeThresholds[1])}",
            2 => $"{FormatThreshold(SizeThresholds[1])} – {FormatThreshold(SizeThresholds[2])}",
            3 => $"{FormatThreshold(SizeThresholds[2])} – {FormatThreshold(SizeThresholds[3])}",
            4 => $"{FormatThreshold(SizeThresholds[3])} – {FormatThreshold(SizeThresholds[4])}",
            _ => $"≥ {FormatThreshold(SizeThresholds[4])}"
        };

        sortKey = bucket.ToString("D2", CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryGetRegexValue(
        string fileName,
        Regex? regex,
        string? regexGroupName,
        int regexGroupNumber,
        out string? label,
        out string? sortKey)
    {
        label = null;
        sortKey = null;

        if (regex == null)
        {
            return false;
        }

        Match match = regex.Match(fileName);

        if (!match.Success)
        {
            return false;
        }

        Group group = regexGroupName != null ? match.Groups[regexGroupName] : match.Groups[regexGroupNumber];

        if (!group.Success || string.IsNullOrEmpty(group.Value))
        {
            return false;
        }

        label = group.Value;
        sortKey = label;
        return true;
    }

    private static string GetRelativeFolder(string folderPath, string rootFolder)
    {
        if (string.IsNullOrEmpty(rootFolder))
        {
            return Path.GetFileName(folderPath);
        }

        try
        {
            string relative = Path.GetRelativePath(rootFolder, folderPath);

            if (relative == ".")
            {
                return Strings.ImageBrowserWindow_Stats_RootFolder;
            }

            if (!relative.Equals("..", StringComparison.Ordinal) &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return relative.Replace('\\', '/');
            }
        }
        catch (ArgumentException)
        {
        }

        return Path.GetFileName(folderPath);
    }

    private static int GetDayOfWeekOrder(DayOfWeek day, DayOfWeek firstDayOfWeek) =>
        ((int)day - (int)firstDayOfWeek + 7) % 7;

    private static DateTime GetWeekStart(DateTime date, DayOfWeek firstDayOfWeek) =>
        date.Date.AddDays(-GetDayOfWeekOrder(date.DayOfWeek, firstDayOfWeek));

    private static string FormatShare(int count, int totalFiles)
    {
        if (totalFiles <= 0)
        {
            return "0%";
        }

        double share = count * 100.0 / totalFiles;
        string format = (share > 0 && share < 10) || (share > 99 && share < 100) ? "0.#" : "0";
        return share.ToString(format, CultureInfo.CurrentCulture) + "%";
    }

    private static ImageBrowserStatsRuleResult CreateErrorResult(
        IReadOnlyList<ImageFileInfo> files,
        long totalSize,
        ImageBrowserStatsRule rule,
        string error) => new()
        {
            Rule = rule,
            TotalFiles = files.Count,
            TotalSize = totalSize,
            Error = error
        };
}
