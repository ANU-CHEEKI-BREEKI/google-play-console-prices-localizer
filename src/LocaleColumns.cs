using System.Globalization;
using System.Text.RegularExpressions;

namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    /// <summary>
    /// The translation tooling identifies a language by the code in the trailing parentheses of its
    /// column header, 'English (United States)(en-US)'. Both 'locales export' subcommands write that
    /// shape and both imports read it back, so the two halves live here instead of next to one of
    /// the commands. Same layout the iOS sibling tool writes, so one translation pipeline covers both.
    /// </summary>
    public static class LocaleColumns
    {
        public const string KeyColumn = "Key";
        public const string CommentsColumn = "Shared Comments";

        /// <summary>
        /// builds the language column header the translation tooling expects: 'English (United States)(en-US)'.
        /// the code in the trailing parentheses is the csv side of the locale - 'id-ID' for indonesian,
        /// even though the api gets 'id' - so the import can map it back through the locales json
        /// </summary>
        public static string ColumnName(LocaleColumn locale)
        {
            var name = locale.Column;

            try
            {
                var culture = CultureInfo.GetCultureInfo(locale.Column);
                if (!culture.EnglishName.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase))
                    name = culture.EnglishName;
            }
            catch (CultureNotFoundException)
            {
                // a Play locale .NET does not know ('fil', 'iw-IL'), the raw code is a good enough column title
            }

            return $"{name}({locale.Column})";
        }

        /// <summary>
        /// reads the locale code back out of a column header.
        /// returns null for the 'Key' / 'Shared Comments' columns and for anything that is not a locale
        /// </summary>
        public static string? Extract(string header)
        {
            var close = header.LastIndexOf(')');
            if (close < 0)
                return null;

            var open = header.LastIndexOf('(', close - 1);
            if (open < 0)
                return null;

            var code = header.Substring(open + 1, close - open - 1).Trim();

            // 'en', 'en-US', 'zh-Hans'. keeps a plain 'Portuguese (Brazil)' header from looking like a locale
            return Regex.IsMatch(code, "^[a-zA-Z]{2,3}(-[a-zA-Z0-9]{2,8})?$") ? code : null;
        }

        public static bool IsBookkeeping(string header)
            => string.Equals(header, KeyColumn, StringComparison.OrdinalIgnoreCase)
            || string.Equals(header, CommentsColumn, StringComparison.OrdinalIgnoreCase)
            || string.Equals(header, "Id", StringComparison.OrdinalIgnoreCase);
    }
}
