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
                var defaultPrices = await CommandLinesUtils.LoadJson<ProductConfigs>(Args, verbose, "--prices-path", Config.DefaultPricesFilePath, resolvedPath);
                if (defaultPrices == null)
                {
                    Console.WriteLine($"Failed to load default prices from {resolvedPath.ResolvedPath}");
                    return;
                }

                var pricesTemplate = await CommandLinesUtils.LoadJson<LocalizedPricesPercentagesConfigs>(Args, verbose, "--path", "./configs/localized-prices-template.json", resolvedPath);
                if (pricesTemplate is null)
                {
                    Console.WriteLine($"Failed to load localized prices template from {resolvedPath.ResolvedPath}");
                    return;
                }
                var roundPricesArray = await CommandLinesUtils.LoadJson<string[]>(Args, verbose, "--roundPricesPath", "./configs/round-prices-for.json");
                var roundPricesFor = new HashSet<string>(roundPricesArray ?? Array.Empty<string>());

                Console.WriteLine("receiving IAP list...");

                var listRequest = Service.Monetization.Onetimeproducts.List(Package);
                var listResponse = await listRequest.ExecuteAsync();

                if (verbose)
                {
                    Console.WriteLine("current IAP");
                    listResponse.OneTimeProducts.PrintIapList(printLocalPrices);
                }

                Console.WriteLine("calculating localized prices...");

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
                    listResponse.OneTimeProducts.PrintIapList(printLocalPrices);
                }

                Console.WriteLine("Sending IAP to Google Play Console...");

                // Update all products using BatchUpdate
                var updateRequests = listResponse.OneTimeProducts.Select(product => new UpdateOneTimeProductRequest
                {
                    OneTimeProduct = product,
                    UpdateMask = "purchaseOptions",
                    RegionsVersion = product.RegionsVersion
                }).ToList();

                await updateRequests.SendWithRetryAsync(Service, Package);

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

        public override bool IsMatches(string[] args) => args.Contains("--localize");
        public override void PrintHelp()
        {
            Console.WriteLine("localize");
            Console.WriteLine("    usage: --localize  [--path <localized-prices-template.json>] [--roundPricesPath <round-prices-for.json>] [-v]  [-l]");
            Console.WriteLine("    restore prices and then update all local prices to match percentages in config");
            Console.WriteLine("    --path path to json file that contains dictionary AA to x%");
            Console.WriteLine("    where AA country code, and x% price percentage from default price");
            Console.WriteLine("    default path is ./configs/localized-prices-template.json");
            Console.WriteLine("    --roundPricesPath path to json file that tells for which countries prices should be rounded. Because google does not allow prices like 4,99 for some countries.");
            Console.WriteLine("    default path is ./configs/round-prices-for.json");
            Console.WriteLine("    -v  print IAP list");
            Console.WriteLine("    -l  print local prices");
        }
    }
}

