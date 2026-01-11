namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    public class CommandsCollection : List<CommandBase>
    {
        public bool TryPrintHelp(string[] args)
        {
            if (args.Length > 0)
                return false;

            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("------------------------------------------------------------------------------------------------");
            Console.WriteLine("this is a tool for managing Google Play In-App Purchases");
            Console.WriteLine("mainly designed to fast and easily localize In-App Purchase prices for all available countries");
            Console.WriteLine("------------------------------------------------------------------------------------------------");
            Console.WriteLine();
            Console.WriteLine("usage:");
            Console.WriteLine();
            Console.WriteLine("<command> [command-options] [--config <path_to_config.json>] [config-options]");
            Console.WriteLine();

            Console.WriteLine("options:");

            CommandLinesUtils.PrintOption(
                "[command-options]",
                "Command-specific options"
            );

            CommandLinesUtils.PrintOption(
                "--config <path>",
                "Explicitly specify the path to your global config JSON file. If not provided, the tool will try to find it in '../config.json' path. You can also provide only the path to folder that contains the 'config.json' file."
            );

            CommandLinesUtils.PrintOption(
                "[config-options]",
                "Explicitly specify configuration options. If not provided, the tool will use global config."
            );

            Console.WriteLine("config-options:");

            CommandLinesUtils.PrintOption(
                "--package <package>",
                "Explicitly specify your app package name."
            );
            CommandLinesUtils.PrintOption(
                "--credentials <path>",
                "Explicitly specify the path to your credentials JSON file."
            );

            CommandLinesUtils.PrintOption(
                "--prices <path>",
                "Specifies path to json with default prices in default currency. If not specified, used path from global config json."
            );
            CommandLinesUtils.PrintOption(
                "--region <region>",
                "Specify the region for which to display prices. Default is US, or region, specified in global config.json"
            );
            CommandLinesUtils.PrintOption(
                "--currency <currency>",
                "Specify the base currency from which to convert prices. Default is USD, or currency specified in global config.json"
            );
            CommandLinesUtils.PrintOption(
                "--iap <iap-id>",
                "Specify the ID of a specific In-App Purchase to display."
            );

            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("commands:");
            Console.WriteLine();

            foreach (var item in this)
            {
                Console.WriteLine(item.Name);
                CommandLinesUtils.PrintDescription(item.Description);
                Console.WriteLine();
            }

            return true;
        }
    }
}

