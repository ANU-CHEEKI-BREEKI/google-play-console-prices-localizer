namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    public class Command_List : CommandBase
    {
        public override async Task ExecuteAsync()
        {
            try
            {
                var printPrices = Args.HasFlag("-l");
                var verbose = Args.HasFlag("-v");

                if (verbose)
                    Console.WriteLine("receiving IAP list...");

                var listRequest = Service.Monetization.Onetimeproducts.List(Package);
                var listResponse = await listRequest.ExecuteAsync();

                if (verbose)
                    Console.WriteLine("current IAP");

                listResponse.OneTimeProducts
                    .Filter(Config.Iap)
                    .PrintIapList(printPrices, defaultRegion: Config.DefaultRegion);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        public override string Name => "list";
        public override string Description => "Lists all One-time products in the project, and their prices for specified region.";

        public override void PrintHelp()
        {
            Console.WriteLine("list [-l] [-v]");
            Console.WriteLine();
            Console.WriteLine();

            Console.WriteLine("description:");
            CommandLinesUtils.PrintDescription(Description);

            Console.WriteLine();
            Console.WriteLine("options:");
            CommandLinesUtils.PrintOption(
                "-l",
                "Include local pricing for all regions"
            );
            CommandLinesUtils.PrintOption(
                "-v",
                "Include detailed verbose output"
            );
        }
    }
}

