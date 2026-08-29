using Google.Apis.AndroidPublisher.v3.Data;

namespace ANU.APIs.GoogleDeveloperAPI.IAPManaging
{
    /// <summary>
    /// Writes a translated store listing csv back into the store page, one Listings.Update per
    /// changed language, all inside one edit that is committed at the end - or discarded on a dry
    /// run and on every failure, so a half done import never reaches players.
    ///
    /// A language the store does not have yet is created by this very write: for Google a store
    /// language exists because a listing for it exists. A new language therefore needs all three
    /// texts at once, and one that would arrive incomplete is dropped with a warning instead of
    /// being rejected by the api.
    /// </summary>
    public class Command_LocalesImportListing : Command_LocalesListingBase
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

                var path = Config.ListingTranslationsFilePath;
                if (!File.Exists(path))
                {
                    Console.WriteLine($"[ERROR] no csv at {Path.GetFullPath(path)}");
                    Console.WriteLine("        run 'locales export listing' first, or pass --csv <path>");
                    return;
                }

                var csv = await Translations.LoadAsync(path, await LoadLocalesFile(verbose), verbose);

                var rows = csv.Rows.Where(r => string.Equals(r.Id, ListingId, StringComparison.Ordinal)).ToList();

                foreach (var stranger in csv.Rows.Except(rows))
                    Console.WriteLine($"Warning: key '{stranger.Key}' is not a '{ListingId}.*' key, skipped.");

                if (rows.Count == 0)
                {
                    Console.WriteLine($"the csv has no '{ListingId}.*' rows to import.");
                    return;
                }

                Console.WriteLine($"read {rows.Count} key(s) in {csv.Locales.Count} language(s) from {Path.GetFullPath(path)}");
                Console.WriteLine("reading the store listing...");

                AppEdit? edit = null;
                var committed = false;
                try
                {
                    edit = await Service!.Edits.Insert(null, Package).ExecuteAsync();

                    var listings = ((await Service.Edits.Listings.List(Package, edit.Id).ExecuteAsync()).Listings ?? [])
                        .Where(l => !string.IsNullOrWhiteSpace(l.Language))
                        .ToDictionary(l => l.Language!, StringComparer.OrdinalIgnoreCase);

                    Console.WriteLine($"the store listing has {listings.Count} language(s)");

                    var changed = Merge(listings, rows, verbose);

                    Console.WriteLine();
                    Console.WriteLine($"{changed.Count} language(s) to update, {csv.Locales.Count - changed.Count} already up to date or empty.");

                    if (changed.Count == 0)
                        return;

                    if (dryRun)
                    {
                        Console.WriteLine();
                        Console.WriteLine("dry run, nothing was sent:");
                        foreach (var listing in changed)
                            Console.WriteLine($"        {listing.Language,-10} \"{listing.Title}\"");
                        return;
                    }

                    Console.WriteLine();
                    var failed = new List<string>();

                    foreach (var listing in changed)
                    {
                        Console.WriteLine($"   -> updating [{listing.Language}]...");

                        try
                        {
                            await Service.Edits.Listings.Update(listing, Package, edit.Id, listing.Language).ExecuteAsync();
                        }
                        catch (Google.GoogleApiException ex)
                        {
                            // one refused language must not sink the rest: nothing is published
                            // until the commit, so the ones that went through are still pending
                            Console.WriteLine($"      [ERROR] Google refused [{listing.Language}]: {ex.Message.Trim()}");
                            failed.Add(listing.Language!);
                        }
                    }

                    if (failed.Count == changed.Count)
                    {
                        Console.WriteLine();
                        Console.WriteLine("every language was refused, nothing to commit.");
                        return;
                    }

                    Console.WriteLine();
                    Console.WriteLine("   -> committing the edit...");

                    await CommitEdit(edit.Id);
                    committed = true;

                    Console.WriteLine();
                    Console.WriteLine($"updated the store listing in {changed.Count - failed.Count} language(s). Nothing else was part of the edit.");
                    PrintCommitted();

                    if (failed.Count > 0)
                        Console.WriteLine($"{failed.Count} language(s) were NOT imported: {string.Join(", ", failed)}");
                }
                catch (Google.GoogleApiException ex)
                {
                    Console.WriteLine();
                    Console.WriteLine("[ERROR] Google rejected the edit, nothing was published:");
                    Console.WriteLine($"        {ex.Message.Trim()}");

                    PrintCommitHint(ex);
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
        /// Puts the csv values into the listings, and answers which languages actually changed. An
        /// empty cell was dropped at parse time and never wipes anything, a value equal to what is
        /// already there is not a change, and over the length limit is not sent at all.
        ///
        /// A language new to the store must come out of the merge with all three texts, because the
        /// write is what creates it - one that would arrive incomplete is dropped with a warning.
        /// </summary>
        List<Listing> Merge(Dictionary<string, Listing> listings, List<TranslationRow> rows, bool verbose)
        {
            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                if (row.Field is not (TitleField or ShortField or FullField or VideoField))
                {
                    Console.WriteLine($"Warning: '{row.Key}' is not a store listing field, skipped.");
                    continue;
                }

                var limit = LimitOf(row.Field);

                foreach (var (locale, value) in row.Values)
                {
                    if (value.Length > limit)
                    {
                        Console.WriteLine($"Warning: {row.Key} [{locale}] is {value.Length} characters, the limit is {limit}. Not sent.");
                        continue;
                    }

                    if (!listings.TryGetValue(locale, out var listing))
                    {
                        listing = new Listing { Language = locale };
                        listings[locale] = listing;
                    }

                    var current = row.Field switch
                    {
                        TitleField => listing.Title,
                        ShortField => listing.ShortDescription,
                        FullField => listing.FullDescription,
                        _ => listing.Video,
                    };

                    if (string.Equals(current, value, StringComparison.Ordinal))
                        continue;

                    switch (row.Field)
                    {
                        case TitleField: listing.Title = value; break;
                        case ShortField: listing.ShortDescription = value; break;
                        case FullField: listing.FullDescription = value; break;
                        default: listing.Video = value; break;
                    }

                    changed.Add(locale);

                    if (verbose)
                        Console.WriteLine($"   {row.Key} [{locale}]: {value}");
                }
            }

            // a listing needs all three texts, and only a language created by this very import can
            // come out without them - drop it here where the reason can be said
            var complete = new List<Listing>();

            foreach (var locale in changed)
            {
                var listing = listings[locale];

                if (string.IsNullOrWhiteSpace(listing.Title)
                    || string.IsNullOrWhiteSpace(listing.ShortDescription)
                    || string.IsNullOrWhiteSpace(listing.FullDescription))
                {
                    Console.WriteLine($"Warning: [{locale}] is new to the store and the csv does not fill all of {TitleField}, {ShortField} and {FullField}. Dropped, a new language needs all three.");
                    continue;
                }

                complete.Add(listing);
            }

            return [.. complete.OrderBy(l => l.Language, StringComparer.Ordinal)];
        }

        public override string Name => "locales import listing";

        public override string Description
            => "Writes a translated store listing csv back into the store page. Committed as one edit with nothing else in it.";

        public override void PrintHelp()
        {
            Console.WriteLine("locales import listing [--review] [--csv <path>] [--locales-file <path>] [-n|--dry-run] [-v]");
            Console.WriteLine();
            Console.WriteLine();

            Console.WriteLine("description:");
            CommandLinesUtils.PrintDescription(Description);
            CommandLinesUtils.PrintDescription($"Reads the csv 'locales export listing' writes: one row per field ('{ListingId}.{TitleField}', '{ListingId}.{ShortField}', '{ListingId}.{FullField}', '{ListingId}.{VideoField}'), one column per language.");
            CommandLinesUtils.PrintDescription("An empty cell means 'not translated yet' and is left alone - it never wipes text that is already there. A value identical to what Google already has is not sent, so re-running an unchanged csv does nothing.");
            CommandLinesUtils.PrintDescription("Column headers are mapped back through the locales json, so a column the export wrote as 'id-ID' still reaches the api as 'id'.");
            CommandLinesUtils.PrintDescription($"A language the store does not have yet is created by this very write, so it must bring all three texts at once - one that would arrive incomplete is dropped with a warning. Same for an app name over {TitleLimit} characters, a short description over {ShortLimit} or a full one over {FullLimit}.");
            CommandLinesUtils.PrintDescription("Everything goes as one edit, committed at the end: on -n/--dry-run and on every failure the edit is discarded and nothing reaches players. The commit is a draft by default - the changes wait in the Play Console (Publishing overview) until a human sends them for review. Only --review sends them right away.");

            Console.WriteLine();
            Console.WriteLine("options:");

            CommandLinesUtils.PrintOption(
                "--review",
                "Send the changes straight to Google review instead of leaving them as a Play Console draft. After approval they go live on their own."
            );
            CommandLinesUtils.PrintOption(
                "--csv <path>",
                "Specifies path to the csv to read. If not specified, used path from global config json ('ListingTranslationsFilePath'), which defaults to './listing-translations.csv' next to it."
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
