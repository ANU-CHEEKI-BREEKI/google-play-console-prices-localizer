using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Google.Apis.AndroidPublisher.v3.Data;
using Newtonsoft.Json;

namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    /// <summary>
    /// Creates products the way Google recommends for catalog creation: a single batchUpdate
    /// with allowMissing and LATENCY_TOLERANT. One request for every new product, counted as
    /// one call against the hourly quota, and not routed through the slow latency-sensitive path.
    /// The only cost is that the new products may take up to 24 hours to reach devices,
    /// which for a product nobody has seen yet does not matter.
    /// </summary>
    public class Command_CreateIaps : CommandBase
    {
        /// <summary>the SDK ships no constants for these enums, these are the REST values</summary>
        private const string Available = "AVAILABLE";
        private const string LatencyTolerant = "PRODUCT_UPDATE_LATENCY_TOLERANCE_LATENCY_TOLERANT";

        /// <summary>every created product gets this single backward compatible purchase option</summary>
        private const string PurchaseOptionId = "default";

        private const int MaxTitleLength = 55;
        private const int MaxDescriptionLength = 200;

        /// <summary>
        /// Measured: a create with the full region list takes Google about two minutes to answer,
        /// one with a single region three seconds. That is the API, not the request size, the
        /// 'restore' command sees the same two minutes. So the timeout has to be generous.
        /// </summary>
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

        // "must start with a number or lowercase letter, and can contain
        //  numbers (0-9), lowercase letters (a-z), underscores (_) and periods (.)"
        private static readonly Regex ProductIdPattern = new("^[a-z0-9][a-z0-9_.]*$", RegexOptions.Compiled);

        private readonly Dictionary<string, ConvertRegionPricesResponse> conversions = new();

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

                var definitions = await LoadDefinitions(verbose);
                if (definitions is null)
                    return;

                if (!string.IsNullOrEmpty(Config.Iap))
                    definitions = definitions.Where(d => d.ProductId == Config.Iap).ToList();

                if (definitions.Count == 0)
                {
                    Console.WriteLine("nothing to create, no product definitions matched.");
                    return;
                }

                Console.WriteLine("receiving IAP list...");

                var existing = await Timed("the IAP list", () => Service!.Monetization.Onetimeproducts.ListAllAsync(Package));
                var existingIds = new HashSet<string>(
                    existing.Where(p => p.ProductId is not null).Select(p => p.ProductId!),
                    StringComparer.Ordinal
                );

                var skipped = definitions.Where(d => existingIds.Contains(d.ProductId)).Select(d => d.ProductId).ToList();
                var missing = definitions.Where(d => !existingIds.Contains(d.ProductId)).ToList();

                // never touched, 'restore' and 'localize' are the commands for existing prices
                foreach (var id in skipped)
                    Console.WriteLine($"   -> [SKIP] {id}");

                if (missing.Count == 0)
                {
                    PrintSummary([], skipped, [], dryRun);
                    return;
                }

                // the SDK exposes no constants for the region list, so an existing product is
                // the only reliable source for the countries the app actually sells in
                var template = existing.FirstOrDefault(p => p.PurchaseOptions?.Any(po => po.BuyOption?.LegacyCompatible == true) == true)
                               ?? existing.FirstOrDefault();

                var templateOption = template?.PurchaseOptions?.FirstOrDefault(po => po.BuyOption?.LegacyCompatible == true)
                                     ?? template?.PurchaseOptions?.FirstOrDefault();

                if (template is null)
                    Console.WriteLine("no existing product to copy the region list from, using every region the price converter returns.");
                else if (verbose)
                    Console.WriteLine($"copying the region list from '{template.ProductId}' ({templateOption?.RegionalPricingAndAvailabilityConfigs?.Count ?? 0} region(s)).");

                var requests = new List<UpdateOneTimeProductRequest>();
                var failed = new List<string>();
                string? regionsVersion = template?.RegionsVersion?.Version;

                foreach (var definition in missing)
                {
                    var basePrice = definition.DefaultPrice;

                    // make from 10$ 9.99$
                    // YES google can make it on their side, but NOT in countries where the local currency is not supported
                    // so lets make sure here that price in EVERY country is not rounded
                    if (Math.Truncate(basePrice) == basePrice)
                        basePrice -= 0.01m;

                    ConvertRegionPricesResponse converted;
                    try
                    {
                        converted = await ConvertAsync(basePrice);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[ERROR] failed to convert prices for {definition.ProductId}: {ex.Message}");
                        failed.Add(definition.ProductId);
                        continue;
                    }

                    regionsVersion ??= converted.RegionVersion?.Version;

                    var regionConfigs = BuildRegionConfigs(converted, templateOption);
                    var product = BuildProduct(definition, regionConfigs, BuildNewRegionsConfig(converted, templateOption), templateOption);

                    if (dryRun || verbose)
                        PrintPreview(definition, product, regionConfigs, template, dryRun);

                    requests.Add(new UpdateOneTimeProductRequest
                    {
                        OneTimeProduct = product,

                        // this is what turns the update into a create
                        AllowMissing = true,

                        // required, ignored by the API when the product is actually created
                        UpdateMask = "listings,purchaseOptions",

                        RegionsVersion = new RegionsVersion { Version = regionsVersion },
                        LatencyTolerance = LatencyTolerant,
                    });
                }

                if (dryRun)
                {
                    PrintSummary(requests.Select(r => r.OneTimeProduct.ProductId).ToList(), skipped, failed, dryRun);
                    return;
                }

                var created = new List<string>();

                if (requests.Count > 0)
                {
                    var body = new BatchUpdateOneTimeProductsRequest { Requests = requests };
                    var size = JsonConvert.SerializeObject(body).Length / 1024;

                    Console.WriteLine($"   -> Creating {requests.Count} product(s) in one request ({size} KB, regions version '{regionsVersion ?? "none"}')...");
                    Console.WriteLine("      Google takes about two minutes to write a full region list, this is normal.");

                    var ok = await Extensions.ExecuteWithRetryAsync(
                        () => Timed("the create request", async () =>
                        {
                            using var timeout = new CancellationTokenSource(RequestTimeout);
                            await Service!.Monetization.Onetimeproducts.BatchUpdate(body, Package).ExecuteAsync(timeout.Token);
                        }),
                        $"{requests.Count} product(s)"
                    );

                    if (ok)
                        created.AddRange(requests.Select(r => r.OneTimeProduct.ProductId));
                    else
                        failed.AddRange(requests.Select(r => r.OneTimeProduct.ProductId));
                }

                PrintSummary(created, skipped, failed, dryRun);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        /// <summary>
        /// a silent console is indistinguishable from a hang, this says how long we have been waiting
        /// </summary>
        private static async Task<T> Timed<T>(string label, Func<Task<T>> action)
        {
            var watch = Stopwatch.StartNew();
            using var finished = new CancellationTokenSource();

            var heartbeat = Task.Run(async () =>
            {
                while (!finished.IsCancellationRequested)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(10), finished.Token); }
                    catch (OperationCanceledException) { return; }

                    Console.WriteLine($"      still waiting for {label}... {watch.Elapsed.TotalSeconds:0}s");
                }
            });

            try
            {
                var result = await action();
                Console.WriteLine($"      {label} took {watch.Elapsed.TotalSeconds:0.0}s");
                return result;
            }
            finally
            {
                finished.Cancel();
                await heartbeat;
            }
        }

        private static Task Timed(string label, Func<Task> action)
            => Timed(label, async () => { await action(); return true; });

        // ---------------------------------------------------------------- csv

        private class ProductDefinition
        {
            public string ProductId = "";
            public decimal DefaultPrice;
            public string Title = "";
            public string Description = "";
        }

        private async Task<List<ProductDefinition>?> LoadDefinitions(bool verbose)
        {
            var path = Config.ProductDefinitionsFilePath;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                Console.WriteLine($"[ERROR] product definitions csv not found: '{path}'");
                Console.WriteLine("        set 'ProductDefinitionsFilePath' in your config.json, or pass --products <path>");
                Console.WriteLine("        run 'export-iaps' first to get a csv of the products you already have.");
                return null;
            }

            var rows = await CommandLinesUtils.LoadCsv(path, path, verbose);

            var definitions = new List<ProductDefinition>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var row in rows)
            {
                var definition = ParseRow(row, seen);
                if (definition is null)
                    continue;

                seen.Add(definition.ProductId);
                definitions.Add(definition);
            }

            Console.WriteLine($"loaded {definitions.Count} product definition(s) from {Path.GetFullPath(path)}");

            if (verbose)
            {
                foreach (var d in definitions)
                    Console.WriteLine($"   {d.ProductId} | {d.DefaultPrice.ToString(CultureInfo.InvariantCulture)} | \"{d.Title}\"");
            }

            return definitions;
        }

        private ProductDefinition? ParseRow(Dictionary<string, string> row, HashSet<string> seen)
        {
            var productId = Get(row, "product_id");

            if (!ProductIdPattern.IsMatch(productId))
            {
                Console.WriteLine($"[ERROR] {productId}: not a valid product id. It must start with a lowercase letter or a digit and contain only a-z, 0-9, '_' and '.'. Skipped.");
                return null;
            }

            if (seen.Contains(productId))
            {
                Console.WriteLine($"[ERROR] {productId}: listed more than once in the csv. Skipped.");
                return null;
            }

            var rawPrice = Get(row, "default_price");

            // the comma replace handles the european decimal separator spreadsheets export
            if (!decimal.TryParse(rawPrice.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out var price))
            {
                Console.WriteLine($"[ERROR] {productId}: can not parse default_price '{rawPrice}'. Skipped.");
                return null;
            }

            if (price <= 0)
            {
                Console.WriteLine($"[ERROR] {productId}: default_price must be greater than zero, got '{rawPrice}'. Skipped.");
                return null;
            }

            var title = Get(row, "title");
            var description = Get(row, "description");

            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(description))
            {
                Console.WriteLine($"[ERROR] {productId}: both a title and a description are required. Skipped.");
                return null;
            }

            if (title.Length > MaxTitleLength)
            {
                Console.WriteLine($"[ERROR] {productId}: the title is {title.Length} characters, the limit is {MaxTitleLength}. Skipped.");
                return null;
            }

            if (description.Length > MaxDescriptionLength)
            {
                Console.WriteLine($"[ERROR] {productId}: the description is {description.Length} characters, the limit is {MaxDescriptionLength}. Skipped.");
                return null;
            }

            return new ProductDefinition
            {
                ProductId = productId,
                DefaultPrice = price,
                Title = title,
                Description = description,
            };
        }

        private static string Get(Dictionary<string, string> row, string column)
            => row.TryGetValue(column, out var value) ? value : "";

        // ------------------------------------------------------------ building

        /// <summary>one conversion per distinct base price, not per product</summary>
        private async Task<ConvertRegionPricesResponse> ConvertAsync(decimal price)
        {
            var currency = string.IsNullOrWhiteSpace(Config.DefaultCurrency) ? "USD" : Config.DefaultCurrency;
            var key = $"{currency}:{price}";

            if (conversions.TryGetValue(key, out var cached))
                return cached;

            var units = (long)Math.Floor(price);
            var nanos = (int)((price - units) * 1_000_000_000);

            var response = await Timed($"the exchange rates for {price.ToString("0.00", CultureInfo.InvariantCulture)} {currency}", () =>
                Service!.Monetization
                    .ConvertRegionPrices(new ConvertRegionPricesRequest
                    {
                        Price = new Money { CurrencyCode = currency, Units = units, Nanos = nanos }
                    }, Package)
                    .ExecuteAsync());

            conversions[key] = response;
            return response;
        }

        private static List<OneTimeProductPurchaseOptionRegionalPricingAndAvailabilityConfig> BuildRegionConfigs(
            ConvertRegionPricesResponse converted,
            OneTimeProductPurchaseOption? templateOption)
        {
            var configs = new List<OneTimeProductPurchaseOptionRegionalPricingAndAvailabilityConfig>();

            // preferred path: mirror an existing product, so a new product lands in exactly the same
            // countries with exactly the same availability string the console already uses
            if (templateOption?.RegionalPricingAndAvailabilityConfigs is { Count: > 0 } templateConfigs)
            {
                foreach (var templateConfig in templateConfigs)
                {
                    var price = converted.ConvertedRegionPrices.TryGetValue(templateConfig.RegionCode, out var regionPrice)
                        ? regionPrice.Price
                        : templateConfig.Price?.CurrencyCode == converted.ConvertedOtherRegionsPrice.UsdPrice.CurrencyCode
                            ? converted.ConvertedOtherRegionsPrice.UsdPrice
                            : converted.ConvertedOtherRegionsPrice.EurPrice;

                    configs.Add(new OneTimeProductPurchaseOptionRegionalPricingAndAvailabilityConfig
                    {
                        RegionCode = templateConfig.RegionCode,
                        Availability = templateConfig.Availability,
                        Price = Copy(price),
                    });
                }

                return configs;
            }

            // fallback: the app has no products at all, so take every region the converter knows about
            foreach (var pair in converted.ConvertedRegionPrices)
            {
                configs.Add(new OneTimeProductPurchaseOptionRegionalPricingAndAvailabilityConfig
                {
                    RegionCode = pair.Key,
                    Availability = Available,
                    Price = Copy(pair.Value.Price),
                });
            }

            return configs;
        }

        private static OneTimeProductPurchaseOptionNewRegionsConfig? BuildNewRegionsConfig(
            ConvertRegionPricesResponse converted,
            OneTimeProductPurchaseOption? templateOption)
        {
            // a template that omits NewRegionsConfig has deliberately opted out of future Play regions,
            // so only build one when the template has one, or when there is no template at all
            if (templateOption is not null && templateOption.NewRegionsConfig is null)
                return null;

            return new OneTimeProductPurchaseOptionNewRegionsConfig
            {
                Availability = templateOption?.NewRegionsConfig?.Availability ?? Available,
                UsdPrice = Copy(converted.ConvertedOtherRegionsPrice.UsdPrice),
                EurPrice = Copy(converted.ConvertedOtherRegionsPrice.EurPrice),
            };
        }

        /// <summary>
        /// the converter hands back one shared Money for the 'other regions' price,
        /// a copy keeps two region configs from ever pointing at the same object
        /// </summary>
        private static Money Copy(Money money)
            => new() { CurrencyCode = money.CurrencyCode, Units = money.Units, Nanos = money.Nanos };

        private OneTimeProduct BuildProduct(
            ProductDefinition definition,
            List<OneTimeProductPurchaseOptionRegionalPricingAndAvailabilityConfig> regionConfigs,
            OneTimeProductPurchaseOptionNewRegionsConfig? newRegionsConfig,
            OneTimeProductPurchaseOption? templateOption)
        {
            // OfferTags, TaxAndComplianceSettings and RestrictedPaymentCountries are deliberately
            // left unset: the tax category is a per product legal decision, better left to the
            // console default than copied from an unrelated product
            return new OneTimeProduct
            {
                PackageName = Package,
                ProductId = definition.ProductId,

                // the csv holds a single language, the rest are added in the Play Console
                Listings =
                [
                    new OneTimeProductListing
                    {
                        LanguageCode = string.IsNullOrWhiteSpace(Config.DefaultLanguageCode) ? "en-US" : Config.DefaultLanguageCode,
                        Title = definition.Title,
                        Description = definition.Description,
                    }
                ],

                PurchaseOptions =
                [
                    new OneTimeProductPurchaseOption
                    {
                        PurchaseOptionId = PurchaseOptionId,

                        BuyOption = new OneTimeProductBuyPurchaseOption
                        {
                            // this tool only manages the single 'backward compatible' option,
                            // that is the one legacy billing flows can see
                            LegacyCompatible = true,
                            MultiQuantityEnabled = templateOption?.BuyOption?.MultiQuantityEnabled ?? false,
                        },

                        RegionalPricingAndAvailabilityConfigs = regionConfigs,
                        NewRegionsConfig = newRegionsConfig,
                    }
                ],

                // RegionsVersion is output only on the resource, the version travels on the request instead
            };
        }

        // ------------------------------------------------------------- output

        private void PrintPreview(
            ProductDefinition definition,
            OneTimeProduct product,
            List<OneTimeProductPurchaseOptionRegionalPricingAndAvailabilityConfig> regionConfigs,
            OneTimeProduct? template,
            bool dryRun)
        {
            var region = string.IsNullOrWhiteSpace(Config.DefaultRegion) ? "US" : Config.DefaultRegion;
            var regionPrice = regionConfigs.FirstOrDefault(c => c.RegionCode == region);

            Console.WriteLine($"   -> {(dryRun ? "[DRY RUN] would create" : "prepared")} {definition.ProductId}");
            Console.WriteLine($"        listing:   {product.Listings[0].LanguageCode} \"{definition.Title}\"");
            Console.WriteLine($"        regions:   {regionConfigs.Count} (template: {template?.ProductId ?? "none, converter fallback"})");
            Console.WriteLine($"        {region} price:  {regionPrice?.Price.FormattedPrice() ?? "n/a"}");
        }

        private static void PrintSummary(List<string> created, List<string> skipped, List<string> failed, bool dryRun)
        {
            Console.WriteLine();
            Console.WriteLine("summary:");

            Console.WriteLine($"   {(dryRun ? "would create" : "created")}: {created.Count}");
            foreach (var item in created)
                Console.WriteLine($"      -> {item}");

            Console.WriteLine($"   skipped: {skipped.Count} (already exist in Google Play Console)");
            foreach (var item in skipped)
                Console.WriteLine($"      -> {item}");

            Console.WriteLine($"   failed:  {failed.Count}");
            foreach (var item in failed)
                Console.WriteLine($"      -> {item}");

            if (created.Count > 0 && !dryRun)
            {
                Console.WriteLine();
                Console.WriteLine("run 'localize' to apply the regional percentage template to the new products.");
            }
        }

        public override string Name => "create-iaps";
        public override string Description => "Creates the One-time products listed in the product definitions csv that do not exist yet. Existing products are skipped and never modified.";

        public override void PrintHelp()
        {
            Console.WriteLine("create-iaps [--products <path-to-product-definitions.csv>] [--language <code>] [--iap <iap-id>] [-n|--dry-run] [-v]");
            Console.WriteLine();
            Console.WriteLine();

            Console.WriteLine("description:");
            CommandLinesUtils.PrintDescription(Description);
            CommandLinesUtils.PrintDescription("If a product with the same product id already exists, it is skipped: it is never re-created and its prices are never touched. Use 'restore' or 'localize' to change the prices of an existing product.");
            CommandLinesUtils.PrintDescription("All new products go to Google in a single batch request, the way Google recommends for catalog creation (allowMissing + LATENCY_TOLERANT). It counts as one call against the hourly quota. The only trade-off is that the new products may take up to 24 hours to reach devices, which for a product nobody has bought yet does not matter.");
            CommandLinesUtils.PrintDescription("Prices come from Google's exchange rates applied to the 'default_price' column, and a whole price is lowered by 0.01 first, so no country ends up with a rounded price. The region list and availability are copied from a product you already have. Run 'localize' afterwards to apply your percentage template.");
            CommandLinesUtils.PrintDescription("Every created product gets one store listing, in the language from the config, built from the 'title' and 'description' columns, and a single backward compatible purchase option. The tax category is not set, the products get the Play Console default.");
            CommandLinesUtils.PrintDescription("The csv separator is detected automatically, ';', ',' and tab are supported. Run 'export-iaps' to get a csv of your existing products to add new rows to.");

            Console.WriteLine();
            Console.WriteLine("options:");

            CommandLinesUtils.PrintOption(
                "--products <path>",
                "Specifies path to csv with product definitions. If not specified, used path from global config json ('ProductDefinitionsFilePath'), which defaults to './product-definitions.csv' next to it."
            );
            CommandLinesUtils.PrintOption(
                "--language <code>",
                "Language of the created store listing. Default is en-US, or the language specified in global config.json."
            );
            CommandLinesUtils.PrintOption(
                "--iap <iap-id>",
                "Create only this single In-App Purchase out of the csv."
            );
            CommandLinesUtils.PrintOption(
                "-n, --dry-run",
                "Print what would be created without sending anything to Google Play Console. The product list and the exchange rates are still requested, so the preview shows the real numbers."
            );
            CommandLinesUtils.PrintOption(
                "-v",
                "Include additional verbose output"
            );
        }
    }
}
