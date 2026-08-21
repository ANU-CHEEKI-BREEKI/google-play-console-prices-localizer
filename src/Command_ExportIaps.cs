using System.Globalization;
using Google.Apis.AndroidPublisher.v3.Data;

namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    public class Command_ExportIaps : CommandBase
    {
        public static readonly List<string> Headers =
        [
            "product_id",
            "default_price",
            "title",
            "description",
        ];

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

                var path = Config.ProductDefinitionsFilePath;
                if (string.IsNullOrWhiteSpace(path) || Directory.Exists(path))
                {
                    Console.WriteLine($"[ERROR] '{path}' is not a file to write the csv into.");
                    Console.WriteLine("        set 'ProductDefinitionsFilePath' in your config.json, or pass --products <path>");
                    return;
                }

                Console.WriteLine("receiving IAP list...");

                var products = (await Service!.Monetization.Onetimeproducts.ListAllAsync(Package))
                    .Filter(IapFilter)
                    .ToList();

                if (products.Count == 0)
                {
                    Console.WriteLine("no One-time products to export.");
                    return;
                }

                var region = string.IsNullOrWhiteSpace(Config.DefaultRegion) ? "US" : Config.DefaultRegion;

                Console.WriteLine($"exporting {products.Count} product(s) into {Path.GetFullPath(path)}...");

                var language = string.IsNullOrWhiteSpace(Config.DefaultLanguageCode) ? "en-US" : Config.DefaultLanguageCode;

                var rows = new List<List<string>>();
                var currencyWarned = false;
                var languageWarned = false;

                foreach (var product in products)
                    rows.Add(BuildRow(product, region, language, verbose, ref currencyWarned, ref languageWarned));

                await CommandLinesUtils.SaveCsv(path, Headers, rows);

                Console.WriteLine();
                Console.WriteLine($"written: {Path.GetFullPath(path)}");
                Console.WriteLine($"{products.Count} product(s), {rows.Count} row(s).");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        /// <summary>
        /// One row per product: this tool manages the single backward compatible purchase option
        /// and one store listing, so there is nothing that would need a second row.
        /// </summary>
        private List<string> BuildRow(OneTimeProduct product, string region, string language, bool verbose, ref bool currencyWarned, ref bool languageWarned)
        {
            var options = (product.PurchaseOptions ?? []).ToList();

            // rent options are out of this tool's scope, it manages backward compatible buy options only
            var option = options.FirstOrDefault(po => po.BuyOption?.LegacyCompatible == true);

            if (option is null && options.Count > 0)
                Console.WriteLine($"Warning: {product.ProductId} has no backward compatible purchase option, its price is not exported.");

            var listings = (product.Listings ?? []).ToList();

            // the csv holds a single language, the one 'create-iaps' writes for new products
            var listing = listings.FirstOrDefault(l => string.Equals(l.LanguageCode, language, StringComparison.OrdinalIgnoreCase))
                          ?? listings.FirstOrDefault();

            if (!languageWarned && listing is not null && !string.Equals(listing.LanguageCode, language, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Warning: no '{language}' store listing, exporting '{listing.LanguageCode}' instead. Set 'DefaultLanguageCode' in your config.json, or pass --language.");
                languageWarned = true;
            }

            if (verbose)
                Console.WriteLine($"   {product.ProductId}: purchase option '{option?.PurchaseOptionId ?? "none"}', listing '{listing?.LanguageCode ?? "none"}'");

            return
            [
                product.ProductId ?? "",
                FormatPrice(product, option, region, ref currencyWarned),
                listing?.Title ?? "",
                listing?.Description ?? "",
            ];
        }

        private string FormatPrice(OneTimeProduct product, OneTimeProductPurchaseOption? option, string region, ref bool currencyWarned)
        {
            if (option is null)
                return "";

            // unlike PrintIapList this must not throw for a product that is not sold in the default region
            var priceConfig = option.RegionalPricingAndAvailabilityConfigs
                ?.FirstOrDefault(c => c.RegionCode == region);

            if (priceConfig?.Price is null)
            {
                Console.WriteLine($"Warning: {product.ProductId} has no price for region {region}, the default_price cell is left empty.");
                return "";
            }

            // 'create-iaps' feeds default_price to the converter tagged as Config.DefaultCurrency,
            // so a region whose currency is something else would be silently reinterpreted
            if (!currencyWarned
                && !string.IsNullOrEmpty(Config.DefaultCurrency)
                && priceConfig.Price.CurrencyCode != Config.DefaultCurrency)
            {
                Console.WriteLine($"Warning: region {region} prices are in {priceConfig.Price.CurrencyCode}, but the configured default currency is {Config.DefaultCurrency}.");
                Console.WriteLine("         'create-iaps' would read the exported numbers as the default currency. Align --region and --currency.");
                currencyWarned = true;
            }

            return priceConfig.Price.ToExactDecimalPrice().ToString("0.####", CultureInfo.InvariantCulture);
        }

        public override string Name => "export-iaps";
        public override string Description => "Exports all One-time products into a product definitions csv, ready to be edited in a spreadsheet and fed back to 'create-iaps'.";

        public override void PrintHelp()
        {
            Console.WriteLine("export-iaps [--products <path-to-product-definitions.csv>] [--language <code>] [--iap <id[,id...]>] [-v]");
            Console.WriteLine();
            Console.WriteLine();

            Console.WriteLine("description:");
            CommandLinesUtils.PrintDescription(Description);
            CommandLinesUtils.PrintDescription($"Columns: {string.Join(", ", Headers)}.");
            CommandLinesUtils.PrintDescription("The price column is the price of the backward compatible purchase option in the default region. Prices for all the other regions are not exported, 'localize' recalculates them from the percentage template.");
            CommandLinesUtils.PrintDescription("The title and the description are the store listing in the configured language, the rest of the languages are not exported. Only the backward compatible purchase option is exported, one row per product.");
            CommandLinesUtils.PrintDescription("An existing csv at the target path is overwritten.");

            Console.WriteLine();
            Console.WriteLine("options:");

            CommandLinesUtils.PrintOption(
                "--products <path>",
                "Specifies path to the csv to write. If not specified, used path from global config json ('ProductDefinitionsFilePath'), which defaults to './product-definitions.csv' next to it."
            );
            CommandLinesUtils.PrintOption(
                "--language <code>",
                "Language of the exported store listing. Default is en-US, or the language specified in global config.json."
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
