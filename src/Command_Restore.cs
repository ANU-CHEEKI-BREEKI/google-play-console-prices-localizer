using Google.Apis.AndroidPublisher.v3;
using Google.Apis.AndroidPublisher.v3.Data;

namespace gps_iap_managing
{
    public class Command_Restore : CommandBase
    {
        public override async Task ExecuteAsync()
        {
            try
            {
                if (!IsMatches(Args))
                    return;

                var printList = Args.Contains("-v");
                var printPrices = Args.Contains("-l");

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
                    Console.WriteLine("updated IAP");
                    result.Inappproducts.PrintIapList(printPrices);
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
            Console.WriteLine("    usage: --restore [-v] [-l]");
            Console.WriteLine("    automatically recalculate prices based on default price");
            Console.WriteLine("    -v  print IAP list");
            Console.WriteLine("    -l  print local prices");
        }
    }
}

