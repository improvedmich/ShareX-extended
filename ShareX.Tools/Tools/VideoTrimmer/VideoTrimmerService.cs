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

using ShareX.Tools.Localization;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ShareX.Tools;

/// <summary>Cancellable FFmpeg exports. Preview, metadata and playback use Windows Media Foundation.</summary>
internal sealed class VideoTrimmerService(string ffmpegPath)
{
    public string FFmpegPath { get; set; } = ffmpegPath;
    internal static string Timestamp(double seconds) => seconds.ToString("0.######", CultureInfo.InvariantCulture);

    internal static string[] BuildTrimArguments(string input, string output, double start, double end, bool precise)
    {
        List<string> args = ["-v", "error", "-ss", Timestamp(start), "-i", input, "-t", Timestamp(end - start),
            "-map", "0:V:0", "-map", "0:a?", "-map_chapters", "-1"];
        if (precise)
        {
            args.AddRange(["-c:v", "libx264", "-preset", "fast", "-crf", "18", "-vf", "pad=ceil(iw/2)*2:ceil(ih/2)*2",
                "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart"]);
        }
        else
        {
            // Keep the muxer's edit list instead of forcing zero-based timestamps: with an input seek the
            // copied stream starts at the preceding keyframe, and make_zero would expose that extra head
            // material instead of trimming it.
            args.AddRange(["-map", "0:s?", "-c", "copy"]);
        }

        args.AddRange(["-progress", "pipe:1", "-nostats", "-n", output]);
        return args.ToArray();
    }

    internal static string[] BuildConcatArguments(string listFile, string output, bool precise)
    {
        List<string> args = ["-v", "error", "-f", "concat", "-safe", "0", "-i", listFile,
            "-map", "0:V:0", "-map", "0:a?", "-map", "0:s?", "-map_chapters", "-1", "-c", "copy"];
        if (precise) args.AddRange(["-movflags", "+faststart"]);
        args.AddRange(["-progress", "pipe:1", "-nostats", "-n", output]);
        return args.ToArray();
    }

    private static string GetOutputExtension(string input, bool precise) => precise ? ".mp4" : Path.GetExtension(input);

    private static void ValidateRange(double start, double end, double duration)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start || end > duration)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }
    }

    private static void ValidateOutput(string input, string output, bool precise)
    {
        if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(Strings.VideoTrimmer_SourceOverwrite);
        }

        string extension = GetOutputExtension(input, precise);
        if (!string.Equals(Path.GetExtension(output), extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(string.Format(Strings.VideoTrimmer_OutputExtension, extension));
        }
    }

    private static void EnsureOutputExists(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            throw new InvalidOperationException(Strings.VideoTrimmer_EmptyOutput);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    // Concat demuxer paths are single-quoted; an embedded quote needs the shell-like '\'' escape.
    private static string QuoteConcatPath(string path) =>
        $"file '{path.Replace("\\", "/").Replace("'", "'\\''")}'";

    public async Task TrimAsync(string input, string output, double start, double end, double duration,
        bool precise, IProgress<double> progress, CancellationToken token)
    {
        ValidateRange(start, end, duration);
        ValidateOutput(input, output, precise);

        // Publish only after success; cancellation or a muxer failure leaves an existing destination intact.
        string extension = GetOutputExtension(input, precise);
        string temporary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, $".sharex-trim-{Guid.NewGuid():N}{extension}");
        try
        {
            await RunAsync(BuildTrimArguments(input, temporary, start, end, precise), token, progress: progress, duration: end - start);
            token.ThrowIfCancellationRequested();
            EnsureOutputExists(temporary);
            File.Move(temporary, output, true);
        }
        finally
        {
            DeleteIfExists(temporary);
        }
    }

    /// <summary>Removes all ranges and joins the surviving parts into one export.</summary>
    public async Task DeleteRangesAsync(string input, string output, IReadOnlyList<(double Start, double End)> ranges,
        double duration, bool precise, IProgress<double> progress, CancellationToken token)
    {
        if (!double.IsFinite(duration) || duration <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        ValidateOutput(input, output, precise);

        // Cuts this close to an edge would leave nothing meaningful, so they are folded into the edge.
        const double edgeTolerance = 0.05;
        List<(double Start, double End)> merged = [];
        foreach (var range in ranges.OrderBy(range => range.Start))
        {
            double start = double.IsFinite(range.Start) ? Math.Clamp(range.Start, 0, duration) : 0;
            double end = double.IsFinite(range.End) ? Math.Clamp(range.End, 0, duration) : 0;
            if (end - start <= edgeTolerance) continue;

            if (merged.Count > 0 && start <= merged[^1].End + edgeTolerance)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
            }
            else
            {
                merged.Add((start, end));
            }
        }

        if (merged.Count == 0) throw new InvalidOperationException(Strings.VideoTrimmer_NothingToKeep);

        List<(double Start, double End)> keep = [];
        double cursor = 0;
        foreach (var range in merged)
        {
            if (range.Start - cursor > edgeTolerance) keep.Add((cursor, range.Start));
            cursor = Math.Max(cursor, range.End);
        }

        if (duration - cursor > edgeTolerance) keep.Add((cursor, duration));
        if (keep.Count == 0) throw new InvalidOperationException(Strings.VideoTrimmer_NothingToKeep);

        if (keep.Count == 1)
        {
            await TrimAsync(input, output, keep[0].Start, keep[0].End, duration, precise, progress, token);
            return;
        }

        string extension = GetOutputExtension(input, precise);
        string directory = Path.GetDirectoryName(Path.GetFullPath(output))!;
        string id = Guid.NewGuid().ToString("N");
        string list = Path.Combine(directory, $".sharex-delete-{id}-list.txt");
        string temporary = Path.Combine(directory, $".sharex-delete-{id}-out{extension}");
        List<string> parts = [];
        try
        {
            // Re-encoding and stream copy both produce parts with identical stream layouts, so the
            // concat demuxer can join them without another encode.
            double keepLength = keep.Sum(part => part.End - part.Start);
            double completed = 0;
            foreach (var (partStart, partEnd) in keep)
            {
                string part = Path.Combine(directory, $".sharex-delete-{id}-{parts.Count}{extension}");
                parts.Add(part);
                double length = partEnd - partStart;
                await RunAsync(BuildTrimArguments(input, part, partStart, partEnd, precise), token,
                    new ScaledProgress(progress, completed / keepLength * 90, length / keepLength * 90), length);
                token.ThrowIfCancellationRequested();
                EnsureOutputExists(part);
                completed += length;
            }

            File.WriteAllLines(list, parts.Select(QuoteConcatPath), new UTF8Encoding(false));
            await RunAsync(BuildConcatArguments(list, temporary, precise), token,
                new ScaledProgress(progress, 90, 10), keepLength);
            token.ThrowIfCancellationRequested();
            EnsureOutputExists(temporary);
            File.Move(temporary, output, true);
        }
        finally
        {
            foreach (string part in parts) DeleteIfExists(part);
            DeleteIfExists(list);
            DeleteIfExists(temporary);
        }
    }

    private sealed class ScaledProgress(IProgress<double> inner, double offset, double scale) : IProgress<double>
    {
        public void Report(double value) => inner.Report(offset + Math.Clamp(value, 0, 100) * scale / 100);
    }

    private async Task RunAsync(IEnumerable<string> arguments, CancellationToken token,
        IProgress<double> progress, double duration)
    {
        token.ThrowIfCancellationRequested();
        using Process process = new();
        process.StartInfo = new ProcessStartInfo(FFmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string arg in new[] { "-hide_banner", "-nostdin" }.Concat(arguments))
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        process.Start();
        using CancellationTokenRegistration registration = token.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        StringBuilder log = new();
        async Task ReadErrorsAsync()
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                log.AppendLine(line);
                if (log.Length > 32768) log.Remove(0, log.Length - 32768);
            }
        }

        async Task ReadOutputAsync()
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) &&
                    long.TryParse(line.AsSpan(12), CultureInfo.InvariantCulture, out long microseconds))
                {
                    progress.Report(Math.Clamp(microseconds / 1000000d / duration * 100, 0, 100));
                }
            }
        }

        await Task.WhenAll(ReadErrorsAsync(), ReadOutputAsync(), process.WaitForExitAsync());
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(log.ToString().Trim());
        }

    }
}
