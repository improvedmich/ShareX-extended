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
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ShareX;

public enum ImageBrowserStatsRuleKind
{
    Date,
    Extension,
    Folder,
    FileNamePart,
    Size,
    Regex
}

public enum ImageBrowserStatsDateSource
{
    Modified,
    Created
}

public enum ImageBrowserStatsDateBucket
{
    DayOfWeek,
    Hour,
    Day,
    Week,
    Month,
    Year
}

public enum ImageBrowserStatsSortColumn
{
    Label,
    Count,
    Size,
    Share
}

public sealed class ImageBrowserStatsRule
{
    public ImageBrowserStatsRuleKind Kind;
    public ImageBrowserStatsDateSource DateSource = ImageBrowserStatsDateSource.Modified;
    public ImageBrowserStatsDateBucket DateBucket = ImageBrowserStatsDateBucket.Day;
    public string Separator = "_";
    public int PartIndex = 1;
    public string RegexPattern = "";
    public string RegexGroup = "";

    public static List<ImageBrowserStatsRule> CreateDefaultRules() =>
    [
        new() { Kind = ImageBrowserStatsRuleKind.Date, DateSource = ImageBrowserStatsDateSource.Modified, DateBucket = ImageBrowserStatsDateBucket.Day },
        new() { Kind = ImageBrowserStatsRuleKind.Extension },
        new() { Kind = ImageBrowserStatsRuleKind.Folder }
    ];

    public ImageBrowserStatsRule Clone() => new()
    {
        Kind = Kind,
        DateSource = DateSource,
        DateBucket = DateBucket,
        Separator = Separator,
        PartIndex = PartIndex,
        RegexPattern = RegexPattern,
        RegexGroup = RegexGroup
    };
}

public sealed class ImageBrowserStatsValue
{
    public string Label { get; init; } = string.Empty;
    public int Count { get; init; }
    public long Size { get; init; }
    public double Fraction { get; init; }
    public double Share { get; init; }
    public string ShareText { get; init; } = "0%";
    public bool IsNoValue { get; init; }
    public bool IsPeak { get; init; }

    public string CountText => Count.ToString("N0", CultureInfo.CurrentCulture);
    public string SizeText => Size.ToSizeString();
    public string ToolTip => $"{Label}: {CountText} ({ShareText}){System.Environment.NewLine}{SizeText}";
}

public sealed class ImageBrowserStatsRuleResult
{
    public ImageBrowserStatsRule Rule { get; init; } = new();
    public int TotalFiles { get; init; }
    public long TotalSize { get; init; }
    public IReadOnlyList<ImageBrowserStatsValue> Values { get; init; } = [];
    public int ValueCount => Values.Count(value => !value.IsNoValue);
    public int NoValueCount { get; init; }
    public long NoValueSize { get; init; }
    public string? Error { get; init; }
    public bool HasError => !string.IsNullOrEmpty(Error);
}

public sealed record ImageBrowserStatsSnapshot(int FileCount, long TotalSize, IReadOnlyList<ImageBrowserStatsRuleResult> Rules);
