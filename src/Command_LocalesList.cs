using Google.Apis.AndroidPublisher.v3.Data;
using GamesConfig = Google.Apis.GamesConfiguration.v1configuration;

namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    /// <summary>
    /// Prints the languages that already exist in the three places Google keeps them, side by side.
    /// Google keeps three independent lists - the store listing, the Play Games Services translations
    /// and the one-time product listings - and nothing keeps them in sync. The same game routinely
    /// ends up with es-419 on the store page and es-ES in the achievements, or with a language present
    /// in one place and missing in the other two, and the console never shows that anywhere.
    /// Seeing the three lists next to each other is the whole point of this command.
    /// </summary>
    public class Command_LocalesList : CommandBase
    {
        const string StoreListingArea = "store listing";
        const string GamesArea = "play games services";
        const string ProductsArea = "one-time products";

        public override bool NeedsGamesConfiguration => true;

        bool verbose;

        /// <summary>areas that were actually read, in print order. An area that was skipped or failed is not compared</summary>
        readonly List<string> readAreas = [];

        /// <summary>locale code -> the areas that hold it. Codes are compared exactly, es-419 and es-ES are two locales</summary>
        readonly Dictionary<string, List<string>> localeAreas = new(StringComparer.OrdinalIgnoreCase);

        public override async Task ExecuteAsync()
        {
            verbose = Args.HasFlag("-v");

            if (string.IsNullOrWhiteSpace(Package))
            {
                Console.WriteLine("no package name. specify it in config.json or with --package");
                return;
            }

            await PrintStoreListingLocales();
            await PrintGamesLocales();
            await PrintProductLocales();

            PrintDifferences();
        }

        /// <summary>
        /// The store listing has no locale registry of its own: a language exists exactly because a
        /// listing exists for it. Reading them needs an edit, which is created and then thrown away -
        /// never committed, so this stays a read.
        /// </summary>
        async Task PrintStoreListingLocales()
        {
            PrintHeader(StoreListingArea);

            AppEdit? edit = null;
            try
            {
                edit = await Service!.Edits.Insert(null, Package).ExecuteAsync();

                var defaultLanguage = "";
                try
                {
                    var details = await Service.Edits.Details.Get(Package, edit.Id).ExecuteAsync();
                    defaultLanguage = details.DefaultLanguage ?? "";
                }
                catch (Exception ex) when (verbose)
                {
                    Console.WriteLine($"        could not read the app details: {ex.Message}");
                }

                var response = await Service.Edits.Listings.List(Package, edit.Id).ExecuteAsync();
                var listings = (response.Listings ?? []).OrderBy(l => l.Language, StringComparer.Ordinal).ToList();

                if (listings.Count == 0)
                {
                    Console.WriteLine("        none");
                    return;
                }

                foreach (var listing in listings)
                {
                    var note = string.Equals(listing.Language, defaultLanguage, StringComparison.OrdinalIgnoreCase)
                        ? "default"
                        : "";
                    PrintLocale(listing.Language, note, listing.Title ?? "");
                    Remember(listing.Language, StoreListingArea);
                }

                readAreas.Add(StoreListingArea);
            }
            catch (Exception ex)
            {
                PrintFailure(ex);
            }
            finally
            {
                // discard the draft edit, an abandoned one would sit in the console as a pending change
                if (edit is not null)
                {
                    try
                    {
                        await Service!.Edits.Delete(Package, edit.Id).ExecuteAsync();
                    }
                    catch (Exception ex) when (verbose)
                    {
                        Console.WriteLine($"        could not discard the edit {edit.Id}: {ex.Message}");
                    }
                }
            }
        }

        /// <summary>
        /// Play Games Services keeps its languages in the game details, and the configuration API
        /// exposes no resource for them at all - only achievements and leaderboards. So the list is
        /// reconstructed from the translations the two of them actually carry, which means a language
        /// added in the console but never filled in anywhere stays invisible here.
        /// </summary>
        async Task PrintGamesLocales()
        {
            PrintHeader(GamesArea);

            if (string.IsNullOrWhiteSpace(GamesProjectId))
            {
                Console.WriteLine("        skipped: no games project id");
                Console.WriteLine("        set 'GamesProjectId' in config.json, or pass --games-project <id>");
                Console.WriteLine("        the console shows it next to the game name, as 'Project ID'");
                return;
            }

            try
            {
                var achievements = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var leaderboards = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var item in await GamesService!.AchievementConfigurations.ListAllAsync(GamesProjectId))
                    CountLocales(achievements, item.Draft?.Name, item.Draft?.Description, item.Published?.Name, item.Published?.Description);

                foreach (var item in await GamesService.LeaderboardConfigurations.ListAllAsync(GamesProjectId))
                    CountLocales(leaderboards, item.Draft?.Name, item.Published?.Name);

                var locales = achievements.Keys.Concat(leaderboards.Keys)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(l => l, StringComparer.Ordinal)
                    .ToList();

                if (locales.Count == 0)
                {
                    Console.WriteLine("        none");
                    return;
                }

                foreach (var locale in locales)
                {
                    achievements.TryGetValue(locale, out var a);
                    leaderboards.TryGetValue(locale, out var b);
                    PrintLocale(locale, "", $"{a} achievements, {b} leaderboards");
                    Remember(locale, GamesArea);
                }

                readAreas.Add(GamesArea);
            }
            catch (Exception ex)
            {
                PrintFailure(ex);
                Console.WriteLine("        if this is a 403, enable the 'Google Play Game Services Publishing API' in your Cloud project");
            }
        }

        /// <summary>
        /// Like the store listing, a product has no locale registry either: the language exists because
        /// an entry with that language code sits in the product's listings.
        /// </summary>
        async Task PrintProductLocales()
        {
            PrintHeader(ProductsArea);

            try
            {
                var products = (await Service!.Monetization.Onetimeproducts.ListAllAsync(Package))
                    .Filter(IapFilter)
                    .ToList();

                if (products.Count == 0)
                {
                    Console.WriteLine("        no products");
                    return;
                }

                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var product in products)
                {
                    foreach (var language in (product.Listings ?? [])
                        .Select(l => l.LanguageCode)
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        counts.TryGetValue(language, out var count);
                        counts[language] = count + 1;
                    }
                }

                if (counts.Count == 0)
                {
                    Console.WriteLine("        none");
                    return;
                }

                foreach (var pair in counts.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    var note = pair.Value == products.Count ? "" : "partial";
                    PrintLocale(pair.Key, note, $"{pair.Value} of {products.Count} products");
                    Remember(pair.Key, ProductsArea);
                }

                readAreas.Add(ProductsArea);
            }
            catch (Exception ex)
            {
                PrintFailure(ex);
            }
        }

        /// <summary>
        /// The payoff: every locale that is not in all three places at once. Areas that could not be
        /// read are left out of the comparison, otherwise everything would look missing from them.
        /// </summary>
        void PrintDifferences()
        {
            Console.WriteLine();

            if (readAreas.Count < 2)
            {
                Console.WriteLine("not everywhere:");
                Console.WriteLine("        nothing to compare, less than two areas could be read");
                return;
            }

            var incomplete = localeAreas
                .Where(pair => pair.Value.Count < readAreas.Count)
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToList();

            Console.WriteLine($"not everywhere ({string.Join(", ", readAreas)}):");

            if (incomplete.Count == 0)
            {
                Console.WriteLine("        nothing, every locale is in every area");
                return;
            }

            foreach (var pair in incomplete)
            {
                var missing = readAreas.Where(a => !pair.Value.Contains(a));
                Console.WriteLine($"        {pair.Key,-10} missing from: {string.Join(", ", missing)}");
            }
        }

        /// <summary>counts one item once per locale, no matter how many of its bundles carry that locale</summary>
        static void CountLocales(Dictionary<string, int> counts, params GamesConfig.Data.LocalizedStringBundle?[] bundles)
        {
            foreach (var locale in bundles.SelectMany(b => b.Locales()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                counts.TryGetValue(locale, out var count);
                counts[locale] = count + 1;
            }
        }

        void Remember(string? locale, string area)
        {
            if (string.IsNullOrWhiteSpace(locale))
                return;

            if (!localeAreas.TryGetValue(locale, out var areas))
                localeAreas[locale] = areas = [];

            if (!areas.Contains(area))
                areas.Add(area);
        }

        static void PrintHeader(string area)
        {
            Console.WriteLine();
            Console.WriteLine($"{area}:");
        }

        static void PrintLocale(string? code, string note, string detail)
            => Console.WriteLine($"        {code,-10} {note,-8} {detail}".TrimEnd());

        void PrintFailure(Exception ex)
            => Console.WriteLine(verbose ? $"        failed: {ex}" : $"        failed: {ex.Message}");

        public override string Name => "locales list";

        public override string Description
            => "Lists the languages that already exist in the store listing, in Play Games Services and in the one-time products, and shows which of them are missing from where. Read only, it never writes anything.";

        public override void PrintHelp()
        {
            Console.WriteLine("locales list [--games-project <id>] [--iap <id[,id...]>] [-v]");
            Console.WriteLine();
            Console.WriteLine();

            Console.WriteLine("description:");
            CommandLinesUtils.PrintDescription(Description);
            CommandLinesUtils.PrintDescription("Google keeps three independent language lists and never syncs them. The store listing and the products hold a language because a listing for it exists, so there is nothing to 'add' on its own - writing the text creates the locale. Play Games Services is different: its languages live in the game details, the configuration API cannot touch them at all, and they can only be added by hand in the console.");
            CommandLinesUtils.PrintDescription("This is the default subcommand: plain 'locales' runs it.");
            CommandLinesUtils.PrintDescription("The games list here is reconstructed from the translations the achievements and leaderboards actually carry, so a language added in the console but left empty everywhere will not show up.");

            Console.WriteLine();
            Console.WriteLine("options:");
            CommandLinesUtils.PrintOption(
                "--games-project <id>",
                "Play Games Services project id, shown in the console next to the game name as 'Project ID'. Default is the id from global config.json. Without it the games list is skipped."
            );
            CommandLinesUtils.PrintOption(
                CommandLinesUtils.IapOptionName,
                CommandLinesUtils.IapOptionDescription + " Narrows the product list only."
            );
            CommandLinesUtils.PrintOption(
                "-v",
                "Include detailed verbose output"
            );

            CommandLinesUtils.PrintCommonOptions();
        }
    }
}
