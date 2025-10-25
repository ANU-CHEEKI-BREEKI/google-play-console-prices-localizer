namespace gps_iap_managing
{
    public class CommandsCollection : List<CommandBase>
    {
        public bool TryPrintHelp(string[] args)
        {
            if (args.Length > 0)
                return false;

            Console.WriteLine("usage:");
            Console.WriteLine("    {path_to_credentials.json}  {com.package.name} --command {command-params}");
            Console.WriteLine("alternate usage:");
            Console.WriteLine("    --command {command-params} [--config {path-to-config-or-place-it-at../config.json}]");
            Console.WriteLine();
            Console.WriteLine("commands:");
            Console.WriteLine();

            foreach (var item in this)
            {
                item.PrintHelp();
                Console.WriteLine();
            }

            return true;
        }
    }
}

