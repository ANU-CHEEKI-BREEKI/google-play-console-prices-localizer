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

                // Google demands a regions version on any product write, even one that only touches
                // text. Every listed product already carries the one it was last written with, so the
                // exchange rate endpoint is only asked when none of them does
                var regionsVersion = changed.Select(p => p.RegionsVersion?.Version).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
                    ?? products.Values.Select(p => p.RegionsVersion?.Version).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
                    ?? await CurrentRegionsVersion();

                if (string.IsNullOrWhiteSpace(regionsVersion))
                {
                    Console.WriteLine("[ERROR] could not work out the regions version, and Google rejects a product write without one.");
                    return;
                }

                if (verbose)
                    Console.WriteLine($"regions version: {regionsVersion}");

                // one batch, not thirty patches. Google writes a product in its own time, and asked
                // one at a time it took a quarter of an hour each - the same thirty products go
                // through in about two minutes when they are sent together
                var body = new BatchUpdateOneTimeProductsRequest
                {
                    Requests = [.. changed.Select(product => new UpdateOneTimeProductRequest
                    {
                        OneTimeProduct = product,

                        // listings only: without the mask this would be read as "here is the whole
                        // product", and everything this command does not know about would be wiped
                        UpdateMask = "listings",

                        RegionsVersion = new RegionsVersion { Version = regionsVersion },
                        LatencyTolerance = Extensions.LatencyTolerant,
                    })],
                };

                var refused = new List<string>();

                // a batch is all or nothing, and one language Google will not take sinks all thirty
                // products. It does name the language though, so dropping and resending gets there
                for (int round = 0; round <= csv.Locales.Count; round++)
                {
                    Console.WriteLine($"   -> updating {changed.Count} product(s) in one request...");
                    Console.WriteLine("      Google takes a couple of minutes to write a batch, this is normal.");

                    var error = await SendBatch(body);

                    if (error is null)
                    {
                        Console.WriteLine();
                        Console.WriteLine($"updated {changed.Count} product(s). Prices were not part of the request.");

                        if (verbose)
                        {
                            foreach (var product in changed)
                                Console.WriteLine($"   {product.ProductId}: {product.Listings?.Count ?? 0} listing(s)");
                        }

                        if (refused.Count > 0)
                        {
                            Console.WriteLine();
                            Console.WriteLine($"{refused.Count} language(s) were NOT imported: {string.Join(", ", refused)}");
                            Console.WriteLine("Google does not accept these codes for product listings at all. Take them out of your locales json and out of the csv.");
                        }

                        return;
                    }

                    var unsupported = UnsupportedLanguage(error.Message);

                    if (unsupported is null)
                    {
                        Console.WriteLine();
                        Console.WriteLine("[ERROR] Google Play rejected the batch, and a batch is all or nothing.");
                        Console.WriteLine($"        {error.Message.Trim()}");
                        Console.WriteLine("        Nothing was written. Re-run for one product at a time with --iap <id> to narrow it down.");
                        return;
                    }

                    Console.WriteLine($"   Google refuses '{unsupported}', dropping it and sending again");
                    refused.Add(unsupported);

                    foreach (var product in changed)
                        DropLocale(product, unsupported);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        /// <summary>
        /// Sends the batch, riding out the 503s Google throws at a big one. null means it went
        /// through, anything else is Google's own objection and worth reading
        /// </summary>
        async Task<Google.GoogleApiException?> SendBatch(BatchUpdateOneTimeProductsRequest body)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    await Extensions.Timed("the listings update", async () =>
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(15));
                        await Service!.Monetization.Onetimeproducts.BatchUpdate(body, Package).ExecuteAsync(timeout.Token);
                    });

                    return null;
                }
                catch (Google.GoogleApiException ex)
                {
                    // 5xx is Google being busy, not Google disagreeing
                    if ((int)ex.HttpStatusCode < 500)
                        return ex;

                    Console.WriteLine($"      {ex.HttpStatusCode}, retrying...");
                    await Task.Delay(TimeSpan.FromSeconds(5 * (attempt + 1)));
                }
            }

            return null;
        }

        /// <summary>
        /// the language out of 'Product "x": Language bs isn't supported. Choose another language.'
        /// null when the message is not that one
        /// </summary>
        static string? UnsupportedLanguage(string message)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                message,
                @"Language (?<locale>[A-Za-z0-9\-_]+) isn't supported"
            );

            return match.Success ? match.Groups["locale"].Value : null;
        }

        static void DropLocale(OneTimeProduct product, string locale)
        {
            if (product.Listings is null)
                return;

            foreach (var listing in product.Listings.ToList())
            {
                if (string.Equals(listing.LanguageCode, locale, StringComparison.OrdinalIgnoreCase))
                    product.Listings.Remove(listing);
            }
        }

        /// <summary>
        /// The regions version Google is on right now. Read off the exchange rate endpoint, which is
        /// the only place the api hands it out, and the price it is asked about does not matter
        /// </summary>
        async Task<string?> CurrentRegionsVersion()
        {
            var response = await Service!.Monetization
                .ConvertRegionPrices(new ConvertRegionPricesRequest
                {
                    Price = new Money { CurrencyCode = "USD", Units = 1, Nanos = 0 }
                }, Package)
                .ExecuteAsync();

            return response.RegionVersion?.Version;
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
