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
            Console.WriteLine();
            Console.WriteLine("------------------------------------------------------------------------------------------------");
            Console.WriteLine("----------------------------------### ATTENTION ###---------------------------------------------");
            Console.WriteLine("------------------------------------------------------------------------------------------------");
            Console.WriteLine("");
            Console.WriteLine("THE PROJECT WAS UPDATED TO USE NEW GOOGLE APIS FOR Onetimeproducts");
            Console.WriteLine("but, since i (as Unity game-dev) dont use IAP purchase options, and use only the Default purchase option,");
            Console.WriteLine("this project designed to work only with single 'LegacyCompatible'/'Backward compatible' purchase options");
            Console.WriteLine("");
            Console.WriteLine("------------------------------------------------------------------------------------------------");
            Console.WriteLine();
            Console.WriteLine("usage:");
            Console.WriteLine();
            Console.WriteLine("<command> [command-options] [--config <path_to_config.json> | --profile <name>] [config-options]");
            Console.WriteLine();

            Console.WriteLine("options:");

            CommandLinesUtils.PrintOption(
                "[command-options]",
                "Command-specific options"
            );
            CommandLinesUtils.PrintOption(
                CommandLinesUtils.IapOptionName,
                CommandLinesUtils.IapOptionDescription + " Works with every command that touches products."
            );

            CommandLinesUtils.PrintOption(
                "--config <path>",
                "Explicitly specify the path to your app config JSON file. You can also provide only the path to folder that contains the 'config.json' file."
            );
            CommandLinesUtils.PrintOption(
                "--profile <name>",
                "Use the config registered under this name, see the 'config' command. When neither --config nor --profile is given, the current profile is used, and if there is none, '../config.json'."
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
                "--products <path>",
                "Specifies path to the csv with product definitions used by the 'export-iaps' and 'create-iaps' commands. If not specified, used path from global config json ('ProductDefinitionsFilePath'), which defaults to './product-definitions.csv' next to it."
            );
            CommandLinesUtils.PrintOption(
                "--achievements <path>",
                "Specifies path to the csv with achievement translations used by the 'export-achievements' command. If not specified, used path from global config json ('AchievementDefinitionsFilePath'), which defaults to './achievement-definitions.csv' next to it."
            );
            CommandLinesUtils.PrintOption(
                "--language <code>",
                "Language of the store listing the 'export-iaps' and 'create-iaps' commands read and write. Default is en-US, or the language specified in global config.json."
            );
            CommandLinesUtils.PrintOption(
                "--source-locales <code[,code...]>",
                "Locales that lead the exported columns, in this exact order, and that are always exported even when empty. These are the languages a translation service reads as its context. Default is the list from global config.json ('SourceLocales')."
            );
            CommandLinesUtils.PrintOption(
                "--locales-file <path>",
                "Specifies path to the json with every locale the exports produce a column for, a plain array of codes. If not specified, used path from global config json ('LocalesFilePath'), which defaults to './locales.json' next to it."
            );
            CommandLinesUtils.PrintOption(
                "--locales <code[,code...]>",
                "Locales to export columns for, for this run only. Overrides the whole locales json file."
            );
            CommandLinesUtils.PrintOption(
                "--games-project <id>",
                "Play Games Services project id, shown in the console next to the game name. Used by the 'locales' command to read the games translations. Default is the id from global config.json."
            );
            CommandLinesUtils.PrintOption(
                "--out <path>",
                "Specify the directory the 'vitals' command writes its reports into. Default is './vitals-export' next to the global config json."
            );

            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("commands:");
            Console.WriteLine();

            foreach (var item in this)
            {
                CommandLinesUtils.PrintOption(item.Name, item.Description);
                Console.WriteLine();
            }

            Console.WriteLine();
            Console.WriteLine("Run '<executable> [command] --help|-h' for more information on a command.");
            Console.WriteLine();

            return true;
        }
    }
}

