using Google.Apis.AndroidPublisher.v3;
using Google.Apis.AndroidPublisher.v3.Data;
using static Google.Apis.AndroidPublisher.v3.MonetizationResource;

namespace gps_iap_managing
{
    public class Command_List : CommandBase
    {
        public override async Task ExecuteAsync()
        {
            try
            {
                if (!IsMatches(Args))
                    return;

                var printPrices = Args.Contains("-l");

                Console.WriteLine("receiving IAP list...");

                var listRequest = new InappproductsResource.ListRequest(Service, Package);
                var listResponse = await listRequest.ExecuteAsync();

                Console.WriteLine("current IAP");
                listResponse.Inappproduct.PrintIapList(printPrices);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        public override bool IsMatches(string[] args) => args.Contains("--list");
        public override void PrintHelp()
        {
            Console.WriteLine("list");
            Console.WriteLine("    usage: --list  [-l]");
            Console.WriteLine("    list all IAP in project (NOT subscriptions)");
            Console.WriteLine("    -l  print local prices");
        }
    }
}

