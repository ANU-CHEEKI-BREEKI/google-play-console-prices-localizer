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

                if (!NamesAreUnique(csv))
                    return;

                Console.WriteLine("receiving achievements...");

                var changes = await BuildChanges(csv, [], verbose);

                Console.WriteLine();
                Console.WriteLine($"{changes.Changed.Count} achievement(s) to update, {changes.Unchanged} already up to date, {changes.Unknown} unknown.");

                if (changes.Changed.Count == 0)
                    return;

                if (dryRun)
                {
                    Console.WriteLine();
                    Console.WriteLine("dry run, nothing was sent:");
                    foreach (var achievement in changes.Changed)
                        Console.WriteLine($"        {achievement.Id}  {Summarize(achievement.Draft)}");
                    return;
                }

                Console.WriteLine();

                // one achievement goes first, on its own. Play Games Services rejects a whole request
                // over a single locale it does not accept, and there is no point finding that out 73
                // times in a row
                var canary = changes.Changed[0];
                var refused = await FindRefusedLocales(canary, csv.Locales);

                if (refused is null)
                    return;

                var written = 1;

                if (refused.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"dropping {refused.Count} language(s) Google refused: {string.Join(", ", refused)}");
                    Console.WriteLine();

                    // start over without them, against freshly listed achievements. Rebuilding is what
                    // keeps the count honest - with the refused languages gone, most of the csv often
                    // turns out to change nothing at all - and the probe left this one's token stale
                    changes = await BuildChanges(csv, refused, verbose);

                    Console.WriteLine($"{changes.Changed.Count} achievement(s) still to update in the languages that were accepted.");
                    written = 0;
                }
                else
                {
                    // the probe already wrote the canary, and its token is stale now
                    changes.Changed.RemoveAt(0);
                }

                foreach (var achievement in changes.Changed)
                {
                    var ok = await Extensions.ExecuteWithRetryAsync(
                        async () => await GamesService!.AchievementConfigurations.Update(achievement, achievement.Id).ExecuteAsync(),
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
                Console.WriteLine($"updated {written} achievement(s).");
                Console.WriteLine("the translations are in the draft now. Publish the games services configuration in the console to put them in front of players.");

                if (refused.Count > 0)
                    PrintRefusedHelp(refused);
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
        /// Achievement names have to be unique per locale, and Google accepts a clashing one, then
        /// blocks publishing with "there is a problem with your achievement" and never says what.
        /// Translators collapse near synonyms all the time - Deadeye and Sharpshooter both become
        /// Scharfschütze - so this is caught here rather than in the console days later.
        /// </summary>
        static bool NamesAreUnique(TranslationsCsv csv)
        {
            var names = csv.Rows.Where(r => r.Field == NameField).ToList();
            var clashes = new List<string>();

            foreach (var locale in csv.Locales)
            {
                var byName = names
                    .Where(r => r.Values.ContainsKey(locale.Locale))
                    .GroupBy(r => r.Values[locale.Locale].Trim(), StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Count() > 1);

                foreach (var group in byName)
                    clashes.Add($"        [{locale.Column}] \"{group.Key}\" is used by {string.Join(" and ", group.Select(r => r.Id))}");
            }

            if (clashes.Count == 0)
                return true;

            Console.WriteLine();
            Console.WriteLine($"[ERROR] {clashes.Count} achievement name(s) are not unique within their language.");
            Console.WriteLine("        Google takes them, then refuses to publish and does not say why. Nothing was sent.");
            Console.WriteLine();
            foreach (var clash in clashes)
                Console.WriteLine(clash);
            Console.WriteLine();
            Console.WriteLine("        Give each of them its own wording in the csv and run again.");

            return false;
        }

        record Changes(List<GamesConfig.Data.AchievementConfiguration> Changed, int Unchanged, int Unknown);

        /// <summary>
        /// Lists the achievements afresh and merges the csv into them, skipping
        /// <paramref name="refused"/> entirely. Freshly listed on purpose: an achievement carries a
        /// token that goes stale the moment something writes to it, and the probe writes.
        /// </summary>
        async Task<Changes> BuildChanges(TranslationsCsv csv, List<string> refused, bool verbose)
        {
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

                    var values = refused.Count == 0
                        ? row.Values
                        : row.Values
                            .Where(v => !refused.Contains(v.Key, StringComparer.OrdinalIgnoreCase))
                            .ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);

                    edits += Merge(bundle, values, verbose ? row.Key : null);
                }

                if (edits > 0)
                    changed.Add(achievement);
                else
                    unchanged++;
            }

            foreach (var id in unknown)
                Console.WriteLine($"Warning: no achievement '{id}' in this games project, skipped.");

            return new Changes(changed, unchanged, unknown.Count);
        }

        /// <summary>
        /// Sends one achievement on its own until it goes through, working out which locales Google
        /// will not take. Answers those locales, or null when the request fails for a reason that is
        /// not about a locale at all - there is nothing sensible to do with the other 72 then.
        ///
        /// Play Games Services refuses a whole request over one bad locale, and is only sometimes
        /// helpful about which:
        ///   "The locale uk in the name field is not supported by the application"
        ///        - a real code, just not added to the games project yet. Named, so it can just go.
        ///   "Localized string has invalid locale code"
        ///        - not a code Google knows at all, and it will not say which one. Found by halving.
        /// </summary>
        async Task<List<string>?> FindRefusedLocales(GamesConfig.Data.AchievementConfiguration canary, List<LocaleColumn> locales)
        {
            var snapshot = Snapshot(canary.Draft);
            var alive = locales.Select(l => l.Locale).ToList();
            var refused = new List<string>();

            // worst case one round per locale, plus the round that finally succeeds
            for (int round = 0; round <= locales.Count; round++)
            {
                var error = await Send(canary, snapshot, alive);

                if (error is null)
                    return refused;

                var named = UnsupportedLocale(error.Message);

                if (named is not null)
                {
                    Console.WriteLine($"   Google refuses '{named}': not added to the games project");
                    refused.Add(named);
                    alive.RemoveAll(l => string.Equals(l, named, StringComparison.OrdinalIgnoreCase));
                    continue;
                }

                if (!IsInvalidLocaleCode(error.Message))
                {
                    Console.WriteLine($"[ERROR] Google Play rejected {canary.Id} and the whole import stopped here.");
                    Console.WriteLine($"        {error.Message.Trim()}");
                    Console.WriteLine("        Nothing was written.");
                    return null;
                }

                // Google will not name it, so halve the list until a single locale is left holding it
                Console.WriteLine("   Google says one of the locale codes is invalid but not which one, looking for it...");

                var bad = await FindOneInvalid(canary, snapshot, alive);

                if (bad is null)
                {
                    Console.WriteLine($"[ERROR] could not work out which locale code Google objects to.");
                    Console.WriteLine($"        {error.Message.Trim()}");
                    Console.WriteLine($"        the request carried: {string.Join(", ", alive)}");
                    Console.WriteLine("        Nothing was written.");
                    return null;
                }

                Console.WriteLine($"   Google refuses '{bad}': not a locale code Play Games Services knows");
                unknownCodes.Add(bad);
                refused.Add(bad);
                alive.RemoveAll(l => string.Equals(l, bad, StringComparison.OrdinalIgnoreCase));
            }

            return refused;
        }

        /// <summary>
        /// Binary search for one locale Google calls invalid. The default language rides along in every
        /// probe, because an achievement without it is a different kind of broken.
        /// </summary>
        async Task<string?> FindOneInvalid(
            GamesConfig.Data.AchievementConfiguration canary,
            Snapshotted snapshot,
            List<string> suspects
        )
        {
            while (suspects.Count > 1)
            {
                var half = suspects.Take(suspects.Count / 2).ToList();

                if (half.Count == 0)
                    return null;

                var error = await Send(canary, snapshot, WithDefault(half));

                if (error is not null && IsInvalidLocaleCode(error.Message))
                {
                    suspects = half;
                    continue;
                }

                // this half is clean, so the one Google objects to is in the other
                suspects = [.. suspects.Skip(suspects.Count / 2)];
            }

            return suspects.FirstOrDefault();
        }

        List<string> WithDefault(List<string> locales)
        {
            var withDefault = new List<string>(locales);

            if (!string.IsNullOrWhiteSpace(Config.DefaultLanguageCode)
                && !withDefault.Contains(Config.DefaultLanguageCode, StringComparer.OrdinalIgnoreCase))
            {
                withDefault.Insert(0, Config.DefaultLanguageCode);
            }

            return withDefault;
        }

        /// <summary>
        /// Sends the canary carrying exactly these locales. null means Google took it.
        ///
        /// An achievement carries a token that every accepted write invalidates, and the search here
        /// writes on purpose - so a stale token is expected rather than exceptional, and costs one
        /// extra read to fix
        /// </summary>
        async Task<Google.GoogleApiException?> Send(
            GamesConfig.Data.AchievementConfiguration canary,
            Snapshotted snapshot,
            List<string> locales
        )
        {
            snapshot.ApplyTo(canary.Draft, locales);

            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    await GamesService!.AchievementConfigurations.Update(canary, canary.Id).ExecuteAsync();
                    return null;
                }
                catch (Google.GoogleApiException ex)
                {
                    if (attempt > 0 || !IsStaleToken(ex.Message))
                        return ex;

                    var fresh = await GamesService!.AchievementConfigurations.Get(canary.Id).ExecuteAsync();
                    canary.Token = fresh.Token;
                }
            }

            return null;
        }

        static bool IsInvalidLocaleCode(string message)
            => message.Contains("invalid locale code", StringComparison.OrdinalIgnoreCase);

        static bool IsStaleToken(string message)
            => message.Contains("token is too old", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// the canary's translations as they came out of the csv merge, so a probe can put back any
        /// subset of them without having to redo the merge
        /// </summary>
        record Snapshotted(List<GamesConfig.Data.LocalizedString> Name, List<GamesConfig.Data.LocalizedString> Description)
        {
            public void ApplyTo(GamesConfig.Data.AchievementConfigurationDetail? detail, List<string> locales)
            {
                if (detail is null)
                    return;

                Apply(detail.Name, Name);
                Apply(detail.Description, Description);

                void Apply(GamesConfig.Data.LocalizedStringBundle? bundle, List<GamesConfig.Data.LocalizedString> all)
                {
                    if (bundle is null)
                        return;

                    bundle.Translations = [.. all.Where(t => locales.Contains(t.Locale, StringComparer.OrdinalIgnoreCase))];
                }
            }
        }

        static Snapshotted Snapshot(GamesConfig.Data.AchievementConfigurationDetail? detail)
            => new(
                [.. detail?.Name?.Translations ?? []],
                [.. detail?.Description?.Translations ?? []]
            );

        /// <summary>
        /// the locale out of "The locale uk in the name field is not supported by the application".
        /// null when the message is not that one
        /// </summary>
        static string? UnsupportedLocale(string message)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                message,
                @"The locale (?<locale>[A-Za-z0-9\-_]+) in the \w+ field is not supported"
            );

            return match.Success ? match.Groups["locale"].Value : null;
        }

        static void Drop(GamesConfig.Data.AchievementConfigurationDetail? detail, IReadOnlyCollection<string> locales)
        {
            Drop(detail?.Name, locales);
            Drop(detail?.Description, locales);

            static void Drop(GamesConfig.Data.LocalizedStringBundle? bundle, IReadOnlyCollection<string> locales)
            {
                if (bundle?.Translations is null)
                    return;

                // the generated data class exposes an IList, so no RemoveAll to lean on
                foreach (var translation in bundle.Translations.ToList())
                {
                    if (locales.Contains(translation.Locale, StringComparer.OrdinalIgnoreCase))
                        bundle.Translations.Remove(translation);
                }
            }
        }

        /// <summary>
        /// The two refusals need two different answers, so they are never printed as one list:
        /// a language that is only missing from the games project is one checkbox away, while an
        /// unknown code will never work and only belongs out of the csv.
        /// </summary>
        void PrintRefusedHelp(List<string> refused)
        {
            var unknown = refused.Where(unknownCodes.Contains).ToList();
            var missing = refused.Where(l => !unknownCodes.Contains(l)).ToList();

            if (missing.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"{missing.Count} language(s) are not turned on for this games project: {string.Join(", ", missing)}");
                Console.WriteLine("Google knows the codes, they are just not enabled here. Add them once:");
                Console.WriteLine("        Play Games Services -> Setup and management -> Configuration -> Edit properties -> Manage translations");
                Console.WriteLine("then run this command again - everything already imported is skipped as up to date.");
            }

            if (unknown.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"{unknown.Count} code(s) are not locales Play Games Services knows at all: {string.Join(", ", unknown)}");
                Console.WriteLine("no console setting will ever accept these. Take them out of your locales json and out of the csv,");
                Console.WriteLine("and check the spelling against the language picker under Manage translations.");
            }
        }

        /// <summary>codes Google called invalid outright, as opposed to merely not enabled</summary>
        readonly HashSet<string> unknownCodes = new(StringComparer.OrdinalIgnoreCase);

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
