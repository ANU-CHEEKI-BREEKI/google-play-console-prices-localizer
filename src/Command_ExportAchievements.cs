using Newtonsoft.Json.Linq;
using GamesConfig = Google.Apis.GamesConfiguration.v1configuration;

namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    /// <summary>
    /// Exports every achievement's name and description into a csv laid out the way translation
    /// tooling expects it: one row per key, one column per language.
    ///
    /// Google never machine translates achievements the way it does the store page, so a game that
    /// ships 70 of them in English ships 70 of them in English everywhere. The console can only be
    /// clicked through one achievement and one language at a time, which is what makes this the
    /// single most requested translation nobody ever gets around to.
    /// </summary>
    public class Command_ExportAchievements : CommandBase
    {
        const string KeyHeader = "key";

        /// <summary>
        /// key suffixes. Achievement ids are base64url tokens like CgkIj8z_jpUZEAIQAQ and never
        /// contain a dot, so a dot separates the id from the field without any escaping
        /// </summary>
        const string NameSuffix = ".name";
        const string DescriptionSuffix = ".description";

        public override bool NeedsAndroidPublisher => false;
        public override bool NeedsGamesConfiguration => true;

        public override async Task ExecuteAsync()
        {
            try
            {
                var verbose = Args.HasFlag("-v");

                if (string.IsNullOrWhiteSpace(GamesProjectId))
                {
                    Console.WriteLine("no games project id. specify 'GamesProjectId' in config.json, or pass --games-project <id>");
                    Console.WriteLine("the console shows it next to the game name, as 'Project ID'");
                    return;
                }

                var path = Config.AchievementDefinitionsFilePath;
                if (string.IsNullOrWhiteSpace(path) || Directory.Exists(path))
                {
                    Console.WriteLine($"[ERROR] '{path}' is not a file to write the csv into.");
                    Console.WriteLine("        set 'AchievementDefinitionsFilePath' in your config.json, or pass --achievements <path>");
                    return;
                }

                Console.WriteLine("receiving achievements...");

                var achievements = await GamesService!.AchievementConfigurations.ListAllAsync(GamesProjectId);

                if (achievements.Count == 0)
                {
                    Console.WriteLine("no achievements to export.");
                    return;
                }

                // the draft is what the console edits and what 'import-achievements' writes back,
                // the published copy is only a fallback for an achievement never edited since it went live
                var details = achievements.ToDictionary(
                    a => a,
                    a => a.Draft ?? a.Published
                );

                var locales = await ResolveLocales(details.Values, verbose);

                if (locales.Count == 0)
                {
                    Console.WriteLine("no translations at all, and no locales configured. nothing to put in the columns.");
                    return;
                }

                Console.WriteLine($"exporting {achievements.Count} achievement(s) in {locales.Count} language(s) into {Path.GetFullPath(path)}...");

                // the console orders achievements by sort rank, keep the csv in the order the author sees
                var ordered = achievements
                    .OrderBy(a => details[a]?.SortRank ?? int.MaxValue)
                    .ThenBy(a => a.Id, StringComparer.Ordinal);

                var rows = new List<List<string>>();

                foreach (var achievement in ordered)
                {
                    var detail = details[achievement];

                    if (detail is null)
                    {
                        Console.WriteLine($"Warning: {achievement.Id} has neither a draft nor a published version, skipped.");
                        continue;
                    }

                    // name and description are two separate keys: a translation service wants one
                    // string per row, and the two are translated independently anyway
                    rows.Add(BuildRow(achievement.Id + NameSuffix, detail.Name, locales));
                    rows.Add(BuildRow(achievement.Id + DescriptionSuffix, detail.Description, locales));

                    if (verbose)
                        Console.WriteLine($"   {achievement.Id}: \"{detail.Name.ValueFor(locales[0].Locale)}\"");
                }

                List<string> headers = [KeyHeader, .. locales.Select(l => l.Column)];

                await CommandLinesUtils.SaveCsv(path, headers, rows);

                Console.WriteLine();
                Console.WriteLine($"written: {Path.GetFullPath(path)}");
                Console.WriteLine($"{rows.Count} key(s) from {rows.Count / 2} achievement(s), {locales.Count} language(s): {string.Join(", ", locales)}");

                PrintCoverage(rows, locales);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                Console.WriteLine();
                Console.WriteLine("if this is a 403, enable the 'Google Play Game Services Publishing API' in your Cloud project");
            }
        }

        static List<string> BuildRow(string key, GamesConfig.Data.LocalizedStringBundle? bundle, List<LocaleColumn> locales)
            => [key, .. locales.Select(l => bundle.ValueFor(l.Locale))];

        /// <summary>
        /// The columns of the csv. Every locale the tool can see gets one - the source locales only
        /// decide what comes first, they never narrow anything down.
        ///
        /// Order: the source locales, in exactly the order they are configured, because a translation
        /// service reads the leading columns as its context and the order decides which one is the
        /// primary source. Then everything already translated, then the locales json, then whatever
        /// --locales overrides it with for this one run.
        ///
        /// The locales json is not a nicety: Play Games Services hides a language until something is
        /// translated into it, and the api exposes no language list at all, so a language added in the
        /// console and still empty is invisible unless it is named in the file.
        /// </summary>
        async Task<List<LocaleColumn>> ResolveLocales(IEnumerable<GamesConfig.Data.AchievementConfigurationDetail?> details, bool verbose)
        {
            var found = details
                .SelectMany(d => d?.Name.Locales().Concat(d.Description.Locales()) ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(l => l, StringComparer.Ordinal)
                .Select(l => new LocaleColumn(l, l));

            // without a configured order the single default language leads, as it did before
            var leading = (Config.SourceLocales is { Count: > 0 }
                    ? Config.SourceLocales
                    : [Config.DefaultLanguageCode])
                .Select(l => new LocaleColumn(l, l));

            var configured = Config.Locales is { Count: > 0 }
                ? Config.Locales.Select(l => new LocaleColumn(l, l))
                : await LoadLocalesFile(verbose);

            var locales = new List<LocaleColumn>();

            foreach (var locale in leading.Concat(found).Concat(configured))
            {
                if (string.IsNullOrWhiteSpace(locale.Locale))
                    continue;

                // the first mention wins, so a locale named in the file keeps the column name from there
                // only when nothing earlier already claimed it
                if (!locales.Any(l => string.Equals(l.Locale, locale.Locale, StringComparison.OrdinalIgnoreCase)))
                    locales.Add(locale);
            }

            return locales;
        }

        /// <summary>
        /// The locales json: a plain array of locale codes, in the order the columns should come out.
        ///
        /// An entry is normally just the code, which is both what Google wants and what the csv column
        /// is called. When those two have to differ, the entry is a one property object instead -
        /// { "id": "id-ID" } keeps sending "id" to the api while the csv says "id-ID", because a
        /// translation service reads a column called "id" as an identifier rather than indonesian.
        ///
        /// A root level object works too, for a file that is all aliases. A missing file is not an
        /// error, it just means nothing beyond what is already translated.
        /// </summary>
        async Task<List<LocaleColumn>> LoadLocalesFile(bool verbose)
        {
            var path = Config.LocalesFilePath;

            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                if (verbose)
                    Console.WriteLine($"no locales file at {Path.GetFullPath(path ?? "")}, exporting only what is already translated");
                return [];
            }

            var parsed = JToken.Parse(await File.ReadAllTextAsync(path));

            var locales = parsed switch
            {
                JArray array => [.. array.SelectMany(ReadEntry)],
                JObject map => ReadProperties(map),
                _ => new List<LocaleColumn>(),
            };

            if (verbose)
                Console.WriteLine($"loaded {locales.Count} locale(s) from {Path.GetFullPath(path)}");

            return locales;

            static IEnumerable<LocaleColumn> ReadEntry(JToken entry) => entry switch
            {
                JObject alias => ReadProperties(alias),
                _ => Column(entry.ToString()),
            };

            static List<LocaleColumn> ReadProperties(JObject map)
                => [.. map.Properties().SelectMany(p => Column(p.Name, p.Value.ToString()))];

            static IEnumerable<LocaleColumn> Column(string locale, string? column = null)
            {
                if (string.IsNullOrWhiteSpace(locale))
                    yield break;

                yield return new LocaleColumn(locale, string.IsNullOrWhiteSpace(column) ? locale : column);
            }
        }

        /// <summary>
        /// How much of each language is actually filled in. Without this the csv looks complete the
        /// moment it has columns, and a half translated language is exactly the thing worth seeing.
        /// </summary>
        static void PrintCoverage(List<List<string>> rows, List<LocaleColumn> locales)
        {
            Console.WriteLine();
            Console.WriteLine("filled in:");

            for (int i = 0; i < locales.Count; i++)
            {
                // +1 for the key column
                var filled = rows.Count(r => !string.IsNullOrWhiteSpace(r[i + 1]));
                var note = filled == 0 ? "  <- empty, ready to translate" : "";
                Console.WriteLine($"        {locales[i].Column,-10} {filled,4} of {rows.Count} key(s){note}");
            }
        }

        public override string Name => "export-achievements";

        public override string Description
            => "Exports every Play Games Services achievement into a csv, one row per key and one column per language, ready to be fed to a translation service.";

        public override void PrintHelp()
        {
            Console.WriteLine("export-achievements [--achievements <path-to-achievement-definitions.csv>] [--source-locales <code[,code...]>] [--locales <code[,code...]>] [--languages <code[,code...]>] [--games-project <id>] [-v]");
            Console.WriteLine();
            Console.WriteLine();

            Console.WriteLine("description:");
            CommandLinesUtils.PrintDescription(Description);
            CommandLinesUtils.PrintDescription($"Columns: '{KeyHeader}', then one column per language. Every achievement contributes two rows, '<achievement_id>{NameSuffix}' and '<achievement_id>{DescriptionSuffix}', because a translation service wants one string per row.");
            CommandLinesUtils.PrintDescription("Google never machine translates achievements the way it does the store page, so whatever is not in here is English for everyone.");
            CommandLinesUtils.PrintDescription("Every language the tool can see gets a column. The source locales only decide what comes first, they never narrow anything down: source locales, then everything already translated, then the extra locales, then whatever --languages adds for this run.");
            CommandLinesUtils.PrintDescription("The leading columns are what a translation service reads as its context, so 'en-US, uk, ru' gives it english as the source and ukrainian as the second opinion.");
            CommandLinesUtils.PrintDescription("Play Games Services hides a language until something is translated into it, and its api has no language list at all. So a language you added in the console and have not filled in yet is invisible here until you name it in 'Locales' or --languages. That empty column is the work.");
            CommandLinesUtils.PrintDescription("Exported from the draft version, the one the console edits, falling back to the published one for an achievement never touched since it went live. Points, type, steps and icons are not exported and never change.");
            CommandLinesUtils.PrintDescription("Rows are in the console's own order, by sort rank. An existing csv at the target path is overwritten.");

            Console.WriteLine();
            Console.WriteLine("options:");

            CommandLinesUtils.PrintOption(
                "--achievements <path>",
                "Specifies path to the csv to write. If not specified, used path from global config json ('AchievementDefinitionsFilePath'), which defaults to './achievement-definitions.csv' next to it."
            );
            CommandLinesUtils.PrintOption(
                "--source-locales <code[,code...]>",
                "Locales that lead the columns, in this exact order, always exported even when empty. Default is the list from global config.json ('SourceLocales'), and without one the single 'DefaultLanguageCode' leads."
            );
            CommandLinesUtils.PrintOption(
                "--locales <code[,code...]>",
                "Every locale to produce a column for, on top of whatever is already translated. Default is the list from global config.json ('Locales'). This is where a language that exists in the console but is still empty has to be named."
            );
            CommandLinesUtils.PrintOption(
                "--languages <code[,code...]>",
                "Same as --locales but meant for one run, appended last. Use the codes Play Games Services itself uses, see the 'locales' command - they are not always the ones the store page uses."
            );
            CommandLinesUtils.PrintOption(
                "--games-project <id>",
                "Play Games Services project id, shown in the console next to the game name as 'Project ID'. Default is the id from global config.json."
            );
            CommandLinesUtils.PrintOption(
                "-v",
                "Include additional verbose output"
            );

            CommandLinesUtils.PrintCommonOptions();
        }
    }
}
