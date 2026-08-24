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

                var plan = PlanPrices(products, defaultPrices);
                if (plan.Items.Count == 0)
                {
                    Console.WriteLine("nothing to restore.");
                    return;
                }

                // every product with the same base price gets the very same exchange rates,
                // so the rates are fetched once per distinct price instead of once per product
                var rates = await Service.ConvertRegionPricesAsync(
                    Package,
                    Config.DefaultCurrency ?? "USD",
                    plan.Items.Select(p => p.Price),
                    verbose,
                    Parallelism()
                );

                var updated = new List<OneTimeProduct>();

                foreach (var (product, option, price) in plan.Items)
                {
                    if (!rates.TryGetValue(price, out var converted))
                    {
                        Console.WriteLine($"Failed to convert prices for {product.ProductId}, it keeps its current prices.");
                        plan.NoRates.Add(product.ProductId);
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
                var sent = await updated.SendWithRetryAsync(Service, Package, sensitive: Args.HasFlag("--sensitive"), parallel: Parallelism());

                if (verbose)
                {
                    Console.WriteLine("updated IAP");

                    // Fetch the updated list
                    (await Service.Monetization.Onetimeproducts.ListAllAsync(Package))
                        .Filter(IapFilter)
                        .PrintIapList(printLocalPrices, Config.DefaultRegion);
                }

                PrintSummary(Name, plan, rates.Count, sent);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        /// <summary>
        /// what a price run found before it talked to Google: the products it can write, and the
        /// ones it cannot, kept apart so the summary at the end can name them
        /// </summary>
        internal class PricePlan
        {
            /// <summary>ready to write: a purchase option, and a base price already lowered the way Google Play needs it</summary>
            public List<(OneTimeProduct Product, OneTimeProductPurchaseOption Option, decimal Price)> Items { get; } = [];

            /// <summary>no backward compatible purchase option, there is nothing on the product to price</summary>
            public List<string> NoOption { get; } = [];

            /// <summary>not in the product definitions csv, or its default_price cell is empty</summary>
            public List<string> NoPrice { get; } = [];

            /// <summary>Google did not answer with exchange rates for its price, so it keeps the old ones</summary>
            public List<string> NoRates { get; } = [];

            public int Skipped => NoOption.Count + NoPrice.Count + NoRates.Count;
        }

        /// <summary>
        /// the products that have both a purchase option to write to and a base price to write.
        /// </summary>
        internal static PricePlan PlanPrices(IEnumerable<OneTimeProduct> products, ProductConfigs defaultPrices)
        {
            var plan = new PricePlan();

            foreach (var product in products)
            {
                var legacyOption = product.LegacyOption();

                if (legacyOption is null)
                {
                    Console.WriteLine($"Warning: {product.ProductId} has no backward compatible purchase option, nothing to price.");
                    plan.NoOption.Add(product.ProductId);
                    continue;
                }

                if (!defaultPrices.TryGetValue(product.ProductId, out var defaultPrice))
                {
                    Console.WriteLine($"Warning: No default_price for {product.ProductId} in the product definitions csv.");
                    plan.NoPrice.Add(product.ProductId);
                    continue;
                }

                // make from 10$ 9.99%
                // YES google can make it on their side, but NOT not countries there local currency not supported
                // so lets make sure here that  price in EVERY country not rounded
                if (Math.Truncate(defaultPrice) == defaultPrice)
                    defaultPrice -= 0.01m;

                plan.Items.Add((product, legacyOption, defaultPrice));
            }

            return plan;
        }

        /// <summary>
        /// The few lines that say what the run did. Everything above them scrolls past while
        /// Google takes its two minutes per product, so this is the part that has to be readable.
        /// </summary>
        internal static void PrintSummary(string command, PricePlan plan, int distinctPrices, Extensions.SendReport sent)
        {
            Console.WriteLine();
            Console.WriteLine("summary:");
            Console.WriteLine($"   updated:         {sent.Updated}");
            Console.WriteLine($"   failed:          {sent.Failed.Count}");
            Console.WriteLine($"   skipped:         {plan.Skipped}");

            foreach (var (reason, ids) in new[]
            {
                ("no purchase option", plan.NoOption),
                ("no default_price in the csv", plan.NoPrice),
                ("no exchange rates from Google", plan.NoRates),
            })
            {
                foreach (var id in ids)
                    Console.WriteLine($"      -> {id} ({reason})");
            }

            Console.WriteLine($"   exchange rates:  {distinctPrices} request(s) for {plan.Items.Count} product(s)");
            Console.WriteLine($"   time sending:    {sent.Elapsed.Human()}, {sent.Parallel} product(s) at a time");

            if (sent.Failed.Count == 0)
                return;

            Console.WriteLine();
            Console.WriteLine($"[RETRY] {sent.Failed.Count} product(s) failed. Nothing was half-written: a product either got its whole new price schedule or kept the old one. Run again for just them:");
            Console.WriteLine($"        dotnet run -- {command} --iap {string.Join(",", sent.Failed.Distinct())}");
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
