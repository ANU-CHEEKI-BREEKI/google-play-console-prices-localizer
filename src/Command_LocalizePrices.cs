using Google.Apis.AndroidPublisher.v3.Data;

namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    public class Command_LocalizePrices : CommandBase
    {
        public override async Task ExecuteAsync()
        {
            try
            {
                var verbose = Args.HasFlag("-v");
                var printLocalPrices = Args.HasFlag("-l");

                Console.WriteLine("loading default prices...");

                // 'restore' is not a pre-step: the base price comes straight from the csv, so the
                // localized write at the end is the only write this command needs
                var defaultPrices = await CommandLinesUtils.LoadBasePrices(Config.ProductDefinitionsFilePath, verbose);
                if (defaultPrices is null)
                    return;

                var resolvedPath = new CommandLinesUtils.ResolvedPathGetter();

                var pricesTemplate = await CommandLinesUtils.LoadJson<LocalizedPricesPercentagesConfigs>(Config.LocalizedPricesTemplateFilePath, "./configs/localized-prices-template.json", verbose, resolvedPath);
                if (pricesTemplate is null)
                {
                    Console.WriteLine($"Failed to load localized prices template from {resolvedPath.ResolvedPath}");
                    return;
                }
                var roundPricesArray = await CommandLinesUtils.LoadJson<string[]>(Config.RoundPricesForFilePath, "./configs/round-prices-for.json", verbose, resolvedPath);
                if (roundPricesArray is null)
                {
                    Console.WriteLine($"Failed to load round prices list from {resolvedPath.ResolvedPath}");
                    return;
                }

                var roundPricesFor = new HashSet<string>(roundPricesArray);

                Console.WriteLine("receiving IAP list...");

                var products = (await Service!.Monetization.Onetimeproducts.ListAllAsync(Package))
                    .Filter(IapFilter)
                    .ToList();

                if (verbose)
                {
                    Console.WriteLine("current IAP");
                    products.PrintIapList(printLocalPrices, Config.DefaultRegion);
                }

                Console.WriteLine("calculating localized prices...");

                var plan = Command_Restore.PlanPrices(products, defaultPrices);
                if (plan.Items.Count == 0)
                {
                    Console.WriteLine("nothing to localize.");
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

                    var newConfigs = new List<OneTimeProductPurchaseOptionRegionalPricingAndAvailabilityConfig>();

                    foreach (var oldConfig in option.RegionalPricingAndAvailabilityConfigs)
                    {
                        // a copy: the same converted price object is shared by every product
                        // priced the same, and the percentage below is applied by writing into it
                        var newPrice = converted.PriceFor(oldConfig);

                        if (!pricesTemplate.TryGetValue(oldConfig.RegionCode, out var pricePercentage))
                        {
                            if (verbose)
                                Console.WriteLine($"Warning: No price percentage for region {oldConfig.RegionCode}. Keeping original price.");
                        }
                        else
                        {
                            var decimalPrice = newPrice.ToDecimalPrice();
                            decimalPrice *= pricePercentage;

                            decimalPrice = Math.Ceiling(decimalPrice);

                            if (!roundPricesFor.Contains(oldConfig.RegionCode))
                                decimalPrice -= 0.01m;

                            var localUnits = (long)Math.Floor(decimalPrice);
                            var localNanos = (int)((decimalPrice - localUnits) * 1_000_000_000);

                            newPrice.Units = localUnits;
                            newPrice.Nanos = localNanos;
                        }

                        newConfigs.Add(new OneTimeProductPurchaseOptionRegionalPricingAndAvailabilityConfig
                        {
                            Availability = oldConfig.Availability,
                            RegionCode = oldConfig.RegionCode,
                            Price = newPrice,
                        });
                    }

                    // Apply the full list of regions
                    option.RegionalPricingAndAvailabilityConfigs = newConfigs;
                    updated.Add(product);
                }

                if (verbose)
                {
                    Console.WriteLine("Local updated prices:");
                    updated.PrintIapList(printLocalPrices, Config.DefaultRegion);
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

                Command_Restore.PrintSummary(Name, plan, rates.Count, sent);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        public override string Name => "localize";
        public override string Description => "Recalculates prices for all regions based on the 'default_price' column of the product definitions csv and the localized prices template.";

        public override void PrintHelp()
        {
            Console.WriteLine("localize [--products <path-to-product-definitions.csv>] [--localized-template <path-to-localized-template.json>]");
            Console.WriteLine("         [--round-prices <path-to-round-prices.json>] [--iap <id[,id...]>] [--parallel <n>] [--sensitive] [-v] [-l]");
            Console.WriteLine();
            Console.WriteLine();

            Console.WriteLine("description:");
            CommandLinesUtils.PrintDescription(Description);
            CommandLinesUtils.PrintDescription("Base prices come from the same csv 'export-iaps' writes and 'create-iaps' reads. Run 'export-iaps' once if you do not have it yet.");
            CommandLinesUtils.PrintDescription("There is no 'restore' pre-step: the base price is read from the csv, not from the store, so the localized prices are the only thing this command writes.");
            CommandLinesUtils.PrintDescription("Google's exchange rates are asked once per distinct price, not once per product, and only the products that actually got new prices are sent.");

            Console.WriteLine();
            Console.WriteLine("options:");

            CommandLinesUtils.PrintOption(
                "--products <path>",
                "Specifies path to the product definitions csv the base prices are read from. If not specified, used path from global config json ('ProductDefinitionsFilePath'), which defaults to './product-definitions.csv' next to it."
            );
            CommandLinesUtils.PrintOption(
                "--localized-template <path>",
                "Specifies path to json with percentages for each region that needs to be localized. Default path is: ./configs/localized-prices-template.json"
            );
            CommandLinesUtils.PrintOption(
                "--round-prices <path>",
                "Specifies path to json with list of regions for which prices should be rounded. Required since Google Play enforces some regions prices to be rounded. Default path is: ./configs/round-prices-for.json"
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
