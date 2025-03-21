using Google.Apis.AndroidPublisher.v3;
using Google.Apis.AndroidPublisher.v3.Data;

namespace gps_iap_managing
{
    public class Command_LocalizePrices : CommandBase
    {
        private int Mil = 1_000_000;

        public override async Task ExecuteAsync()
        {
            try
            {
                if (!IsMatches(Args))
                    return;

                var printList = Args.Contains("-v");
                var printPrices = Args.Contains("-l");

                Console.WriteLine("loading prices template...");

                var pricesTemplate = await CommandLinesUtils.LoadJson<Dictionary<string, decimal>>(Args, printList, "--path=", "./configs/localized-prices-template.json");
                var roundPricesArray = await CommandLinesUtils.LoadJson<string[]>(Args, printList, "--roundPricesPath=", "./configs/round-prices-for.json");
                var roundPricesFor = new HashSet<string>(roundPricesArray ?? Array.Empty<string>());

                Console.WriteLine("receiving IAP list...");

                var listRequest = new InappproductsResource.ListRequest(Service, Package);
                var listResponse = await listRequest.ExecuteAsync();

                Console.WriteLine("resetting local prices...");

                foreach (var product in listResponse.Inappproduct)
                    product.Prices = new Dictionary<string, Price>();

                var updateProductsRequests = listResponse.Inappproduct.Select(product => new InappproductsUpdateRequest()
                {
                    PackageName = Package,
                    Sku = product.Sku,
                    AllowMissing = false,
                    AutoConvertMissingPrices = true,
                    Inappproduct = product
                }).ToArray();
                var updateRequest = new InappproductsResource.BatchUpdateRequest(
                    Service,
                    new InappproductsBatchUpdateRequest() { Requests = updateProductsRequests },
                    Package
                );

                Console.WriteLine("sending IAP to Google Play Console...");

                var result = await updateRequest.ExecuteAsync();

                if (printList)
                {
                    Console.WriteLine("current restored IAP");
                    result.Inappproducts.PrintIapList(printPrices);
                }

                Console.WriteLine("calculating local prices...");

                // -0.01 to make prices looks smaller
                // like 4,99 USD instead of 5 USD looks like 4 USD to the customer
                // just generic marketing trick

                foreach (var product in result.Inappproducts)
                {
                    foreach (var price in product.Prices)
                    {
                        if (!pricesTemplate!.TryGetValue(price.Key, out var pricePercentage))
                            continue;

                        var microPrice = decimal.Parse(price.Value.PriceMicros);
                        var regularPrice = microPrice / Mil;
                        var localizedPrice = regularPrice * pricePercentage;

                        var roundedMarketingPrice = Math.Round(localizedPrice);

                        // for some countries prices are invalid.
                        // so exclude this countries from our fancy marketing trick
                        if (!roundPricesFor.Contains(price.Key))
                        {
                            var roundedPrice = Math.Round(localizedPrice);
                            if (roundedPrice < 1)
                                roundedPrice = Math.Round(localizedPrice, 2);

                            var marketingPrice = roundedPrice - 0.01m;
                            roundedMarketingPrice = Math.Round(marketingPrice, 2);

                            if (roundedMarketingPrice < 0)
                                roundedMarketingPrice = Math.Round(localizedPrice, 2);
                        }
                        else if (roundedMarketingPrice < 0.5m)
                        {
                            roundedMarketingPrice = 1;
                        }

                        var localizedMicroPrice = roundedMarketingPrice * Mil;
                        var noPeriodPrice = Math.Round(localizedMicroPrice);

                        price.Value.PriceMicros = $"{noPeriodPrice}";
                    }
                }


                if (printList)
                {
                    Console.WriteLine("calculated IAP prices:");
                    result.Inappproducts.PrintIapList(printPrices);
                }

                updateProductsRequests = result.Inappproducts.Select(product => new InappproductsUpdateRequest()
                {
                    PackageName = Package,
                    Sku = product.Sku,
                    AllowMissing = false,
                    AutoConvertMissingPrices = true,
                    Inappproduct = product
                }).ToArray();
                updateRequest = new InappproductsResource.BatchUpdateRequest(
                    Service,
                    new InappproductsBatchUpdateRequest() { Requests = updateProductsRequests },
                    Package
                );

                Console.WriteLine("sending IAP to Google Play Console...");

                result = await updateRequest.ExecuteAsync();

                if (printList)
                {
                    Console.WriteLine("updated IAP:");
                    result.Inappproducts.PrintIapList(printPrices);
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
            Console.WriteLine("    usage: --localize  [--path={localized-prices-template.json}] [--roundPricesPath={round-prices-for.json}] [-v]  [-l]");
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

