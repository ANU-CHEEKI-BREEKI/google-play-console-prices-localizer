using Google.Apis.AndroidPublisher.v3.Data;

namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    public class Command_LocalizePrices : CommandBase
    {
        public override async Task ExecuteAsync()
        {
            try
            {
                var verbose = Args.Contains("-v");
                var printLocalPrices = Args.Contains("-l");

                Console.WriteLine("loading default prices...");

                var resolvedPath = new CommandLinesUtils.ResolvedPathGetter();
                var defaultPrices = await CommandLinesUtils.LoadJson<ProductConfigs>(Config.DefaultPricesFilePath, Config.DefaultPricesFilePath, verbose, resolvedPath);
                if (defaultPrices == null)
                {
                    Console.WriteLine($"Failed to load default prices from {resolvedPath.ResolvedPath}");
                    return;
                }

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

                var roundPricesFor = new HashSet<string>(roundPricesArray ?? Array.Empty<string>());

                Console.WriteLine("receiving IAP list...");

                var listRequest = Service.Monetization.Onetimeproducts.List(Package);
                var listResponse = await listRequest.ExecuteAsync();

                var products = listResponse.OneTimeProducts.Filter(Config.Iap).ToList();

                if (verbose)
                {
                    Console.WriteLine("current IAP");
                    products.PrintIapList(printLocalPrices, Config.DefaultRegion);
                }

                Console.WriteLine("calculating localized prices...");

                foreach (var product in products)
                {
                    var legacyOption = product.PurchaseOptions
                        ?.FirstOrDefault(po => po.BuyOption?.LegacyCompatible == true);

                    if (legacyOption is null)
                        continue;

                    if (!defaultPrices.TryGetValue(product.ProductId, out var defaultPrice))
                    {
                        Console.WriteLine($"Warning: No default price for {product.ProductId}");
                        continue;
                    }

                    // make from 10$ 9.99%
                    // YES google can make it on their side, but NOT not countries there local currency not supported
                    // so lets make sure here that  price in EVERY country not rounded
                    if (Math.Truncate(defaultPrice) == defaultPrice)
                        defaultPrice -= 0.01m;

                    var units = (long)Math.Floor(defaultPrice);
                    var nanos = (int)((defaultPrice - units) * 1_000_000_000);

                    var baseMoney = new Money
                    {
                        CurrencyCode = Config.DefaultCurrency ?? "USD",
                        Units = units,
                        Nanos = nanos
                    };

                    try
                    {
                        if (verbose)
                            Console.WriteLine($"Calculating exchange rates for {product.ProductId}...");

                        var convertRequest = new ConvertRegionPricesRequest
                        {
                            Price = baseMoney,
                        };

                        var convertResponse = await Service.Monetization
                            .ConvertRegionPrices(convertRequest, Package)
                            .ExecuteAsync();

                        var newConfigs = new List<OneTimeProductPurchaseOptionRegionalPricingAndAvailabilityConfig>();
                        foreach (var oldConfig in legacyOption.RegionalPricingAndAvailabilityConfigs)
                        {
                            var newPrice = convertResponse.ConvertedRegionPrices.TryGetValue(oldConfig.RegionCode, out var price)
                                ? price.Price
                                : oldConfig.Price.CurrencyCode == convertResponse.ConvertedOtherRegionsPrice.UsdPrice.CurrencyCode
                                    ? convertResponse.ConvertedOtherRegionsPrice.UsdPrice
                                    : convertResponse.ConvertedOtherRegionsPrice.EurPrice;

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

                            var newConfig = new OneTimeProductPurchaseOptionRegionalPricingAndAvailabilityConfig
                            {
                                Availability = oldConfig.Availability,
                                RegionCode = oldConfig.RegionCode,
                                Price = newPrice,
                            };
                            newConfigs.Add(newConfig);
                        }

                        // Apply the full list of regions
                        legacyOption.RegionalPricingAndAvailabilityConfigs = newConfigs;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Failed to convert prices for {product.ProductId}: {ex.Message}");
                    }
                }

                if (verbose)
                {
                    Console.WriteLine("Local updated prices:");
                    products.PrintIapList(printLocalPrices, Config.DefaultRegion);
                }

                Console.WriteLine("Sending IAP to Google Play Console...");

                await products.SendWithRetryAsync(Service, Package);

                if (verbose)
                {
                    Console.WriteLine("updated IAP");

                    // Fetch the updated list
                    var updatedListRequest = Service!.Monetization.Onetimeproducts.List(Package);
                    var updatedListResponse = await updatedListRequest.ExecuteAsync();
                    updatedListResponse.OneTimeProducts
                        .Filter(Config.Iap)
                        .PrintIapList(printLocalPrices, Config.DefaultRegion);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        public override bool IsMatches(string[] args)
            => args.Length > 0 && args[0].Equals("localize", StringComparison.OrdinalIgnoreCase);

        public override string Name => "localize";
        public override string Description => "Recalculates prices for all regions based on the default currency price provided in your JSON config and localized prices template.";

        public override void PrintHelp()
        {
            Console.WriteLine("localize [--prices <path>] [--localized-template <path>] [--round-prices <path>] [-v] [-l]");
            Console.WriteLine();
            Console.WriteLine();

            Console.WriteLine(Name);
            Console.WriteLine("    usage: localize [--prices <path-to-default-prices.json>] [--localized-template <path-to-localized-template.json>] [--round-prices <path-to-round-prices.json>] [-v] [-l]");

            Console.WriteLine("    description:");
            CommandLinesUtils.PrintDescription(Description);

            Console.WriteLine("options:");

            CommandLinesUtils.PrintOption(
                "--localized-template <path>",
                "Specifies path to json with percentages for each region that needs to be localized. Default path is: ./configs/localized-prices-template.json"
            );
            CommandLinesUtils.PrintOption(
                "--round-prices <path>",
                "Specifies path to json with list of regions for which prices should be rounded. Required since Google Play enforces some regions prices to be rounded. Default path is: ./configs/round-prices-for.json"
            );

            CommandLinesUtils.PrintOption(
                "-v",
                "Include additional verbose output"
            );
            CommandLinesUtils.PrintOption(
                "-l",
                "Include local pricing for all regions"
            );

        }
    }
}

