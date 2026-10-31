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

namespace ShareX.HelpersLib
{
    public class CodeMenuEntryInputFileName : CodeMenuEntry
    {
        protected override string Prefix { get; } = "%";

        public static readonly CodeMenuEntryInputFileName fileName =
            new CodeMenuEntryInputFileName("input_file_name", Localization.Strings.ReplCodeMenuEntry_fn_input_file_name, Localization.Strings.ReplCodeMenuCategory_Input_File);

        public static readonly CodeMenuEntryInputFileName fileExt =
            new CodeMenuEntryInputFileName("input_file_ext", Localization.Strings.ReplCodeMenuEntry_fe_input_file_extension, Localization.Strings.ReplCodeMenuCategory_Input_File);

        public static readonly CodeMenuEntryInputFileName filePath =
            new CodeMenuEntryInputFileName("input_file_path", Localization.Strings.ReplCodeMenuEntry_fp_input_file_path, Localization.Strings.ReplCodeMenuCategory_Input_File);

        public static readonly CodeMenuEntryInputFileName folderPath =
            new CodeMenuEntryInputFileName("input_folder_path", Localization.Strings.ReplCodeMenuEntry_ff_input_folder_path, Localization.Strings.ReplCodeMenuCategory_Input_Folder);

        public static readonly CodeMenuEntryInputFileName folderName =
            new CodeMenuEntryInputFileName("input_folder_name", Localization.Strings.ReplCodeMenuEntry_fnm_input_folder_name, Localization.Strings.ReplCodeMenuCategory_Input_Folder);

        public CodeMenuEntryInputFileName(string value, string description, string category = null) : base(value, description, category)
        {
        }
    }
}
