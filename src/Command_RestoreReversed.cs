using Google.Apis.AndroidPublisher.v3;
using Google.Apis.AndroidPublisher.v3.Data;
using Newtonsoft.Json;

namespace gps_iap_managing
{
    public class Command_RestoreReversed : CommandBase
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

                var defaultPrices = JsonConvert.DeserializeObject<Dictionary<string, ProductConfig>>(
                   await File.ReadAllTextAsync(Config.DefaultPricesFilePath)
                );

                Console.WriteLine("receiving IAP list...");

                var listRequest = new InappproductsResource.ListRequest(Service, Package);
                var listResponse = await listRequest.ExecuteAsync();

                if (printList)
                {
                    Console.WriteLine("current IAP");
                    listResponse.Inappproduct.PrintIapList(printPrices);
                }

                Console.WriteLine("resetting local prices...");

                foreach (var product in listResponse.Inappproduct)
                {
                    if (defaultPrices.TryGetValue(product.Sku, out var productConfig))
                        product.DefaultPrice.PriceMicros = $"{Math.Round(productConfig.DefaultPrice * Mil)}";
                    product.Prices = new Dictionary<string, Price>();
                }

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
                    Console.WriteLine("updated IAP");
                    result.Inappproducts.PrintIapList(printPrices);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        public override bool IsMatches(string[] args) => args.Contains("--restore-rev");
        public override void PrintHelp()
        {
            Console.WriteLine("restore");
            Console.WriteLine("    usage: --restore [-v] [-l]");
            Console.WriteLine("    sets default price from config DefaultPricesFilePath");
            Console.WriteLine("    and then automatically recalculate prices based on default price");
            Console.WriteLine("    -v  print IAP list");
            Console.WriteLine("    -l  print local prices");
        }
    }
}

