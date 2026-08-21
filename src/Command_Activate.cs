using Google.Apis.AndroidPublisher.v3.Data;

namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    public class Command_Activate : CommandBase
    {
        public override async Task ExecuteAsync()
        {
            try
            {
                var verbose = Args.HasFlag("-v");
                var dryRun = Args.HasFlag("-n") || Args.HasFlag("--dry-run");

                Console.WriteLine("receiving IAP list...");

                var products = (await Service!.Monetization.Onetimeproducts.ListAllAsync(Package))
                    .Filter(IapFilter)
                    .ToList();

                var active = new List<string>();
                var noOption = new List<string>();
                var pending = new List<(string ProductId, string PurchaseOptionId)>();

                foreach (var product in products)
                {
                    var option = product.LegacyOption();

                    if (option is null)
                    {
                        noOption.Add(product.ProductId);
                        continue;
                    }

                    if (option.State == Extensions.PurchaseOptionActive)
                    {
                        active.Add(product.ProductId);
                        continue;
                    }

                    if (verbose || dryRun)
                        Console.WriteLine($"   -> {(dryRun ? "[DRY RUN] would activate" : "to activate")} {product.ProductId} ({option.PurchaseOptionId}, state: {option.State ?? "unknown"})");

                    pending.Add((product.ProductId, option.PurchaseOptionId));
                }

                foreach (var id in noOption)
                    Console.WriteLine($"Warning: {id} has no backward compatible purchase option, nothing to activate.");

                var activated = new List<string>();
                var failed = new List<string>();

                if (pending.Count > 0 && !dryRun)
                {
                    if (await Service.ActivateAsync(Package, pending))
                        activated.AddRange(pending.Select(p => p.ProductId));
                    else
                        failed.AddRange(pending.Select(p => p.ProductId));
                }
                else if (dryRun)
                {
                    activated.AddRange(pending.Select(p => p.ProductId));
                }

                Console.WriteLine();
                Console.WriteLine("summary:");

                Console.WriteLine($"   {(dryRun ? "would activate" : "activated")}: {activated.Count}");
                foreach (var id in activated)
                    Console.WriteLine($"      -> {id}");

                Console.WriteLine($"   already active: {active.Count}");
                if (verbose)
                {
                    foreach (var id in active)
                        Console.WriteLine($"      -> {id}");
                }

                Console.WriteLine($"   failed: {failed.Count}");
                foreach (var id in failed)
                    Console.WriteLine($"      -> {id}");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        public override string Name => "activate";
        public override string Description => "Activates every One-time product that is not active yet, so it can actually be bought. Products that are already active are left alone.";

        public override void PrintHelp()
        {
            Console.WriteLine("activate [--iap <id[,id...]>] [-n|--dry-run] [-v]");
            Console.WriteLine();
            Console.WriteLine();

            Console.WriteLine("description:");
            CommandLinesUtils.PrintDescription(Description);
            CommandLinesUtils.PrintDescription("A freshly created product is a draft, nobody can buy it until its purchase option is activated. 'create-iaps' does this on its own, this command is for products created elsewhere, or for a create run whose activation failed.");
            CommandLinesUtils.PrintDescription("One request per product, several at a time, with LATENCY_TOLERANT.");

            Console.WriteLine();
            Console.WriteLine("options:");

            CommandLinesUtils.PrintOption(
                CommandLinesUtils.IapOptionName,
                CommandLinesUtils.IapOptionDescription
            );
            CommandLinesUtils.PrintOption(
                "-n, --dry-run",
                "Print what would be activated without sending anything to Google Play Console."
            );
            CommandLinesUtils.PrintOption(
                "-v",
                "Include additional verbose output, including the list of products that are already active."
            );

            CommandLinesUtils.PrintCommonOptions();
        }
    }
}
