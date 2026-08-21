namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    /// <summary>
    /// Everything about languages lives under one command, because everything about languages shares
    /// one 'SourceLocales' and one locales json. 'locales list' shows what exists where, and
    /// 'locales export ...' pulls the text out into a translatable csv.
    ///
    /// Only a router: each subcommand is a CommandBase of its own, so it keeps its own help, its own
    /// options and its own answer to which Google services it needs.
    /// </summary>
    public class Command_Locales : CommandBase
    {
        /// <summary>
        /// the subcommand the args select, or the listing when they select nothing.
        /// Resolved off Args, which Program hands over before it reads any of the Needs* properties,
        /// so a subcommand that needs no games project never asks for one
        /// </summary>
        CommandBase Sub => sub ??= Resolve();
        CommandBase? sub;

        CommandBase Resolve()
        {
            var name = Arg(1);
            var target = Arg(2);

            return (name, target) switch
            {
                ("" or "list", _) => new Command_LocalesList(),

                ("export", "achievements") => new Command_LocalesExportAchievements(),
                ("export", "iaps") => new Command_LocalesExportIaps(),

                _ => new Unknown(name, target),
            };
        }

        string Arg(int index)
            => Args.Length > index && !Args[index].StartsWith('-') ? Args[index] : "";

        public override bool NeedsConfig => Sub.NeedsConfig;
        public override bool NeedsAndroidPublisher => Sub.NeedsAndroidPublisher;
        public override bool NeedsPlayDeveloperReporting => Sub.NeedsPlayDeveloperReporting;
        public override bool NeedsGamesConfiguration => Sub.NeedsGamesConfiguration;
        public override string AuthUserKey => Sub.AuthUserKey;

        public override Task ExecuteAsync()
        {
            Sub.Initialize(Service, ReportingService, GamesService, Config, Args);
            return Sub.ExecuteAsync();
        }

        public override string Name => "locales";

        public override string Description
            => "Everything about languages: which ones exist where, and exporting the translatable text out of them. Run 'locales' on its own for the listing.";

        public override void PrintHelp()
        {
            // 'locales export achievements --help' should explain that subcommand, not this router
            if (Sub is not Unknown && Sub is not Command_LocalesList)
            {
                Sub.PrintHelp();
                return;
            }

            Console.WriteLine("locales [list]");
            Console.WriteLine("locales export achievements [options]");
            Console.WriteLine("locales export iaps [options]");
            Console.WriteLine();
            Console.WriteLine();

            Console.WriteLine("description:");
            CommandLinesUtils.PrintDescription(Description);
            CommandLinesUtils.PrintDescription("Google keeps three independent language lists for one game - the store listing, the Play Games Services translations and the one-time product listings - and nothing keeps them in sync. They do not even agree on the codes: es-419 on the store page against es-ES in the achievements, hebrew as iw-IL.");
            CommandLinesUtils.PrintDescription("The exports all write the same shape of csv, one row per key and one column per language, and all read the same 'SourceLocales' and locales json to decide the columns.");

            Console.WriteLine();
            Console.WriteLine("subcommands:");

            CommandLinesUtils.PrintOption("list", "Show which languages exist in the store listing, in Play Games Services and in the products, and which are missing from where. This is the default when no subcommand is given.");
            CommandLinesUtils.PrintOption("export achievements", "Write every achievement name and description into a translatable csv.");
            CommandLinesUtils.PrintOption("export iaps", "Write every One-time product title and description into a translatable csv.");

            Console.WriteLine();
            Console.WriteLine("Run 'locales <subcommand> --help' for the options of one subcommand.");
            Console.WriteLine();

            Console.WriteLine("examples:");
            Console.WriteLine();
            CommandLinesUtils.PrintDescription("locales                                    # what exists where", 4);
            CommandLinesUtils.PrintDescription("locales export achievements                # 73 achievements out to a csv", 4);
            CommandLinesUtils.PrintDescription("locales export iaps --iap pack_one         # one product only", 4);
            CommandLinesUtils.PrintDescription("locales export iaps --locales en-US,uk     # two columns, ignore the locales json", 4);
        }

        /// <summary>a subcommand that does not exist, kept as a CommandBase so the router stays uniform</summary>
        class Unknown(string name, string target) : CommandBase
        {
            public override bool NeedsConfig => false;
            public override bool NeedsAndroidPublisher => false;

            public override Task ExecuteAsync()
            {
                var typed = string.IsNullOrEmpty(target) ? name : $"{name} {target}";
                Console.WriteLine($"unknown subcommand '{typed}'. see 'locales --help'.");
                return Task.CompletedTask;
            }

            public override string Name => "locales";
            public override string Description => "";
            public override void PrintHelp() { }
        }
    }
}
