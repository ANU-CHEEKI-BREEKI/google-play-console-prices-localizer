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
        public const string KeyHeader = "key";

        /// <summary>
        /// Reads a translations csv back.
        ///
        /// The column headers are the language names the export wrote, which are not always the locale
        /// codes the api wants - 'id-ID' in the csv is 'id' to Google. <paramref name="known"/> is the
        /// locales json, and it is what maps a column back to its locale. A column the file says
        /// nothing about is taken as a locale code as is.
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

            var locales = table.Headers
                .Where(h => !string.Equals(h, keyHeader, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(h))
                .Select(h => new LocaleColumn(LocaleOf(h, known), h))
                .ToList();

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

                foreach (var locale in locales)
                {
                    if (!row.TryGetValue(locale.Column, out var value) || string.IsNullOrWhiteSpace(value))
                        continue;

                    values[locale.Locale] = value;
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
