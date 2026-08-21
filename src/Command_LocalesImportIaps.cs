using Google.Apis.AndroidPublisher.v3;
using Google.Apis.AndroidPublisher.v3.Data;

namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    /// <summary>
    /// Writes a translated products csv back into the One-time product listings.
    ///
    /// Listings only. The patch carries an update mask of "listings", so prices, regions and purchase
    /// options are never part of the request and 'localize' keeps owning them.
    /// </summary>
    public class Command_LocalesImportIaps : CommandBase
    {
        const string TitleField = "title";
        const string DescriptionField = "description";

        /// <summary>google's own limits. A listing over either of them is rejected outright</summary>
        const int TitleLimit = 55;
        const int DescriptionLimit = 200;

        public override async Task ExecuteAsync()
        {
            try
            {
                var verbose = Args.HasFlag("-v");
                var dryRun = Args.HasFlag("-n") || Args.HasFlag("--dry-run");

                if (string.IsNullOrWhiteSpace(Package))
                {
                    Console.WriteLine("no package name. specify it in config.json or with --package");
                    return;
                }

                var path = Config.IapTranslationsFilePath;
                if (!File.Exists(path))
                {
                    Console.WriteLine($"[ERROR] no csv at {Path.GetFullPath(path)}");
                    Console.WriteLine("        run 'locales export iaps' first, or pass --csv <path>");
                    return;
                }

                var csv = await Translations.LoadAsync(path, await LoadLocalesFile(verbose), verbose);

                if (csv.Rows.Count == 0)
                {
                    Console.WriteLine("the csv has no rows to import.");
                    return;
                }

                Console.WriteLine($"read {csv.Rows.Count} key(s) in {csv.Locales.Count} language(s) from {Path.GetFullPath(path)}");
                Console.WriteLine("receiving IAP list...");

                var wanted = Extensions.ParseIapFilter(IapFilter);

                var products = (await Service!.Monetization.Onetimeproducts.ListAllAsync(Package))
                    .Where(p => !string.IsNullOrWhiteSpace(p.ProductId))
                    .ToDictionary(p => p.ProductId!, StringComparer.Ordinal);

                var changed = new List<OneTimeProduct>();
                var unchanged = 0;
                var unknown = new List<string>();

                foreach (var group in csv.ById)
                {
                    if (wanted.Count > 0 && !wanted.Contains(group.Key))
                        continue;

                    if (!products.TryGetValue(group.Key, out var product))
                    {
                        unknown.Add(group.Key);
                        continue;
                    }

                    if (Merge(product, group, verbose) > 0)
                        changed.Add(product);
                    else
                        unchanged++;
                }

                foreach (var id in unknown)
                    Console.WriteLine($"Warning: no One-time product '{id}' in this app, skipped.");

                Console.WriteLine();
                Console.WriteLine($"{changed.Count} product(s) to update, {unchanged} already up to date, {unknown.Count} unknown.");

                if (changed.Count == 0)
                    return;

                if (dryRun)
                {
                    Console.WriteLine();
                    Console.WriteLine("dry run, nothing was sent:");
                    foreach (var product in changed)
                        Console.WriteLine($"        {product.ProductId}  {product.Listings?.Count ?? 0} listing(s)");
                    return;
                }

                Console.WriteLine();

                var written = 0;

                foreach (var product in changed)
                {
                    // listings only: without the mask the patch would be read as "here is the whole
                    // product", and everything this command does not know about would be wiped
                    var request = Service.Monetization.Onetimeproducts.Patch(product, Package, product.ProductId);
                    request.UpdateMask = "listings";
                    // a listing change should show up fast, there is no region list to grind through
                    request.LatencyTolerance = MonetizationResource.OnetimeproductsResource
                        .PatchRequest.LatencyToleranceEnum.PRODUCTUPDATELATENCYTOLERANCELATENCYSENSITIVE;

                    var ok = await Extensions.ExecuteWithRetryAsync(
                        async () => await request.ExecuteAsync(),
                        product.ProductId!
                    );

                    if (ok)
                    {
                        written++;
                        if (verbose)
                            Console.WriteLine($"   {product.ProductId}: {product.Listings?.Count ?? 0} listing(s)");
                    }
                }

                Console.WriteLine();
                Console.WriteLine($"updated {written} of {changed.Count} product(s). Prices were not part of the request.");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        /// <summary>
        /// Puts the csv values into a product's listings, and answers how many of them were actually a
        /// change. A value identical to what is already there is not a change, which is what keeps a
        /// re-run from sending the whole catalog again.
        ///
        /// A listing needs both a title and a description, so a locale that would end up with only one
        /// of them is dropped with a warning rather than sent and rejected.
        /// </summary>
        int Merge(OneTimeProduct product, IEnumerable<TranslationRow> rows, bool verbose)
        {
            product.Listings ??= [];

            var listings = product.Listings
                .Where(l => !string.IsNullOrWhiteSpace(l.LanguageCode))
                .ToDictionary(l => l.LanguageCode!, StringComparer.OrdinalIgnoreCase);

            var edits = 0;

            foreach (var row in rows)
            {
                var isTitle = row.Field == TitleField;

                if (!isTitle && row.Field != DescriptionField)
                {
                    Console.WriteLine($"Warning: '{row.Key}' is neither a {TitleField} nor a {DescriptionField}, skipped.");
                    continue;
                }

                var limit = isTitle ? TitleLimit : DescriptionLimit;

                foreach (var (locale, value) in row.Values)
                {
                    if (value.Length > limit)
                    {
                        Console.WriteLine($"Warning: {row.Key} [{locale}] is {value.Length} characters, the limit is {limit}. Not sent.");
                        continue;
                    }

                    if (!listings.TryGetValue(locale, out var listing))
                    {
                        listing = new OneTimeProductListing { LanguageCode = locale };
                        listings[locale] = listing;
                        product.Listings.Add(listing);
                    }

                    var current = isTitle ? listing.Title : listing.Description;
                    if (string.Equals(current, value, StringComparison.Ordinal))
                        continue;

                    if (isTitle)
                        listing.Title = value;
                    else
                        listing.Description = value;

                    edits++;

                    if (verbose)
                        Console.WriteLine($"   {row.Key} [{locale}]: {value}");
                }
            }

            // a half filled listing is rejected by the api, drop it here where the reason can be said
            var incomplete = listings.Values
                .Where(l => string.IsNullOrWhiteSpace(l.Title) || string.IsNullOrWhiteSpace(l.Description))
                .ToList();

            foreach (var listing in incomplete)
            {
                Console.WriteLine($"Warning: {product.ProductId} [{listing.LanguageCode}] has only a {(string.IsNullOrWhiteSpace(listing.Title) ? DescriptionField : TitleField)}, a listing needs both. Dropped.");
                product.Listings.Remove(listing);
                edits--;
            }

            return Math.Max(edits, 0);
        }

        public override string Name => "locales import iaps";

        public override string Description
            => "Writes a translated products csv back into the One-time product listings. Prices are never part of the request.";

        public override void PrintHelp()
        {
            Console.WriteLine("locales import iaps [--csv <path>] [--locales-file <path>] [--iap <id[,id...]>] [-n|--dry-run] [-v]");
            Console.WriteLine();
            Console.WriteLine();

            Console.WriteLine("description:");
            CommandLinesUtils.PrintDescription(Description);
            CommandLinesUtils.PrintDescription("Reads the csv 'locales export iaps' writes: one row per key, one column per language, every key a '<product_id>.title' or '<product_id>.description'.");
            CommandLinesUtils.PrintDescription("An empty cell means 'not translated yet' and is left alone - it never wipes a listing that is already there. A value identical to what Google already has is not sent, so re-running an unchanged csv does nothing.");
            CommandLinesUtils.PrintDescription("Column headers are mapped back through the locales json, so a column the export wrote as 'id-ID' still reaches the api as 'id'. A column the file says nothing about is taken as a locale code as is.");
            CommandLinesUtils.PrintDescription($"A listing needs both a title and a description, so a language that would end up with only one of them is dropped with a warning instead of being rejected by Google. Same for a title over {TitleLimit} characters or a description over {DescriptionLimit}.");
            CommandLinesUtils.PrintDescription("The patch carries an update mask of 'listings', so prices, regions and purchase options are not in the request at all.");

            Console.WriteLine();
            Console.WriteLine("options:");

            CommandLinesUtils.PrintOption(
                "--csv <path>",
                "Specifies path to the csv to read. If not specified, used path from global config json ('IapTranslationsFilePath'), which defaults to './iap-translations.csv' next to it."
            );
            CommandLinesUtils.PrintOption(
                "--locales-file <path>",
                "Specifies path to the json that maps a column name back to its locale code. Default is the path from global config json ('LocalesFilePath'), './locales.json' next to it."
            );
            CommandLinesUtils.PrintOption(
                CommandLinesUtils.IapOptionName,
                CommandLinesUtils.IapOptionDescription
            );
            CommandLinesUtils.PrintOption(
                "-n|--dry-run",
                "Show what would be written and send nothing."
            );
            CommandLinesUtils.PrintOption(
                "-v",
                "Include additional verbose output"
            );

            CommandLinesUtils.PrintCommonOptions();
        }
    }
}
