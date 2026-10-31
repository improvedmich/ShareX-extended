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

using System;
using System.Text;

namespace ShareX.HelpersLib
{
    /// <summary>
    /// Shared parser for output file name masks in tools that process input files
    /// (VideoConverter, ImageConverter, ImageResizer, ImageThumbnailer, ImageWatermark).
    ///
    /// Supports:
    ///   - Legacy $filename token (backward compatible with existing configs)
    ///   - %input_file_name%   - file name without extension
    ///   - %input_file_ext%    - file extension including dot
    ///   - %input_file_path%   - full path to the input file
    ///   - %input_folder_path% - directory containing the input file
    ///   - %input_folder_name% - directory name
    ///   - All NameParser tokens (%y, %mo, %d, %h, %mi, %s, %i, %ra, %guid, ...)
    ///
    /// Each token is replaced by its own unique sentinel before NameParser runs,
    /// then each sentinel is replaced by its real value after NameParser finishes.
    /// This prevents %i inside %input_file_name% from triggering the auto-increment block,
    /// and prevents % sequences inside the actual file name from being mistaken for tokens.
    /// </summary>
    public static class InputFileNameParser
    {
        private const string LegacyFilenameToken = "$filename";

        // Default output file name mask for every tool that parses input file name patterns
        // (VideoConverter, ImageConverter, ImageResizer, ImageThumbnailer, ImageWatermark).
        public const string DefaultMask = "%input_file_name%";

        public static string Parse(string pattern, string inputFilePath, int autoIncrementNumber = 0,
            NameParserType parserType = NameParserType.FileName)
        {
            if (string.IsNullOrEmpty(pattern)) return string.Empty;
            if (string.IsNullOrEmpty(inputFilePath)) return pattern;

            string baseName = System.IO.Path.GetFileNameWithoutExtension(inputFilePath);
            string ext = System.IO.Path.GetExtension(inputFilePath);
            string fullFolder = System.IO.Path.GetDirectoryName(inputFilePath) ?? string.Empty;
            string folderName = System.IO.Path.GetFileName(fullFolder);

            // Unique sentinel per token so each gets its own value back after NameParser runs
            string id = Guid.NewGuid().ToString("N");
            string sName  = "$" + id + "n$";
            string sExt   = "$" + id + "e$";
            string sPath  = "$" + id + "p$";
            string sFold  = "$" + id + "f$";
            string sFoldN = "$" + id + "m$";

            StringBuilder sb = new StringBuilder(pattern);

            // Legacy $filename behaves the same as %input_file_name%
            sb.Replace(LegacyFilenameToken, sName);

            // Replace both %token% and %token forms (longer first)
            ReplaceInputToken(sb, CodeMenuEntryInputFileName.folderName.ToPrefixString(), sFoldN);
            ReplaceInputToken(sb, CodeMenuEntryInputFileName.folderPath.ToPrefixString(),  sFold);
            ReplaceInputToken(sb, CodeMenuEntryInputFileName.filePath.ToPrefixString(),     sPath);
            ReplaceInputToken(sb, CodeMenuEntryInputFileName.fileExt.ToPrefixString(),      sExt);
            ReplaceInputToken(sb, CodeMenuEntryInputFileName.fileName.ToPrefixString(),     sName);

            // Now run NameParser for all standard tokens (%y, %mo, %i, %ra, ...)
            NameParser nameParser = new NameParser(parserType)
            {
                AutoIncrementNumber = autoIncrementNumber
            };

            string result = nameParser.Parse(sb.ToString());

            // Replace sentinels with actual values (sanitised for file names)
            string safeName  = FileHelpers.SanitizeFileName(baseName);
            string safeExt   = FileHelpers.SanitizeFileName(ext);
            string safePath  = parserType == NameParserType.FilePath ? FileHelpers.SanitizePath(inputFilePath) : inputFilePath;
            string safeFold  = FileHelpers.SanitizePath(fullFolder);
            string safeFoldN = FileHelpers.SanitizeFileName(folderName);

            result = result.Replace(sName,  safeName);
            result = result.Replace(sExt,   safeExt);
            result = result.Replace(sPath,  safePath);
            result = result.Replace(sFold,  safeFold);
            result = result.Replace(sFoldN, safeFoldN);

            return result;
        }

        private static void ReplaceInputToken(StringBuilder sb, string token, string sentinel)
        {
            // Match %token% first (longer) then %token (without closing %)
            sb.Replace(token + "%", sentinel);
            sb.Replace(token, sentinel);
        }

        /// <summary>
        /// Returns a preview string for the mask, useful for UI previews.
        /// </summary>
        public static string Preview(string pattern, string sampleFilePath = @"C:\Media\Screenshot 2026-01-15 14-30-00.png")
        {
            return Parse(pattern, sampleFilePath, 1, NameParserType.FileName);
        }
    }
}
