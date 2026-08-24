using Google.Apis.AndroidPublisher.v3.Data;

namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    public class Command_Restore : CommandBase
    {
        public override async Task ExecuteAsync()
        {
            try
            {
                var verbose = Args.HasFlag("-v");
                var printLocalPrices = Args.HasFlag("-l");

                Console.WriteLine("loading default prices...");

                var defaultPrices = await CommandLinesUtils.LoadBasePrices(Config.ProductDefinitionsFilePath, verbose);
                if (defaultPrices is null)
                    return;

                Console.WriteLine("receiving IAP list...");

                var products = (await Service!.Monetization.Onetimeproducts.ListAllAsync(Package))
                    .Filter(IapFilter)
                    .ToList();

                if (verbose)
                {
                    Console.WriteLine("current IAP");
                    products.PrintIapList(printLocalPrices, Config.DefaultRegion);
                }

                Console.WriteLine("resetting prices to default...");

                var planned = PlanPrices(products, defaultPrices);
                if (planned.Count == 0)
                {
                    Console.WriteLine("nothing to restore.");
                    return;
                }

                // every product with the same base price gets the very same exchange rates,
                // so the rates are fetched once per distinct price instead of once per product
                var rates = await Service.ConvertRegionPricesAsync(
                    Package,
                    Config.DefaultCurrency ?? "USD",
                    planned.Select(p => p.Price),
                    verbose,
                    Parallelism()
                );

                var updated = new List<OneTimeProduct>();

                foreach (var (product, option, price) in planned)
                {
                    if (!rates.TryGetValue(price, out var converted))
                    {
                        Console.WriteLine($"Failed to convert prices for {product.ProductId}, it keeps its current prices.");
                        continue;
                    }

                    option.RegionalPricingAndAvailabilityConfigs =
                    [
                        .. option.RegionalPricingAndAvailabilityConfigs.Select(oldConfig =>
                            new OneTimeProductPurchaseOptionRegionalPricingAndAvailabilityConfig
                            {
                                Availability = oldConfig.Availability,
                                RegionCode = oldConfig.RegionCode,
                                ETag = oldConfig.ETag,

                                Price = converted.PriceFor(oldConfig),
                            })
                    ];

                    updated.Add(product);
                }

                if (verbose)
                {
                    Console.WriteLine("Local updated prices:");
                    updated.PrintIapList(false, Config.DefaultRegion);
                }

                Console.WriteLine("Sending IAP to Google Play Console...");

                // only the products this run actually recalculated: patching an untouched
                // product would still cost its two minutes of Google's time
                var ok = await updated.SendWithRetryAsync(Service, Package, sensitive: Args.HasFlag("--sensitive"), parallel: Parallelism());
                if (!ok)
                    Console.WriteLine("some products were NOT updated, see the errors above.");

                if (verbose)
                {
                    Console.WriteLine("updated IAP");

                    // Fetch the updated list
                    (await Service.Monetization.Onetimeproducts.ListAllAsync(Package))
                        .Filter(IapFilter)
                        .PrintIapList(printLocalPrices, Config.DefaultRegion);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        /// <summary>
        /// the products that have both a purchase option to write to and a base price to write,
        /// with the price already lowered the way Google Play needs it.
        /// </summary>
        internal static List<(OneTimeProduct Product, OneTimeProductPurchaseOption Option, decimal Price)> PlanPrices(
            IEnumerable<OneTimeProduct> products,
            ProductConfigs defaultPrices
        )
        {
            var planned = new List<(OneTimeProduct, OneTimeProductPurchaseOption, decimal)>();

            foreach (var product in products)
            {
                var legacyOption = product.LegacyOption();

                if (legacyOption is null)
                    continue;

                if (!defaultPrices.TryGetValue(product.ProductId, out var defaultPrice))
                {
                    Console.WriteLine($"Warning: No default_price for {product.ProductId} in the product definitions csv.");
                    continue;
                }

                // make from 10$ 9.99%
                // YES google can make it on their side, but NOT not countries there local currency not supported
                // so lets make sure here that  price in EVERY country not rounded
                if (Math.Truncate(defaultPrice) == defaultPrice)
                    defaultPrice -= 0.01m;

                planned.Add((product, legacyOption, defaultPrice));
            }

            return planned;
        }

        public override string Name => "restore";
        public override string Description => "Recalculates prices for all regions based on the 'default_price' column of the product definitions csv.";

        public override void PrintHelp()
        {
            Console.WriteLine("restore [--products <path-to-product-definitions.csv>] [--iap <id[,id...]>] [--parallel <n>] [--sensitive] [-v] [-l]");
            Console.WriteLine();
            Console.WriteLine();

            Console.WriteLine("description:");
            CommandLinesUtils.PrintDescription(Description);
            CommandLinesUtils.PrintDescription("Base prices come from the same csv 'export-iaps' writes and 'create-iaps' reads. Run 'export-iaps' once if you do not have it yet.");
            CommandLinesUtils.PrintDescription("Google's exchange rates are asked once per distinct price, not once per product, and only the products that actually got new prices are sent.");

            Console.WriteLine();
            Console.WriteLine("options:");

            CommandLinesUtils.PrintOption(
                "--products <path>",
                "Specifies path to the product definitions csv the base prices are read from. If not specified, used path from global config json ('ProductDefinitionsFilePath'), which defaults to './product-definitions.csv' next to it."
            );
            CommandLinesUtils.PrintOption(
                CommandLinesUtils.IapOptionName,
                CommandLinesUtils.IapOptionDescription
            );
            CommandLinesUtils.PrintOption(
                "--parallel <n>",
                "How many products go to Google at once, 1 to 16. Default is 8. Google needs about two minutes per product, so this is what decides how long the run takes."
            );
            CommandLinesUtils.PrintOption(
                "--sensitive",
                "Send the update as latency sensitive, so it reaches devices within minutes instead of up to 24 hours. Much slower on Google's side, a full region list may not finish within the timeout."
            );
            CommandLinesUtils.PrintOption(
                "-v",
                "Include additional verbose output"
            );
            CommandLinesUtils.PrintOption(
                "-l",
                "Include local pricing for all regions"
            );

            CommandLinesUtils.PrintCommonOptions();
        }
    }
}
