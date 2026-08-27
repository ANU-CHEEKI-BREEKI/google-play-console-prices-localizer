using Google.Apis.AndroidPublisher.v3.Data;

namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    /// <summary>
    /// Exports the release notes of one track release into the same csv shape the other exports
    /// write: one row (there is only one string to translate), one column per language.
    ///
    /// Read only. The edit it opens to reach the track is thrown away, so nothing in the console
    /// changes, whichever release is read - a draft or the one players already have.
    /// </summary>
    public class Command_LocalesExportReleaseNotes : Command_LocalesReleaseNotesBase
    {
        public override async Task ExecuteAsync()
        {
            try
            {
                var verbose = Args.HasFlag("-v");

                if (string.IsNullOrWhiteSpace(Package))
                {
                    Console.WriteLine("no package name. specify it in config.json or with --package");
                    return;
                }

                var path = Config.ReleaseNotesFilePath;
                if (string.IsNullOrWhiteSpace(path) || Directory.Exists(path))
                {
                    Console.WriteLine($"[ERROR] '{path}' is not a file to write the csv into.");
                    Console.WriteLine("        set 'ReleaseNotesFilePath' in your config.json, or pass --csv <path>");
                    return;
                }

                var selector = ReleaseSelector("latest");

                Console.WriteLine($"reading the '{TrackName}' track...");

                AppEdit? edit = null;
                try
                {
                    edit = await Service!.Edits.Insert(null, Package).ExecuteAsync();

                    var track = await FindTrack(edit.Id);
                    if (track is null)
                        return;

                    var releases = track.Releases ?? [];
                    var release = SelectRelease(releases, selector);

                    if (release is null)
                    {
                        PrintNoRelease(releases, selector);
                        return;
                    }

                    Console.WriteLine($"release: {Describe(release)}");

                    var notes = (release.ReleaseNotes ?? [])
                        .Where(n => !string.IsNullOrWhiteSpace(n.Language))
                        .ToDictionary(n => n.Language!, n => n.Text ?? "", StringComparer.OrdinalIgnoreCase);

                    var locales = await ResolveLocales(notes.Keys, verbose);

                    if (locales.Count == 0)
                    {
                        Console.WriteLine("the release has no notes yet, and no locales are configured. Nothing to put in the columns.");
                        return;
                    }

                    var comment = $"Release notes for '{release.Name}' in the '{TrackName}' track. Max {NotesLimit} characters.";
                    List<string> row = [NotesKey, comment, .. locales.Select(l => notes.TryGetValue(l.Locale, out var text) ? text : "")];

                    List<string> headers = [LocaleColumns.KeyColumn, LocaleColumns.CommentsColumn, .. locales.Select(LocaleColumns.ColumnName)];

                    await CommandLinesUtils.SaveCsv(path, headers, [row]);

                    Console.WriteLine();
                    Console.WriteLine($"written: {Path.GetFullPath(path)}");
                    Console.WriteLine($"1 key in {locales.Count} language(s): {string.Join(", ", locales)}");

                    PrintCoverage(row, locales);
                }
                finally
                {
                    await DiscardEdit(edit, verbose);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        /// <summary>
        /// which languages actually carry notes. A single row makes this short, and an empty column
        /// is exactly the language waiting to be translated
        /// </summary>
        static void PrintCoverage(List<string> row, List<LocaleColumn> locales)
        {
            Console.WriteLine();
            Console.WriteLine("filled in:");

            for (int i = 0; i < locales.Count; i++)
            {
                // +2 for the key and comment columns
                var text = row[i + 2];
                var note = string.IsNullOrWhiteSpace(text)
                    ? "  <- empty, ready to translate"
                    : text.Length > NotesLimit
                        ? $"  <- {text.Length} characters, over the {NotesLimit} limit"
                        : "";
                Console.WriteLine($"        {locales[i].Column,-10} {text.Length,4} character(s){note}");
            }
        }

        public override string Name => "locales export release-notes";

        public override string Description
            => "Exports the release notes of one track release into a csv, one column per language, ready to be fed to a translation service.";

        public override void PrintHelp()
        {
            Console.WriteLine("locales export release-notes [--track <name>] [--release draft|latest|live|<versionCode>] [--csv <path>] [--source-locales <code[,code...]>] [--locales-file <path>] [--locales <code[,code...]>] [--all-locales] [-v]");
            Console.WriteLine();
            Console.WriteLine();

            Console.WriteLine("description:");
            CommandLinesUtils.PrintDescription(Description);
            CommandLinesUtils.PrintDescription($"Columns: '{LocaleColumns.KeyColumn}', '{LocaleColumns.CommentsColumn}', then one column per language named 'English (United States)(en-US)' - the locale code in the trailing parentheses is what the import reads. There is exactly one row, '{NotesKey}', because a release has exactly one string to translate.");
            CommandLinesUtils.PrintDescription("Read only: the edit it opens to reach the track is discarded, nothing in the console changes.");
            CommandLinesUtils.PrintDescription("By default only languages the release already has notes in get a column, in the order the locales json names them. The source locales lead and are always there, even empty. --all-locales adds a column for every entry of the locales json - remember Google only accepts notes in languages the store listing has.");
            CommandLinesUtils.PrintDescription("An existing csv at the target path is overwritten.");

            Console.WriteLine();
            Console.WriteLine("options:");

            CommandLinesUtils.PrintOption(
                "--track <name>",
                "Track to read: production, beta, alpha, internal, or a custom track name. Default is production."
            );
            CommandLinesUtils.PrintOption(
                "--release draft|latest|live|<versionCode>",
                "Which release in the track: 'draft' is the one being prepared, 'live' is the newest one players can have, 'latest' is the newest of them all, a number picks by version code. Default is latest."
            );
            CommandLinesUtils.PrintOption(
                "--csv <path>",
                "Specifies path to the csv to write. If not specified, used path from global config json ('ReleaseNotesFilePath'), which defaults to './release-notes.csv' next to it."
            );
            CommandLinesUtils.PrintOption(
                "--source-locales <code[,code...]>",
                "Locales that lead the columns, in this exact order, always exported even when empty. Default is the list from global config.json ('SourceLocales'), and without one the single 'DefaultLanguageCode' leads."
            );
            CommandLinesUtils.PrintOption(
                "--locales-file <path>",
                "Specifies path to the json that orders the columns and, with --all-locales, names the extra ones. Default is the path from global config json ('LocalesFilePath'), './locales.json' next to it."
            );
            CommandLinesUtils.PrintOption(
                "--locales <code[,code...]>",
                "Locales to export columns for, for this run only, empty or not. Overrides the whole locales json file."
            );
            CommandLinesUtils.PrintOption(
                "--all-locales",
                "Also produce a column for every locale in the locales json, even ones with no notes yet."
            );
            CommandLinesUtils.PrintOption(
                "-v",
                "Include additional verbose output"
            );

            CommandLinesUtils.PrintCommonOptions();
        }
    }
}
