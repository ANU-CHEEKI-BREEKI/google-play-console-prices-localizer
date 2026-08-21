using GamesConfig = Google.Apis.GamesConfiguration.v1configuration;

namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    /// <summary>
    /// Writes a translated achievements csv back into Play Games Services.
    ///
    /// Only the draft is touched, which is what the console edits - the translations show up in the
    /// console right away and reach players when the game services configuration is published, exactly
    /// as if they had been typed in by hand.
    /// </summary>
    public class Command_LocalesImportAchievements : CommandBase
    {
        const string NameField = "name";
        const string DescriptionField = "description";

        public override bool NeedsAndroidPublisher => false;
        public override bool NeedsGamesConfiguration => true;

        public override async Task ExecuteAsync()
        {
            try
            {
                var verbose = Args.HasFlag("-v");
                var dryRun = Args.HasFlag("-n") || Args.HasFlag("--dry-run");

                if (string.IsNullOrWhiteSpace(GamesProjectId))
                {
                    Console.WriteLine("no games project id. specify 'GamesProjectId' in config.json, or pass --games-project <id>");
                    return;
                }

                var path = Config.AchievementTranslationsFilePath;
                if (!File.Exists(path))
                {
                    Console.WriteLine($"[ERROR] no csv at {Path.GetFullPath(path)}");
                    Console.WriteLine("        run 'locales export achievements' first, or pass --csv <path>");
                    return;
                }

                var csv = await Translations.LoadAsync(path, await LoadLocalesFile(verbose), verbose);

                if (csv.Rows.Count == 0)
                {
                    Console.WriteLine("the csv has no rows to import.");
                    return;
                }

                Console.WriteLine($"read {csv.Rows.Count} key(s) in {csv.Locales.Count} language(s) from {Path.GetFullPath(path)}");
                Console.WriteLine("receiving achievements...");

                var achievements = (await GamesService!.AchievementConfigurations.ListAllAsync(GamesProjectId))
                    .Where(a => !string.IsNullOrWhiteSpace(a.Id))
                    .ToDictionary(a => a.Id!, StringComparer.Ordinal);

                var changed = new List<GamesConfig.Data.AchievementConfiguration>();
                var unchanged = 0;
                var unknown = new List<string>();

                foreach (var group in csv.ById)
                {
                    if (!achievements.TryGetValue(group.Key, out var achievement))
                    {
                        unknown.Add(group.Key);
                        continue;
                    }

                    // the draft is the editable copy. An achievement never touched since it went live
                    // has none, so it starts as a copy of what is published
                    achievement.Draft ??= achievement.Published ?? new GamesConfig.Data.AchievementConfigurationDetail();

                    var edits = 0;

                    foreach (var row in group)
                    {
                        var bundle = row.Field switch
                        {
                            NameField => achievement.Draft.Name ??= new GamesConfig.Data.LocalizedStringBundle(),
                            DescriptionField => achievement.Draft.Description ??= new GamesConfig.Data.LocalizedStringBundle(),
                            _ => null,
                        };

                        if (bundle is null)
                        {
                            Console.WriteLine($"Warning: '{row.Key}' is neither a {NameField} nor a {DescriptionField}, skipped.");
                            continue;
                        }

                        edits += Merge(bundle, row.Values, verbose ? row.Key : null);
                    }

                    if (edits > 0)
                        changed.Add(achievement);
                    else
                        unchanged++;
                }

                foreach (var id in unknown)
                    Console.WriteLine($"Warning: no achievement '{id}' in this games project, skipped.");

                Console.WriteLine();
                Console.WriteLine($"{changed.Count} achievement(s) to update, {unchanged} already up to date, {unknown.Count} unknown.");

                if (changed.Count == 0)
                    return;

                if (dryRun)
                {
                    Console.WriteLine();
                    Console.WriteLine("dry run, nothing was sent:");
                    foreach (var achievement in changed)
                        Console.WriteLine($"        {achievement.Id}  {Summarize(achievement.Draft)}");
                    return;
                }

                Console.WriteLine();

                var written = 0;

                foreach (var achievement in changed)
                {
                    var ok = await Extensions.ExecuteWithRetryAsync(
                        async () => await GamesService.AchievementConfigurations.Update(achievement, achievement.Id).ExecuteAsync(),
                        achievement.Id!
                    );

                    if (ok)
                    {
                        written++;
                        if (verbose)
                            Console.WriteLine($"   {achievement.Id}: {Summarize(achievement.Draft)}");
                    }
                }

                Console.WriteLine();
                Console.WriteLine($"updated {written} of {changed.Count} achievement(s).");
                Console.WriteLine("the translations are in the draft now. Publish the games services configuration in the console to put them in front of players.");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                Console.WriteLine();
                Console.WriteLine("if this is a 400 about a locale, that language is not in the games project yet:");
                Console.WriteLine("Play Games Services -> Setup and management -> Configuration -> Edit properties -> Manage translations");
            }
        }

        /// <summary>
        /// Puts the csv values into a bundle, and answers how many of them were actually a change.
        /// A value identical to what is already there is not a change, which is what keeps a re-run
        /// from sending 73 pointless updates.
        /// </summary>
        static int Merge(GamesConfig.Data.LocalizedStringBundle bundle, Dictionary<string, string> values, string? logKey)
        {
            bundle.Translations ??= [];

            var edits = 0;

            foreach (var (locale, value) in values)
            {
                var existing = bundle.Translations
                    .FirstOrDefault(t => string.Equals(t.Locale, locale, StringComparison.OrdinalIgnoreCase));

                if (existing is null)
                {
                    bundle.Translations.Add(new GamesConfig.Data.LocalizedString { Locale = locale, Value = value });
                    edits++;
                }
                else if (!string.Equals(existing.Value, value, StringComparison.Ordinal))
                {
                    existing.Value = value;
                    edits++;
                }
                else
                {
                    continue;
                }

                if (logKey is not null)
                    Console.WriteLine($"   {logKey} [{locale}]: {value}");
            }

            return edits;
        }

        static string Summarize(GamesConfig.Data.AchievementConfigurationDetail? detail)
            => $"{detail?.Name?.Translations?.Count ?? 0} name(s), {detail?.Description?.Translations?.Count ?? 0} description(s)";

        public override string Name => "locales import achievements";

        public override string Description
            => "Writes a translated achievements csv back into Play Games Services.";

        public override void PrintHelp()
        {
            Console.WriteLine("locales import achievements [--csv <path>] [--locales-file <path>] [--games-project <id>] [-n|--dry-run] [-v]");
            Console.WriteLine();
            Console.WriteLine();

            Console.WriteLine("description:");
            CommandLinesUtils.PrintDescription(Description);
            CommandLinesUtils.PrintDescription("Reads the csv 'locales export achievements' writes: one row per key, one column per language, every key a '<achievement_id>.name' or '<achievement_id>.description'.");
            CommandLinesUtils.PrintDescription("An empty cell means 'not translated yet' and is left alone - it never wipes a translation that is already there. A value identical to what Google already has is not sent, so re-running an unchanged csv does nothing.");
            CommandLinesUtils.PrintDescription("Column headers are mapped back through the locales json, so a column the export wrote as 'id-ID' still reaches the api as 'id'. A column the file says nothing about is taken as a locale code as is.");
            CommandLinesUtils.PrintDescription("Only the draft is written, the copy the console edits. Publish the games services configuration in the console to put the translations in front of players.");
            CommandLinesUtils.PrintDescription("A language has to exist in the games project before anything can be written into it, and no api can add one: Play Games Services -> Setup and management -> Configuration -> Edit properties -> Manage translations.");

            Console.WriteLine();
            Console.WriteLine("options:");

            CommandLinesUtils.PrintOption(
                "--csv <path>",
                "Specifies path to the csv to read. If not specified, used path from global config json ('AchievementTranslationsFilePath'), which defaults to './achievement-translations.csv' next to it."
            );
            CommandLinesUtils.PrintOption(
                "--locales-file <path>",
                "Specifies path to the json that maps a column name back to its locale code. Default is the path from global config json ('LocalesFilePath'), './locales.json' next to it."
            );
            CommandLinesUtils.PrintOption(
                "--games-project <id>",
                "Play Games Services project id, shown in the console next to the game name as 'Project ID'. Default is the id from global config.json."
            );
            CommandLinesUtils.PrintOption(
                "-n|--dry-run",
                "Show what would be written and send nothing."
            );
            CommandLinesUtils.PrintOption(
                "-v",
                "Include additional verbose output"
            );

            CommandLinesUtils.PrintCommonOptions();
        }
    }
}
