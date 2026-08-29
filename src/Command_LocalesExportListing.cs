using Google.Apis.AndroidPublisher.v3.Data;

namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    /// <summary>
    /// Exports the store listing - app name, short description, full description and the promo video
    /// url - into the same csv shape the other exports write: one row per field, one column per
    /// language.
    ///
    /// Read only. The edit it opens to reach the listings is thrown away, so nothing in the console
    /// changes.
    /// </summary>
    public class Command_LocalesExportListing : Command_LocalesListingBase
    {
        public override async Task ExecuteAsync()
        {
            try
            {
                var verbose = Args.HasFlag("-v");

                if (string.IsNullOrWhiteSpace(Package))
                {
                    Console.WriteLine("no package name. specify it in config.json or with --package");
                    return;
                }

                var path = Config.ListingTranslationsFilePath;
                if (string.IsNullOrWhiteSpace(path) || Directory.Exists(path))
                {
                    Console.WriteLine($"[ERROR] '{path}' is not a file to write the csv into.");
                    Console.WriteLine("        set 'ListingTranslationsFilePath' in your config.json, or pass --csv <path>");
                    return;
                }

                Console.WriteLine("reading the store listing...");

                AppEdit? edit = null;
                try
                {
                    edit = await Service!.Edits.Insert(null, Package).ExecuteAsync();

                    var listings = ((await Service.Edits.Listings.List(Package, edit.Id).ExecuteAsync()).Listings ?? [])
                        .Where(l => !string.IsNullOrWhiteSpace(l.Language))
                        .ToDictionary(l => l.Language!, StringComparer.OrdinalIgnoreCase);

                    var locales = await ResolveLocales(listings.Keys, verbose);

                    if (locales.Count == 0)
                    {
                        Console.WriteLine("the store listing has no languages yet, and no locales are configured. Nothing to put in the columns.");
                        return;
                    }

                    listings.TryGetValue(locales[0].Locale, out var leading);
                    var appName = leading?.Title ?? Package;

                    var rows = new List<List<string>>
                    {
                        BuildRow(TitleField, Comment(appName, "App name", TitleLimit), listings, locales, l => l.Title),
                        BuildRow(ShortField, Comment(appName, "Short description", ShortLimit), listings, locales, l => l.ShortDescription),
                        BuildRow(FullField, Comment(appName, "Full description", FullLimit), listings, locales, l => l.FullDescription),
                        BuildRow(VideoField, $"Store listing of '{appName}' > Promo video, a youtube url. Optional, and usually the same everywhere.", listings, locales, l => l.Video),
                    };

                    List<string> headers = [LocaleColumns.KeyColumn, LocaleColumns.CommentsColumn, .. locales.Select(LocaleColumns.ColumnName)];

                    await CommandLinesUtils.SaveCsv(path, headers, rows);

                    Console.WriteLine();
                    Console.WriteLine($"written: {Path.GetFullPath(path)}");
                    Console.WriteLine($"{rows.Count} key(s) in {locales.Count} language(s): {string.Join(", ", locales)}");

                    PrintCoverage(rows, locales);
                    PrintLimits(rows, locales);
                }
                finally
                {
                    await DiscardEdit(edit, verbose);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        static string Comment(string appName, string field, int limit)
            => $"Store listing of '{appName}' > {field}. Max {limit} characters.";

        static List<string> BuildRow(
            string field,
            string comment,
            Dictionary<string, Listing> listings,
            List<LocaleColumn> locales,
            Func<Listing, string?> value
        )
            => [$"{ListingId}.{field}", comment, .. locales.Select(l => listings.TryGetValue(l.Locale, out var listing) ? value(listing) ?? "" : "")];

        /// <summary>
        /// How much of each language is actually filled in. The video row is left out of the count,
        /// it is optional and would make every language look unfinished.
        /// </summary>
        static void PrintCoverage(List<List<string>> rows, List<LocaleColumn> locales)
        {
            var text = rows.Where(r => r[0] != $"{ListingId}.{VideoField}").ToList();

            Console.WriteLine();
            Console.WriteLine("filled in:");

            for (int i = 0; i < locales.Count; i++)
            {
                // +2 for the key and comment columns
                var filled = text.Count(r => !string.IsNullOrWhiteSpace(r[i + 2]));
                var note = filled == 0 ? "  <- empty, ready to translate" : "";
                Console.WriteLine($"        {locales[i].Column,-10} {filled,4} of {text.Count} key(s){note}");
            }
        }

        /// <summary>
        /// Google caps the app name at 30 characters, the short description at 80 and the full one
        /// at 4000, and a translation is routinely longer than the english it came from. Worth
        /// knowing before the csv goes out, not after it comes back.
        /// </summary>
        static void PrintLimits(List<List<string>> rows, List<LocaleColumn> locales)
        {
            var over = new List<string>();

            foreach (var row in rows)
            {
                var field = row[0][(row[0].LastIndexOf('.') + 1)..];
                if (LimitOf(field) is not int limit)
                    continue;

                for (int i = 0; i < locales.Count; i++)
                {
                    var value = row[i + 2];
                    if (value.Length > limit)
                        over.Add($"        {row[0]} [{locales[i].Column}] is {value.Length}, the limit is {limit}");
                }
            }

            if (over.Count == 0)
                return;

            Console.WriteLine();
            Console.WriteLine("too long for Google:");
            foreach (var line in over)
                Console.WriteLine(line);
        }

        public override string Name => "locales export listing";

        public override string Description
            => "Exports the store listing - app name, short and full description - into a csv, one row per field and one column per language, ready to be fed to a translation service.";

        public override void PrintHelp()
        {
            Console.WriteLine("locales export listing [--csv <path>] [--source-locales <code[,code...]>] [--locales-file <path>] [--locales <code[,code...]>] [--all-locales] [-v]");
            Console.WriteLine();
            Console.WriteLine();

            Console.WriteLine("description:");
            CommandLinesUtils.PrintDescription(Description);
            CommandLinesUtils.PrintDescription($"Columns: '{LocaleColumns.KeyColumn}', '{LocaleColumns.CommentsColumn}', then one column per language named 'English (United States)(en-US)' - the locale code in the trailing parentheses is what the import reads. Four rows: '{ListingId}.{TitleField}', '{ListingId}.{ShortField}', '{ListingId}.{FullField}' and '{ListingId}.{VideoField}', because a translation service wants one string per row.");
            CommandLinesUtils.PrintDescription("Read only: the edit it opens to reach the listings is discarded, nothing in the console changes.");
            CommandLinesUtils.PrintDescription("By default only languages the store listing already has get a column, in the order the locales json names them. The source locales lead and are always there, even empty. --all-locales adds a column for every entry of the locales json - translate into the empty column and import, that is how a store language is added.");
            CommandLinesUtils.PrintDescription($"An app name over {TitleLimit} characters, a short description over {ShortLimit} or a full one over {FullLimit} is reported at the end, because Google rejects those and a translation is routinely longer than its english.");
            CommandLinesUtils.PrintDescription("An existing csv at the target path is overwritten.");

            Console.WriteLine();
            Console.WriteLine("options:");

            CommandLinesUtils.PrintOption(
                "--csv <path>",
                "Specifies path to the csv to write. If not specified, used path from global config json ('ListingTranslationsFilePath'), which defaults to './listing-translations.csv' next to it."
            );
            CommandLinesUtils.PrintOption(
                "--source-locales <code[,code...]>",
                "Locales that lead the columns, in this exact order, always exported even when empty. Default is the list from global config.json ('SourceLocales'), and without one the single 'DefaultLanguageCode' leads."
            );
            CommandLinesUtils.PrintOption(
                "--locales-file <path>",
                "Specifies path to the json that orders the columns and, with --all-locales, names the extra ones. Default is the path from global config json ('LocalesFilePath'), './locales.json' next to it."
            );
            CommandLinesUtils.PrintOption(
                "--locales <code[,code...]>",
                "Locales to export columns for, for this run only, empty or not. Overrides the whole locales json file."
            );
            CommandLinesUtils.PrintOption(
                "--all-locales",
                "Also produce a column for every locale in the locales json, even ones the store listing does not have yet. This is how a new store language is started: translate into the empty column and import."
            );
            CommandLinesUtils.PrintOption(
                "-v",
                "Include additional verbose output"
            );

            CommandLinesUtils.PrintCommonOptions();
        }
    }
}
