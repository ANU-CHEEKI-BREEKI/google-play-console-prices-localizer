namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    /// <summary>
    /// Exports the title and description of every one-time product into a csv laid out the way
    /// translation tooling expects it: one row per key, one column per language.
    ///
    /// Google auto translates the store page and nothing else, so a product listing is whatever
    /// language somebody typed it in, in every country. That listing is what the Play purchase sheet
    /// shows at the moment of paying, which makes it a worse place for untranslated english than the
    /// store page ever was.
    ///
    /// Not to be confused with the top level 'export-iaps', which writes the product definitions csv 'create-iaps'
    /// reads back - prices, one language, a row per product. This one is only about the text.
    /// </summary>
    public class Command_LocalesExportIaps : CommandBase
    {
        const string KeyHeader = LocaleColumns.KeyColumn;

        /// <summary>what Google truncates a listing at. Quoted in the comment column for the translator</summary>
        const int TitleLimit = 55;
        const int DescriptionLimit = 200;

        /// <summary>
        /// key suffixes. Product ids are lowercase letters, digits, underscores and dots... a dot is
        /// actually legal in a product id, so the suffix is split off at the LAST one on import
        /// </summary>
        const string TitleSuffix = ".title";
        const string DescriptionSuffix = ".description";

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

                var path = Config.IapTranslationsFilePath;
                if (string.IsNullOrWhiteSpace(path) || Directory.Exists(path))
                {
                    Console.WriteLine($"[ERROR] '{path}' is not a file to write the csv into.");
                    Console.WriteLine("        set 'IapTranslationsFilePath' in your config.json, or pass --csv <path>");
                    return;
                }

                Console.WriteLine("receiving IAP list...");

                var products = (await Service!.Monetization.Onetimeproducts.ListAllAsync(Package))
                    .Filter(IapFilter)
                    .OrderBy(p => p.ProductId, StringComparer.Ordinal)
                    .ToList();

                if (products.Count == 0)
                {
                    Console.WriteLine("no One-time products to export.");
                    return;
                }

                var locales = await ResolveLocales(
                    products.SelectMany(p => (p.Listings ?? []).Select(l => l.LanguageCode)),
                    verbose
                );

                if (locales.Count == 0)
                {
                    Console.WriteLine("no listings at all, and no locales configured. nothing to put in the columns.");
                    return;
                }

                Console.WriteLine($"exporting {products.Count} product(s) in {locales.Count} language(s) into {Path.GetFullPath(path)}...");

                var rows = new List<List<string>>();

                foreach (var product in products)
                {
                    var listings = (product.Listings ?? [])
                        .Where(l => !string.IsNullOrWhiteSpace(l.LanguageCode))
                        .ToDictionary(l => l.LanguageCode!, StringComparer.OrdinalIgnoreCase);

                    // title and description are two separate keys: a translation service wants one
                    // string per row, and the two are translated independently anyway
                    listings.TryGetValue(locales[0].Locale, out var leading);
                    var title = leading?.Title ?? product.ProductId;

                    rows.Add(BuildRow(product.ProductId + TitleSuffix, Comment(title, "Title", TitleLimit), listings, locales, l => l.Title));
                    rows.Add(BuildRow(product.ProductId + DescriptionSuffix, Comment(title, "Description", DescriptionLimit), listings, locales, l => l.Description));

                    if (verbose)
                        Console.WriteLine($"   {product.ProductId}: \"{leading?.Title ?? ""}\", {listings.Count} listing(s)");
                }

                List<string> headers = [KeyHeader, LocaleColumns.CommentsColumn, .. locales.Select(LocaleColumns.ColumnName)];

                await CommandLinesUtils.SaveCsv(path, headers, rows);

                Console.WriteLine();
                Console.WriteLine($"written: {Path.GetFullPath(path)}");
                Console.WriteLine($"{rows.Count} key(s) from {products.Count} product(s), {locales.Count} language(s): {string.Join(", ", locales)}");

                PrintCoverage(rows, locales);
                PrintLimits(rows, locales);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        static string Comment(string title, string field, int limit)
            => $"In-App Purchase '{title}' > {field}. Max {limit} characters.";

        static List<string> BuildRow(
            string key,
            string comment,
            Dictionary<string, Google.Apis.AndroidPublisher.v3.Data.OneTimeProductListing> listings,
            List<LocaleColumn> locales,
            Func<Google.Apis.AndroidPublisher.v3.Data.OneTimeProductListing, string?> field
        )
            => [key, comment, .. locales.Select(l => listings.TryGetValue(l.Locale, out var listing) ? field(listing) ?? "" : "")];

        /// <summary>
        /// How much of each language is actually filled in. Without this the csv looks complete the
        /// moment it has columns, and a half translated language is exactly the thing worth seeing.
        /// </summary>
        static void PrintCoverage(List<List<string>> rows, List<LocaleColumn> locales)
        {
            Console.WriteLine();
            Console.WriteLine("filled in:");

            for (int i = 0; i < locales.Count; i++)
            {
                // +2 for the key and comment columns
                var filled = rows.Count(r => !string.IsNullOrWhiteSpace(r[i + 2]));
                var note = filled == 0 ? "  <- empty, ready to translate" : "";
                Console.WriteLine($"        {locales[i].Column,-10} {filled,4} of {rows.Count} key(s){note}");
            }
        }

        /// <summary>
        /// Google truncates a product title at 55 characters and a description at 200, and a
        /// translation is routinely longer than the english it came from. Worth knowing before the
        /// csv goes out, not after it comes back.
        /// </summary>
        static void PrintLimits(List<List<string>> rows, List<LocaleColumn> locales)
        {
            var over = new List<string>();

            foreach (var row in rows)
            {
                var limit = row[0].EndsWith(TitleSuffix, StringComparison.Ordinal) ? TitleLimit : DescriptionLimit;

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

        public override string Name => "locales export iaps";

        public override string Description
            => "Exports the title and description of every One-time product into a csv, one row per key and one column per language, ready to be fed to a translation service.";

        public override void PrintHelp()
        {
            Console.WriteLine("locales export iaps [--csv <path>] [--source-locales <code[,code...]>] [--locales-file <path>] [--locales <code[,code...]>] [--all-locales] [--iap <id[,id...]>] [-v]");
            Console.WriteLine();
            Console.WriteLine();

            Console.WriteLine("description:");
            CommandLinesUtils.PrintDescription(Description);
            CommandLinesUtils.PrintDescription($"Columns: '{KeyHeader}', '{LocaleColumns.CommentsColumn}' (which product, which field, how long it may be), then one column per language named 'English (United States)(en-US)' - the locale code in the trailing parentheses is what the import reads. Every product contributes two rows, '<product_id>{TitleSuffix}' and '<product_id>{DescriptionSuffix}', because a translation service wants one string per row.");
            CommandLinesUtils.PrintDescription("Google auto translates the store page and nothing else, so a product listing stays in whatever language it was typed in - and that listing is what the Play purchase sheet shows at the moment of paying.");
            CommandLinesUtils.PrintDescription("Not to be confused with 'export-iaps', the top level command that writes the product definitions csv 'create-iaps' reads back: prices, one language, one row per product. This command is only about the text.");
            CommandLinesUtils.PrintDescription("By default only languages something is already translated into get a column, in the order the locales json names them. The source locales lead and are always there, even empty. --all-locales adds a column for every entry of the locales json, for when new languages are being added.");
            CommandLinesUtils.PrintDescription("A title over 55 characters or a description over 200 is reported at the end, because Google rejects those and a translation is routinely longer than its english.");
            CommandLinesUtils.PrintDescription("An existing csv at the target path is overwritten.");

            Console.WriteLine();
            Console.WriteLine("options:");

            CommandLinesUtils.PrintOption(
                "--csv <path>",
                "Specifies path to the csv to write. If not specified, used path from global config json ('IapTranslationsFilePath'), which defaults to './iap-translations.csv' next to it."
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
                "Also produce a column for every locale in the locales json, even ones with nothing translated yet. This is how a new language is started: translate into the empty column and import."
            );
            CommandLinesUtils.PrintOption(
                CommandLinesUtils.IapOptionName,
                CommandLinesUtils.IapOptionDescription
            );
            CommandLinesUtils.PrintOption(
                "-v",
                "Include additional verbose output"
            );

            CommandLinesUtils.PrintCommonOptions();
        }
    }
}
