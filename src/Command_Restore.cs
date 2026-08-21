using Google.Apis.AndroidPublisher.v3.Data;

namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    public class Command_Restore : CommandBase
    {
        public override async Task ExecuteAsync()
        {
            try
            {
                var verbose = Args.Contains("-v");
                var printLocalPrices = Args.Contains("-l");

                Console.WriteLine("loading default prices...");

                var defaultPrices = await CommandLinesUtils.LoadJson<ProductConfigs>(Config.DefaultPricesFilePath, Config.DefaultPricesFilePath, verbose);
                if (defaultPrices == null)
                {
                    Console.WriteLine($"Failed to load default prices from {Config.DefaultPricesFilePath}");
                    return;
                }

                Console.WriteLine("receiving IAP list...");

                var listRequest = Service.Monetization.Onetimeproducts.List(Package);
                var listResponse = await listRequest.ExecuteAsync();

                var products = listResponse.OneTimeProducts.Filter(IapFilter).ToList();

                if (verbose)
                {
                    Console.WriteLine("current IAP");
                    products.PrintIapList(printLocalPrices, Config.DefaultRegion);
                }

                Console.WriteLine("resetting prices to default...");

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

                            var newConfig = new OneTimeProductPurchaseOptionRegionalPricingAndAvailabilityConfig
                            {
                                Availability = oldConfig.Availability,
                                RegionCode = oldConfig.RegionCode,
                                ETag = oldConfig.ETag,

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
                    products.PrintIapList(false, Config.DefaultRegion);
                }

                Console.WriteLine("Sending IAP to Google Play Console...");

                var ok = await products.SendWithRetryAsync(Service, Package, sensitive: Args.HasFlag("--sensitive"));
                if (!ok)
                    Console.WriteLine("some products were NOT updated, see the errors above.");

                if (verbose)
                {
                    Console.WriteLine("updated IAP");

                    // Fetch the updated list
                    var updatedListRequest = Service!.Monetization.Onetimeproducts.List(Package);
                    var updatedListResponse = await updatedListRequest.ExecuteAsync();
                    updatedListResponse
                        .OneTimeProducts
                        .Filter(IapFilter)
                        .PrintIapList(printLocalPrices, Config.DefaultRegion);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        public override string Name => "restore";
        public override string Description => "Recalculates prices for all regions based on the default currency price provided in your JSON config.";

        public override void PrintHelp()
        {
            Console.WriteLine("restore [--prices <path-to-default-prices.json>] [--iap <id[,id...]>] [--sensitive] [-v] [-l]");
            Console.WriteLine();
            Console.WriteLine();

            Console.WriteLine("description:");
            CommandLinesUtils.PrintDescription(Description);

            Console.WriteLine();
            Console.WriteLine("options:");

            CommandLinesUtils.PrintOption(
                "--prices <path>",
                "Specifies path to json with default prices in default currency. If not specified, used path from global config json."
            );
            CommandLinesUtils.PrintOption(
                CommandLinesUtils.IapOptionName,
                CommandLinesUtils.IapOptionDescription
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

