namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    /// <summary>one csv row: the key it was written under, and its value per locale</summary>
    public class TranslationRow
    {
        /// <summary>the whole key cell, '&lt;id&gt;.name' or '&lt;id&gt;.title' and so on</summary>
        public string Key { get; init; } = "";

        /// <summary>what the key points at, everything before the last dot</summary>
        public string Id { get; init; } = "";

        /// <summary>the part after the last dot, lowercased: 'name', 'description', 'title'</summary>
        public string Field { get; init; } = "";

        /// <summary>locale code -> value. Only non empty cells are in here</summary>
        public Dictionary<string, string> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>a parsed translations csv, the shape both 'locales export' subcommands write</summary>
    public class TranslationsCsv
    {
        /// <summary>the language columns, in the order the file has them</summary>
        public List<LocaleColumn> Locales { get; init; } = [];

        public List<TranslationRow> Rows { get; init; } = [];

        /// <summary>rows grouped by what they translate, in first seen order</summary>
        public IEnumerable<IGrouping<string, TranslationRow>> ById
            => Rows.GroupBy(r => r.Id, StringComparer.Ordinal);
    }

    public static class Translations
    {
        public const string KeyHeader = LocaleColumns.KeyColumn;

        /// <summary>
        /// Reads a translations csv back.
        ///
        /// A language column is 'English (United States)(en-US)': the code in the trailing parentheses
        /// is the csv side of the locale, which is not always what the api wants - 'id-ID' in the csv
        /// is 'id' to Google. <paramref name="known"/> is the locales json, and it is what maps that
        /// code back to its locale. A code the file says nothing about is taken as a locale as is.
        /// A header with no parentheses at all is read as a bare code, the shape older exports wrote.
        /// 'Key' and 'Shared Comments' are bookkeeping and never data.
        ///
        /// Empty cells are dropped here rather than later: an importer must be able to tell "not
        /// translated yet" from "translated to an empty string", and only the first of those exists.
        /// </summary>
        public static async Task<TranslationsCsv> LoadAsync(string path, List<LocaleColumn> known, bool verbose)
        {
            var table = await CommandLinesUtils.LoadCsvTable(path, path, verbose);

            var keyHeader = table.Headers.FirstOrDefault(h => string.Equals(h, KeyHeader, StringComparison.OrdinalIgnoreCase));
            if (keyHeader is null)
                throw new InvalidOperationException($"the csv has no '{KeyHeader}' column, its headers are: {string.Join(", ", table.Headers)}");

            var columns = table.Headers
                .Where(h => !string.IsNullOrWhiteSpace(h) && !LocaleColumns.IsBookkeeping(h))
                .Select(h => new { Header = h, Code = LocaleColumns.Extract(h) ?? h })
                .Select(c => new { c.Header, Locale = new LocaleColumn(LocaleOf(c.Code, known), c.Code) })
                .ToList();

            var locales = columns.Select(c => c.Locale).ToList();

            var rows = new List<TranslationRow>();

            foreach (var row in table.Rows)
            {
                var key = row.TryGetValue(keyHeader, out var cell) ? cell.Trim() : "";
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                // a product id may itself contain dots, the field is only ever the last segment
                var dot = key.LastIndexOf('.');
                if (dot <= 0 || dot == key.Length - 1)
                {
                    Console.WriteLine($"Warning: key '{key}' has no '.field' suffix, skipped.");
                    continue;
                }

                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var column in columns)
                {
                    if (!row.TryGetValue(column.Header, out var value) || string.IsNullOrWhiteSpace(value))
                        continue;

                    values[column.Locale.Locale] = value;
                }

                rows.Add(new TranslationRow
                {
                    Key = key,
                    Id = key[..dot],
                    Field = key[(dot + 1)..].ToLowerInvariant(),
                    Values = values,
                });
            }

            return new TranslationsCsv { Locales = locales, Rows = rows };
        }

        static string LocaleOf(string column, List<LocaleColumn> known)
            => known.FirstOrDefault(k => string.Equals(k.Column, column, StringComparison.OrdinalIgnoreCase))?.Locale
               ?? column;
    }
}
