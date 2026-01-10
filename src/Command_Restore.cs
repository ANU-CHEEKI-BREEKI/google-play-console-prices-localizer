using Google.Apis.AndroidPublisher.v3;
using Google.Apis.AndroidPublisher.v3.Data;
using Newtonsoft.Json;

namespace gps_iap_managing
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
                    var legacyOption = product.PurchaseOptions?.FirstOrDefault(po => po.BuyOption?.LegacyCompatible == true);

                    if (legacyOption == null)
                    {
                        Console.WriteLine($"Warning: No legacy compatible BuyOption found for product {product.ProductId}");
                        continue;
                    }

                    product.PurchaseOptions = [legacyOption];

                    if (!defaultPrices.TryGetValue(product.ProductId, out var defaultPrice))
                    {
                        Console.WriteLine($"Warning: No default price configured for product {product.ProductId}");
                        continue;
                    }

                    if (legacyOption.RegionalPricingAndAvailabilityConfigs != null)
                    {
                        var config = legacyOption.RegionalPricingAndAvailabilityConfigs.FirstOrDefault(c => c.RegionCode == Config.DefaultCurrencyRegion);
                        if (config is null)
                        {
                            Console.WriteLine($"Warning: No purchase option with region {Config.DefaultCurrencyRegion} found for product {product.ProductId}");
                            continue;
                        }

                        var units = (long)Math.Floor(defaultPrice);
                        var nanos = (int)((defaultPrice - units) * 1_000_000_000);

                        config.Price.Units = units;
                        config.Price.Nanos = nanos;
                        
                        legacyOption.RegionalPricingAndAvailabilityConfigs = [config];
                    }
                }

                if (verbose)
                {
                    Console.WriteLine("Local updated prices:");
                    listResponse.OneTimeProducts.PrintIapList(false);
                }

                Console.WriteLine("Sending IAP to Google Play Console...");

                // Update all products using BatchUpdate
                var updateRequests = listResponse.OneTimeProducts.Select(product => new UpdateOneTimeProductRequest
                {
                    OneTimeProduct = product,
                    UpdateMask = "purchaseOptions",
                    RegionsVersion = new RegionsVersion { Version = Config.DefaultCurrencyRegion }
                }).ToList();

                var batchUpdateRequest = new BatchUpdateOneTimeProductsRequest
                {
                    Requests = updateRequests
                };

                var batchRequest = Service!.Monetization.Onetimeproducts.BatchUpdate(batchUpdateRequest, Package);
                await batchRequest.ExecuteAsync();

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

