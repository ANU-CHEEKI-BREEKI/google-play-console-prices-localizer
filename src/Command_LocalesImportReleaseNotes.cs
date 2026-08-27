using Google.Apis.AndroidPublisher.v3.Data;

namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    /// <summary>
    /// Writes a translated release notes csv back into one track release.
    ///
    /// The default target is the draft - the release being prepared, the one nobody has yet. A
    /// release players can already have is refused unless --live is passed explicitly, because
    /// committing to it re-publishes the very same bundle with the new notes: harmless, but never
    /// something to do by accident.
    /// </summary>
    public class Command_LocalesImportReleaseNotes : Command_LocalesReleaseNotesBase
    {
        public override async Task ExecuteAsync()
        {
            try
            {
                var verbose = Args.HasFlag("-v");
                var dryRun = Args.HasFlag("-n") || Args.HasFlag("--dry-run");

                if (string.IsNullOrWhiteSpace(Package))
                {
                    Console.WriteLine("no package name. specify it in config.json or with --package");
                    return;
                }

                var path = Config.ReleaseNotesFilePath;
                if (!File.Exists(path))
                {
                    Console.WriteLine($"[ERROR] no csv at {Path.GetFullPath(path)}");
                    Console.WriteLine("        run 'locales export release-notes' first, or pass --csv <path>");
                    return;
                }

                var csv = await Translations.LoadAsync(path, await LoadLocalesFile(verbose), verbose);

                var rows = csv.Rows.Where(r => r.Field == NotesField).ToList();

                if (rows.Count == 0)
                {
                    Console.WriteLine($"the csv has no '{NotesKey}' row to import.");
                    return;
                }

                if (rows.Count > 1)
                    Console.WriteLine($"Warning: the csv has {rows.Count} '.{NotesField}' rows, only the first one ('{rows[0].Key}') is imported.");

                var row = rows[0];

                Console.WriteLine($"read notes in {row.Values.Count} language(s) from {Path.GetFullPath(path)}");

                var selector = ReleaseSelector("draft");

                Console.WriteLine($"reading the '{TrackName}' track...");

                AppEdit? edit = null;
                var committed = false;
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

                        if (string.Equals(selector, "draft", StringComparison.OrdinalIgnoreCase))
                            Console.WriteLine("draft is the default target on purpose: it is the only release nobody has yet.");

                        return;
                    }

                    Console.WriteLine($"release: {Describe(release)}");

                    // the guard this command exists around: a release players can have is only
                    // written when --live says so in as many words
                    if (IsLive(release) && !Args.HasFlag("--live"))
                    {
                        Console.WriteLine();
                        Console.WriteLine($"[STOP] this release is '{release.Status}' - players can already have it. Nothing was written.");
                        Console.WriteLine("       writing its notes re-publishes the same bundle with the new text, which is safe but deliberate.");
                        Console.WriteLine("       re-run with --live to do exactly that, or target the draft release instead.");
                        return;
                    }

                    var edits = Merge(release, row, verbose);

                    Console.WriteLine();
                    Console.WriteLine($"{edits} language(s) to update, {row.Values.Count - edits} already up to date.");

                    if (edits == 0)
                        return;

                    if (dryRun)
                    {
                        Console.WriteLine();
                        Console.WriteLine("dry run, nothing was sent.");
                        return;
                    }

                    Console.WriteLine();
                    Console.WriteLine($"   -> updating '{release.Name}' and committing the edit...");

                    await Service.Edits.Tracks.Update(track, Package, edit.Id, track.TrackValue ?? TrackName).ExecuteAsync();

                    var commit = Service.Edits.Commit(Package, edit.Id);
                    if (Args.HasFlag("--no-review"))
                        commit.ChangesNotSentForReview = true;

                    await commit.ExecuteAsync();
                    committed = true;

                    Console.WriteLine();
                    Console.WriteLine($"updated the notes of '{release.Name}' in {edits} language(s). Nothing else was part of the edit.");
                }
                catch (Google.GoogleApiException ex)
                {
                    Console.WriteLine();
                    Console.WriteLine("[ERROR] Google rejected the edit, nothing was published:");
                    Console.WriteLine($"        {ex.Message.Trim()}");

                    if (ex.Message.Contains("changesNotSentForReview", StringComparison.OrdinalIgnoreCase))
                        Console.WriteLine("        Google asks for the opposite setting of --no-review here: add or drop that flag and re-run.");
                }
                finally
                {
                    // a committed edit is gone on its own, only an abandoned one needs discarding
                    if (!committed)
                        await DiscardEdit(edit, verbose);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        /// <summary>
        /// Puts the csv values into the release notes, and answers how many languages actually
        /// changed. An empty cell was dropped at parse time and never wipes anything, a value equal
        /// to what is already there is not a change, and over the length limit is not sent at all.
        /// </summary>
        int Merge(TrackRelease release, TranslationRow row, bool verbose)
        {
            release.ReleaseNotes ??= [];

            var notes = release.ReleaseNotes
                .Where(n => !string.IsNullOrWhiteSpace(n.Language))
                .ToDictionary(n => n.Language!, StringComparer.OrdinalIgnoreCase);

            var edits = 0;

            foreach (var (locale, value) in row.Values)
            {
                if (value.Length > NotesLimit)
                {
                    Console.WriteLine($"Warning: [{locale}] is {value.Length} characters, the limit is {NotesLimit}. Not sent.");
                    continue;
                }

                if (!notes.TryGetValue(locale, out var note))
                {
                    note = new LocalizedText { Language = locale };
                    notes[locale] = note;
                    release.ReleaseNotes.Add(note);
                }

                if (string.Equals(note.Text, value, StringComparison.Ordinal))
                    continue;

                note.Text = value;
                edits++;

                if (verbose)
                    Console.WriteLine($"   [{locale}]: {value}");
            }

            return edits;
        }

        public override string Name => "locales import release-notes";

        public override string Description
            => "Writes a translated release notes csv back into one track release. The draft by default, a released version only with --live.";

        public override void PrintHelp()
        {
            Console.WriteLine("locales import release-notes [--track <name>] [--release draft|latest|live|<versionCode>] [--live] [--no-review] [--csv <path>] [--locales-file <path>] [-n|--dry-run] [-v]");
            Console.WriteLine();
            Console.WriteLine();

            Console.WriteLine("description:");
            CommandLinesUtils.PrintDescription(Description);
            CommandLinesUtils.PrintDescription($"Reads the csv 'locales export release-notes' writes: one '{NotesKey}' row, one column per language.");
            CommandLinesUtils.PrintDescription("An empty cell means 'not translated yet' and is left alone - it never wipes notes that are already there. A value identical to what Google already has is not sent, so re-running an unchanged csv does nothing.");
            CommandLinesUtils.PrintDescription($"Column headers are mapped back through the locales json, so a column the export wrote as 'id-ID' still reaches the api as 'id'. Notes over {NotesLimit} characters are skipped with a warning instead of being rejected by Google.");
            CommandLinesUtils.PrintDescription("The languages of the notes must exist in the store listing - Google rejects a language the app's store page does not have.");
            CommandLinesUtils.PrintDescription("A release players can already have ('inProgress', 'halted' or 'completed') is refused without --live. Writing it re-publishes the very same bundle with the new notes - safe, but always deliberate.");

            Console.WriteLine();
            Console.WriteLine("options:");

            CommandLinesUtils.PrintOption(
                "--track <name>",
                "Track to write: production, beta, alpha, internal, or a custom track name. Default is production."
            );
            CommandLinesUtils.PrintOption(
                "--release draft|latest|live|<versionCode>",
                "Which release in the track: 'draft' is the one being prepared, 'live' is the newest one players can have, 'latest' is the newest of them all, a number picks by version code. Default is draft."
            );
            CommandLinesUtils.PrintOption(
                "--live",
                "Allow writing into a release players can already have. Without it such a release is refused."
            );
            CommandLinesUtils.PrintOption(
                "--no-review",
                "Commit the edit with changesNotSentForReview, for apps where Google demands changes be sent for review manually. Only add it when Google's own error asks for it."
            );
            CommandLinesUtils.PrintOption(
                "--csv <path>",
                "Specifies path to the csv to read. If not specified, used path from global config json ('ReleaseNotesFilePath'), which defaults to './release-notes.csv' next to it."
            );
            CommandLinesUtils.PrintOption(
                "--locales-file <path>",
                "Specifies path to the json that maps a column name back to its locale code. Default is the path from global config json ('LocalesFilePath'), './locales.json' next to it."
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
