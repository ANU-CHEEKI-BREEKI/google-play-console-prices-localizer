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

                var defaultPrices = await CommandLinesUtils.LoadJson<ProductConfigs>(Args, verbose, "--prices-path", Config.DefaultPricesFilePath);
                if (defaultPrices == null)
                {
                    Console.WriteLine($"Failed to load default prices from {Config.DefaultPricesFilePath}");
                    return;
                }

                Console.WriteLine("receiving IAP list...");

                var listRequest = Service.Monetization.Onetimeproducts.List(Package);
                var listResponse = await listRequest.ExecuteAsync();

                if (verbose)
                {
                    Console.WriteLine("current IAP");
                    listResponse.OneTimeProducts.PrintIapList(printLocalPrices);
                }

                Console.WriteLine("resetting prices to default...");

                foreach (var product in listResponse.OneTimeProducts)
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
                        CurrencyCode = Config.DefaultCurrency,
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
                    listResponse.OneTimeProducts.PrintIapList(false);
                }

                Console.WriteLine("Sending IAP to Google Play Console...");

                await listResponse.OneTimeProducts.SendWithRetryAsync(Service, Package);

                if (verbose)
                {
                    Console.WriteLine("updated IAP");

                    // Fetch the updated list
                    var updatedListRequest = Service!.Monetization.Onetimeproducts.List(Package);
                    var updatedListResponse = await updatedListRequest.ExecuteAsync();
                    updatedListResponse.OneTimeProducts.PrintIapList(printLocalPrices);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        public override bool IsMatches(string[] args) => args.Contains("--restore");
        public override void PrintHelp()
        {
            Console.WriteLine("restore");
            Console.WriteLine("    usage: --restore --prices-path <path-to-default-prices.json> [-v] [-l]");
            Console.WriteLine("    automatically recalculate prices based on default price");
            Console.WriteLine("    -v  print IAP list");
            Console.WriteLine("    -l  print local prices");
        }
    }
}

